"""No production I/O: signing, privacy, multipart, integrity and local safety."""
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import tempfile
import time
import unittest
from unittest.mock import patch
import urllib.error
import xml.etree.ElementTree as ET

SPEC = importlib.util.spec_from_file_location("native_backup", Path(__file__).with_name("NativePostgresBackup.py"))
native = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(native)


def config():
    return {"endpoint": "https://s3.example.test", "region": "test", "bucket": "test-bucket",
            "prefix": "native-backups", "access_key": "TEST_ACCESS", "secret_key": "TEST_SECRET",
            "container": "test-db", "allow_unencrypted": True}


class Response(io.BytesIO):
    def __init__(self, content=b"", headers=None):
        super().__init__(content)
        self.headers = headers or {}


class FakeS3(native.S3):
    def __init__(self, responses):
        super().__init__(config())
        self.responses = list(responses)
        self.calls = []

    def open(self, method, key, **kwargs):
        self.calls.append((method, key, kwargs))
        value = self.responses.pop(0)
        if isinstance(value, Exception):
            raise value
        return value


class NativeBackupTests(unittest.TestCase):
    def test_configuration_requires_opt_in_and_safe_endpoint_and_prefix(self):
        native.validate_config(config())
        invalid = [("allow_unencrypted", False), ("endpoint", "http://s3.example.test"),
                   ("endpoint", "https://user:pass@s3.example.test"), ("endpoint", "https://s3.example.test/path"),
                   ("prefix", "../other"), ("prefix", "x//y"), ("container", "-oops"),
                   ("access_key", ""), ("retention_days", 0), ("max_archive_bytes", 3 * 1024 ** 3)]
        for name, value in invalid:
            with self.subTest(name=name, value=value), self.assertRaises(native.BackupError):
                native.validate_config({**config(), name: value})

    def test_signing_encodes_and_sorts_query_without_secrets_in_url(self):
        request = native.S3(config()).request("PUT", "native-backups/a b.backup", b"payload",
                                              query={"uploadId": "a+/=", "partNumber": 2})
        self.assertEqual(request.full_url, "https://s3.example.test/test-bucket/native-backups/a%20b.backup?partNumber=2&uploadId=a%2B%2F%3D")
        self.assertEqual(request.get_header("X-amz-content-sha256"), hashlib.sha256(b"payload").hexdigest())
        self.assertIn("/test/s3/aws4_request", request.get_header("Authorization"))
        self.assertNotIn("TEST_SECRET", str(request.headers))

    def test_provider_error_is_sanitized(self):
        opener = unittest.mock.Mock()
        opener.open.side_effect = urllib.error.HTTPError("https://SECRET", 500, "TOKEN", {}, None)
        with self.assertRaisesRegex(native.BackupError, "^s3_http_500$"):
            native.S3(config(), opener).open("GET", "a")

    def test_privacy_probe_denied_and_exact_cleanup(self):
        for status in (403, 404):
            storage = FakeS3([Response(), Response(b"x" * 32), native.BackupError("s3_http_" + str(status)), Response()])
            with patch.object(native.os, "urandom", side_effect=lambda size: b"x" * size):
                storage.privacy_probe()
            self.assertEqual([x[0] for x in storage.calls], ["PUT", "GET", "GET", "DELETE"])
            self.assertEqual(len({x[1] for x in storage.calls}), 1)
            self.assertFalse(storage.calls[2][2]["signed"])

    def test_public_bucket_and_ambiguous_anonymous_response_fail_closed(self):
        for anonymous in (Response(b"x" * 32), native.BackupError("s3_http_500"), native.BackupError("s3_network_outcome_unknown")):
            storage = FakeS3([Response(), Response(b"x" * 32), anonymous, Response()])
            with patch.object(native.os, "urandom", side_effect=lambda size: b"x" * size), self.assertRaises(native.BackupError):
                storage.privacy_probe()
            self.assertEqual(storage.calls[-1][0], "DELETE")

    def test_corrupt_probe_is_not_accepted(self):
        storage = FakeS3([Response(), Response(b"bad"), Response()])
        with self.assertRaisesRegex(native.BackupError, "privacy_probe_integrity_failed"):
            storage.privacy_probe()

    def test_multipart_conditional_completion_checksums_and_no_object_delete(self):
        storage = FakeS3([Response(b"<Result><UploadId>own</UploadId></Result>"),
                         Response(headers={"ETag": '"one"'}), Response(headers={"ETag": '"two"'}),
                         Response(b"<CompleteMultipartUploadResult/>")])
        with tempfile.TemporaryDirectory() as folder, patch.object(native, "PART_BYTES", 4):
            path = Path(folder) / "a.backup"
            path.write_bytes(b"abcdefgh")
            storage.upload(path, "native-backups/a.backup", 8, "hash")
        self.assertEqual([x[0] for x in storage.calls], ["POST", "PUT", "PUT", "POST"])
        completion = storage.calls[-1][2]
        self.assertEqual(completion["headers"]["if-none-match"], "*")
        root = ET.fromstring(completion["body"])
        self.assertEqual(len(root), 2)
        self.assertEqual(native.xml_value(completion["body"], "ChecksumSHA256"), storage.calls[1][2]["headers"]["x-amz-checksum-sha256"])

    def test_part_failure_and_embedded_200_error_abort_only_owned_upload(self):
        for responses in ([native.BackupError("s3_http_500")],
                          [Response(headers={"ETag": "one"}), Response(b"<Error><Code>InternalError</Code></Error>")]):
            storage = FakeS3([Response(b"<Result><UploadId>own</UploadId></Result>"), *responses, Response()])
            with tempfile.TemporaryDirectory() as folder:
                path = Path(folder) / "a.backup"
                path.write_bytes(b"PGDMP")
                with self.assertRaises(native.BackupError):
                    storage.upload(path, "native-backups/a.backup", 5, "hash")
            self.assertEqual(storage.calls[-1][0], "DELETE")
            self.assertEqual(storage.calls[-1][2]["query"], {"uploadId": "own"})

    def test_failed_abort_reports_unknown_and_does_not_delete_object(self):
        storage = FakeS3([Response(b"<Result><UploadId>own</UploadId></Result>"), native.BackupError("s3_http_500"), native.BackupError("s3_http_500")])
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "a.backup"
            path.write_bytes(b"PGDMP")
            with self.assertRaisesRegex(native.BackupError, "multipart_abort_outcome_unknown"):
                storage.upload(path, "native-backups/a.backup", 5, "hash")

    def test_verified_get_can_materialize_exact_bytes(self):
        body = b"PGDMP-content"
        digest = hashlib.sha256(body).hexdigest()
        storage = FakeS3([Response(headers={"Content-Length": str(len(body)), "x-amz-meta-sha256": digest}),
                         Response(body, {"Content-Length": str(len(body))})])
        output = io.BytesIO()
        storage.verify("a", len(body), digest, output)
        self.assertEqual(output.getvalue(), body)

    def test_head_mismatch_and_get_corruption_or_truncation_fail(self):
        body = b"PGDMP"
        digest = hashlib.sha256(body).hexdigest()
        cases = [[Response(headers={"Content-Length": "5", "x-amz-meta-sha256": "bad"})],
                 [Response(headers={"Content-Length": "5", "x-amz-meta-sha256": digest}), Response(b"BAD!!", {"Content-Length": "5"})],
                 [Response(headers={"Content-Length": "5", "x-amz-meta-sha256": digest}), Response(b"PG", {"Content-Length": "5"})]]
        for responses in cases:
            with self.assertRaises(native.BackupError):
                FakeS3(responses).verify("a", 5, digest)

    @unittest.skipUnless(os.name == "posix", "POSIX permissions verified on Linux CI/VPS")
    def test_private_path_permissions_and_symlinks(self):
        with tempfile.TemporaryDirectory() as folder:
            directory = Path(folder).resolve()
            native.private_path(directory, directory=True)
            file = directory / "config"
            file.write_text("{}")
            file.chmod(0o600)
            native.private_path(file)
            link = directory / "link"
            link.symlink_to(file)
            with self.assertRaises(native.BackupError): native.private_path(link)
            file.chmod(0o644)
            with self.assertRaises(native.BackupError): native.private_path(file)

    @unittest.skipUnless(os.name == "posix", "POSIX archive execution verified on Linux CI/VPS")
    def test_archive_failures_do_not_publish_and_success_is_private(self):
        with tempfile.TemporaryDirectory() as folder:
            directory = Path(folder)
            def dump(command, **kwargs):
                if "pg_restore" not in command:
                    kwargs["stdout"].write(b"PGDMP-content")
                else:
                    self.assertEqual(os.read(kwargs["stdin"].fileno(), 5), b"PGDMP")
                return subprocess_result
            subprocess_result = unittest.mock.Mock(returncode=0)
            with patch.object(native.subprocess, "run", side_effect=dump), patch.object(native.shutil, "disk_usage", return_value=unittest.mock.Mock(free=10 * 1024**3)):
                path = native.create_archive(config(), directory)
                self.assertEqual(path.stat().st_mode & 0o777, 0o600)
                self.assertEqual(path.read_bytes(), b"PGDMP-content")
                subprocess_result.returncode = 1
                with self.assertRaises(native.BackupError): native.create_archive(config(), directory)
            self.assertEqual(len(list(directory.glob("*.backup"))), 1)
            self.assertEqual(len(list(directory.glob(".partial-*"))), 0)

    def test_retention_only_prunes_old_verified_owned_archives(self):
        with tempfile.TemporaryDirectory() as folder:
            directory = Path(folder)
            body = b"PGDMP"
            old = directory / ("proxyharbor-postgres-20260101T000000Z-" + "a"*32 + ".backup")
            old.write_bytes(body)
            old.with_suffix(".json").write_text(json.dumps({"file":old.name,"verified_get":True,"sha256":hashlib.sha256(body).hexdigest()}))
            unverified = directory / ("proxyharbor-postgres-20260101T000000Z-" + "b"*32 + ".backup")
            unverified.write_bytes(body)
            unrelated = directory / "other.backup"
            unrelated.write_bytes(body)
            for path in (old, unverified, unrelated): os.utime(path,(time.time()-20*86400,time.time()-20*86400))
            native.prune_verified(directory,7,None)
            self.assertFalse(old.exists())
            self.assertTrue(unverified.exists())
            self.assertTrue(unrelated.exists())

    @unittest.skipUnless(os.name == "posix", "Linux lock and permissions")
    def test_overlapping_job_is_rejected_before_provider_io(self):
        import fcntl
        with tempfile.TemporaryDirectory() as folder:
            directory = Path(folder).resolve()
            settings = directory / "config.json"
            settings.write_text(json.dumps({**config(), "directory": str(directory)}))
            settings.chmod(0o600)
            with (directory / ".run.lock").open("w") as lock, patch.object(native, "S3") as storage:
                fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
                with self.assertRaisesRegex(native.BackupError, "already_running"):
                    native.run(settings)
                storage.assert_not_called()

    @unittest.skipUnless(os.name == "posix", "Linux service configuration")
    def test_failed_verify_preserves_archive_and_previous_success(self):
        with tempfile.TemporaryDirectory() as folder:
            directory = Path(folder).resolve()
            settings = directory / "config.json"
            settings.write_text(json.dumps({**config(), "directory": str(directory)}))
            settings.chmod(0o600)
            previous = directory / "last-success.json"
            previous.write_text('{"previous":true}')
            path = directory / "owned.backup"
            path.write_bytes(b"PGDMP")
            storage = unittest.mock.Mock()
            storage.verify.side_effect = native.BackupError("get_integrity_mismatch")
            with patch.object(native, "S3", return_value=storage), patch.object(native, "create_archive", return_value=path):
                with self.assertRaises(native.BackupError): native.run(settings)
            self.assertEqual(previous.read_text(), '{"previous":true}')
            self.assertTrue(path.exists())
            self.assertFalse(path.with_suffix(".json").exists())


if __name__ == "__main__":
    unittest.main()
