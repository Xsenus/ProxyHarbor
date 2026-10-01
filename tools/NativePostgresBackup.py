#!/usr/bin/env python3
"""Private native PostgreSQL archives, independently verified in S3 (stdlib only)."""

import argparse
import base64
import datetime as dt
import hashlib
import hmac
import json
import os
from pathlib import Path
import re
import shutil
import stat
import subprocess
import tempfile
import urllib.error
import urllib.parse
import urllib.request
import uuid
import xml.etree.ElementTree as ET

PART_BYTES = 16 * 1024 * 1024
ARCHIVE_NAME = re.compile(r"proxyharbor-postgres-\d{8}T\d{6}Z-[0-9a-f]{32}\.backup")


class BackupError(Exception):
    """Safe operator-facing failure; never include secrets or provider bodies."""


def private_path(path, directory=False):
    path = Path(path)
    if not path.is_absolute() or path.resolve() != path or path.is_symlink():
        raise BackupError("unsafe_path")
    info = path.stat()
    if info.st_uid != os.geteuid() or info.st_mode & 0o077:
        raise BackupError("private_owner_and_permissions_required")
    if not (stat.S_ISDIR(info.st_mode) if directory else stat.S_ISREG(info.st_mode)):
        raise BackupError("unexpected_path_type")
    return path


def validate_config(config):
    endpoint = urllib.parse.urlsplit(config["endpoint"])
    if (endpoint.scheme != "https" or not endpoint.hostname or endpoint.username
            or endpoint.password or endpoint.query or endpoint.fragment
            or endpoint.path not in ("", "/")):
        raise BackupError("https_endpoint_required")
    for name in ("region", "access_key", "secret_key"):
        if not isinstance(config.get(name), str) or not config[name].strip():
            raise BackupError("missing_configuration")
    if not re.fullmatch(r"[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]", config["bucket"]):
        raise BackupError("invalid_bucket")
    if not re.fullmatch(r"[A-Za-z0-9_-]+(?:/[A-Za-z0-9_-]+)*", config["prefix"]):
        raise BackupError("invalid_prefix")
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]*", config["container"]):
        raise BackupError("invalid_container")
    if config.get("allow_unencrypted") is not True:
        raise BackupError("explicit_unencrypted_opt_in_required")
    if not 1 <= config.get("retention_days", 7) <= 365:
        raise BackupError("invalid_retention")
    if not 1 <= config.get("max_archive_bytes", 2 * 1024 ** 3) <= 2 * 1024 ** 3:
        raise BackupError("invalid_archive_limit")
    return config


def xml_value(data, name):
    root = ET.fromstring(data)
    for item in root.iter():
        if item.tag.split("}")[-1] == name:
            return item.text
    raise BackupError("invalid_s3_xml")


class S3:
    def __init__(self, config, opener=None):
        self.config = config
        # No redirect/retry after PUT/Complete: an ambiguous write needs inspection.
        class NoRedirect(urllib.request.HTTPRedirectHandler):
            def redirect_request(self, *args, **kwargs):
                return None
        self.opener = opener or urllib.request.build_opener(NoRedirect())

    def request(self, method, key, body=b"", query=None, headers=None, signed=True):
        endpoint = urllib.parse.urlsplit(self.config["endpoint"])
        path = "/" + self.config["bucket"] + "/" + urllib.parse.quote(key, safe="/~")
        query_string = "&".join(sorted(
            urllib.parse.quote(str(k), safe="~") + "=" + urllib.parse.quote(str(v), safe="~")
            for k, v in (query or {}).items()))
        supplied = {k.lower(): str(v).strip() for k, v in (headers or {}).items()}
        if signed:
            stamp = dt.datetime.now(dt.timezone.utc).strftime("%Y%m%dT%H%M%SZ")
            day = stamp[:8]
            payload_hash = hashlib.sha256(body).hexdigest()
            supplied.update({"host": endpoint.netloc, "x-amz-date": stamp,
                             "x-amz-content-sha256": payload_hash})
            names = ";".join(sorted(supplied))
            canonical = "\n".join([method, path, query_string,
                "".join(k + ":" + supplied[k] + "\n" for k in sorted(supplied)), names, payload_hash])
            scope = day + "/" + self.config["region"] + "/s3/aws4_request"
            signing_key = ("AWS4" + self.config["secret_key"]).encode()
            for part in (day, self.config["region"], "s3", "aws4_request"):
                signing_key = hmac.new(signing_key, part.encode(), hashlib.sha256).digest()
            signing_text = "AWS4-HMAC-SHA256\n" + stamp + "\n" + scope + "\n" + hashlib.sha256(canonical.encode()).hexdigest()
            signature = hmac.new(signing_key, signing_text.encode(), hashlib.sha256).hexdigest()
            supplied["authorization"] = ("AWS4-HMAC-SHA256 Credential=" + self.config["access_key"]
                + "/" + scope + ", SignedHeaders=" + names + ", Signature=" + signature)
        url = "https://" + endpoint.netloc + path + ("?" + query_string if query_string else "")
        return urllib.request.Request(url, data=body if method in ("PUT", "POST") else None,
                                      headers=supplied, method=method)

    def open(self, method, key, **kwargs):
        try:
            return self.opener.open(self.request(method, key, **kwargs), timeout=120)
        except urllib.error.HTTPError as error:
            raise BackupError("s3_http_" + str(error.code)) from None
        except (OSError, urllib.error.URLError):
            raise BackupError("s3_network_outcome_unknown") from None

    def privacy_probe(self):
        key = self.config["prefix"] + "/.privacy-probe-" + uuid.uuid4().hex
        payload = os.urandom(32)
        created = False
        try:
            with self.open("PUT", key, body=payload, headers={"if-none-match": "*"}):
                created = True
            with self.open("GET", key) as response:
                if response.read(33) != payload:
                    raise BackupError("privacy_probe_integrity_failed")
            try:
                with self.open("GET", key, signed=False) as response:
                    response.read(33)
                raise BackupError("public_bucket_refused")
            except BackupError as error:
                if str(error) not in ("s3_http_403", "s3_http_404"):
                    raise
        finally:
            if created:
                with self.open("DELETE", key):
                    pass

    def upload(self, path, key, size, digest):
        if size > 10_000 * PART_BYTES:
            raise BackupError("multipart_limit_exceeded")
        headers = {"content-type": "application/octet-stream", "x-amz-meta-sha256": digest,
                   "x-amz-meta-format": "PostgreSQL-custom", "x-amz-checksum-algorithm": "SHA256",
                   "x-amz-checksum-type": "COMPOSITE"}
        with self.open("POST", key, query={"uploads": ""}, headers=headers) as response:
            upload_id = xml_value(response.read(1024 * 1024), "UploadId")
        completed = False
        try:
            parts = ET.Element("CompleteMultipartUpload", {"xmlns": "http://s3.amazonaws.com/doc/2006-03-01/"})
            with path.open("rb") as stream:
                number = 0
                while chunk := stream.read(PART_BYTES):
                    number += 1
                    checksum = base64.b64encode(hashlib.sha256(chunk).digest()).decode()
                    with self.open("PUT", key, body=chunk,
                            query={"partNumber": number, "uploadId": upload_id},
                            headers={"x-amz-checksum-sha256": checksum}) as response:
                        etag = response.headers.get("ETag")
                    if not etag:
                        raise BackupError("missing_part_etag")
                    part = ET.SubElement(parts, "Part")
                    ET.SubElement(part, "PartNumber").text = str(number)
                    ET.SubElement(part, "ETag").text = etag
                    ET.SubElement(part, "ChecksumSHA256").text = checksum
            with self.open("POST", key, body=ET.tostring(parts), query={"uploadId": upload_id},
                    headers={"content-type": "application/xml", "if-none-match": "*",
                             "x-amz-checksum-type": "COMPOSITE"}) as response:
                result = ET.fromstring(response.read(1024 * 1024))
            if result.tag.split("}")[-1] != "CompleteMultipartUploadResult":
                raise BackupError("complete_outcome_unknown")
            completed = True
        finally:
            if not completed:
                # Only abort the exact upload started above; never delete a backup object.
                try:
                    with self.open("DELETE", key, query={"uploadId": upload_id}):
                        pass
                except BackupError as error:
                    if str(error) != "s3_http_404":
                        raise BackupError("multipart_abort_outcome_unknown") from None

    def verify(self, key, size, digest, output=None):
        with self.open("HEAD", key) as response:
            if int(response.headers.get("Content-Length", -1)) != size or response.headers.get("x-amz-meta-sha256") != digest:
                raise BackupError("head_integrity_mismatch")
        hasher = hashlib.sha256()
        count = 0
        with self.open("GET", key) as response:
            if int(response.headers.get("Content-Length", -1)) != size:
                raise BackupError("get_size_mismatch")
            while chunk := response.read(1024 * 1024):
                count += len(chunk)
                if count > size:
                    raise BackupError("get_size_mismatch")
                hasher.update(chunk)
                if output is not None:
                    output.write(chunk)
        if count != size or hasher.hexdigest() != digest:
            raise BackupError("get_integrity_mismatch")


def atomic_json(path, data):
    handle, partial = tempfile.mkstemp(prefix=".metadata-", dir=path.parent)
    try:
        with os.fdopen(handle, "w", encoding="utf-8") as stream:
            json.dump(data, stream, ensure_ascii=False)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(partial, path)
    finally:
        if os.path.exists(partial):
            os.unlink(partial)


def create_archive(config, directory):
    import resource
    if shutil.disk_usage(directory).free < 3 * 1024 ** 3:
        raise BackupError("insufficient_disk_reserve")
    name = "proxyharbor-postgres-" + dt.datetime.now(dt.timezone.utc).strftime("%Y%m%dT%H%M%SZ") + "-" + uuid.uuid4().hex + ".backup"
    destination = directory / name
    handle, partial = tempfile.mkstemp(prefix=".partial-", dir=directory)
    try:
        with os.fdopen(handle, "wb") as output:
            command = ['docker', 'exec', config['container'], 'sh', '-c',
                'exec pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" --lock-wait-timeout=30s -Fc']
            maximum = config.get("max_archive_bytes", 2 * 1024 ** 3)
            result = subprocess.run(command, stdout=output, stderr=subprocess.PIPE, timeout=1800,
                preexec_fn=lambda: resource.setrlimit(resource.RLIMIT_FSIZE, (maximum, maximum)))
            output.flush()
            os.fsync(output.fileno())
        if result.returncode or not Path(partial).stat().st_size:
            raise BackupError("pg_dump_failed")
        with open(partial, "rb") as source:
            if source.read(5) != b"PGDMP":
                raise BackupError("not_postgresql_custom_archive")
            source.seek(0)
            result = subprocess.run(['docker', 'exec', '-i', config['container'], 'pg_restore', '--list'],
                stdin=source, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, timeout=120)
        if result.returncode:
            raise BackupError("pg_restore_list_failed")
        os.link(partial, destination)  # exclusive atomic publication, never overwrite
        return destination
    except subprocess.TimeoutExpired:
        raise BackupError("pg_dump_or_validation_timeout") from None
    finally:
        os.unlink(partial)


def prune_verified(directory, retention_days, current):
    cutoff = dt.datetime.now(dt.timezone.utc).timestamp() - retention_days * 86400
    for path in directory.iterdir():
        if path == current or not ARCHIVE_NAME.fullmatch(path.name) or path.is_symlink() or not path.is_file():
            continue
        metadata = path.with_suffix(".json")
        if path.stat().st_mtime >= cutoff or metadata.is_symlink() or not metadata.is_file():
            continue
        try:
            record = json.loads(metadata.read_text())
            if record.get("file") != path.name or record.get("verified_get") is not True:
                continue
            with path.open("rb") as source:
                if hashlib.file_digest(source, "sha256").hexdigest() != record.get("sha256"):
                    continue
            path.unlink()
            metadata.unlink()
        except (OSError, ValueError):
            continue


def run(config_path):
    import fcntl  # Linux service; tests for signing/transport also run on Windows.
    os.umask(0o077)
    config = validate_config(json.loads(private_path(config_path).read_text()))
    directory = private_path(config["directory"], directory=True)
    lock_handle = os.open(directory / ".run.lock", os.O_CREAT | os.O_RDWR | os.O_NOFOLLOW, 0o600)
    with os.fdopen(lock_handle, "w") as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            raise BackupError("already_running") from None
        storage = S3(config)
        storage.privacy_probe()  # no private data written until anonymous access is denied
        path = create_archive(config, directory)
        with path.open("rb") as source:
            digest = hashlib.file_digest(source, "sha256").hexdigest()
        size = path.stat().st_size
        key = config["prefix"] + "/" + path.name
        storage.upload(path, key, size, digest)
        storage.verify(key, size, digest)
        record = {"file": path.name, "object_key": key, "bytes": size, "sha256": digest,
                  "verified_get": True, "completed_at_utc": dt.datetime.now(dt.timezone.utc).isoformat()}
        atomic_json(path.with_suffix(".json"), record)
        atomic_json(directory / "last-success.json", record)
        prune_verified(directory, config.get("retention_days", 7), path)
        print(json.dumps(record))


def main():
    import signal
    def cancel(signum, frame):
        raise BackupError("cancelled")
    signal.signal(signal.SIGTERM, cancel)
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", required=True, help="Absolute path to private JSON config")
    args = parser.parse_args()
    try:
        run(args.config)
    except BackupError as error:
        print(json.dumps({"success": False, "error": str(error)}))
        return 1
    except KeyboardInterrupt:
        print(json.dumps({"success": False, "error": "cancelled"}))
        return 130
    except Exception as error:
        # Do not print exception messages, subprocess stderr, config or URLs.
        print(json.dumps({"success": False, "error_type": type(error).__name__}))
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
