#!/bin/sh
set -eu
tool=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd -P)/Create-PredeployBackup.sh
fixture=$(mktemp -d "${TMPDIR:-/tmp}/proxyharbor-backup-creation-test.XXXXXX")
cleanup() {
  resolved_fixture=$(CDPATH= cd -- "$fixture" 2>/dev/null && pwd -P || true)
  case "$resolved_fixture" in
    "${TMPDIR:-/tmp}"/proxyharbor-backup-creation-test.*)
      find "$resolved_fixture" -mindepth 1 -delete
      rmdir -- "$resolved_fixture" ;;
  esac
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM
mkdir "$fixture/bin" "$fixture/backups"
chmod 700 "$fixture/backups"
cat > "$fixture/bin/docker" <<'EOF'
#!/bin/sh
set -eu
[ "$1" = exec ]
shift
if [ "$1" = -i ]; then
  [ "$2" = test-db ] && [ "$3" = pg_restore ] && [ "$4" = --list ]
  [ "$(cat)" = 'fake archive' ]
  [ "${BACKUP_TEST_MODE:-success}" != restore_failure ]
else
  [ "$1" = test-db ] && [ "$2" = sh ] && [ "$3" = -c ]
  case "$4" in *'--lock-wait-timeout=30s -Fc') ;; *) exit 9 ;; esac
  # Verify permissions before any data is written, even under caller umask 000.
  [ "$(stat -Lc %a /proc/self/fd/1)" = 600 ]
  case "${BACKUP_TEST_MODE:-success}" in
    dump_failure) printf 'partial'; exit 9 ;;
    empty) exit 0 ;;
    *) printf 'fake archive' ;;
  esac
fi
EOF
cat > "$fixture/bin/date" <<'EOF'
#!/bin/sh
printf '20260904T030000Z\n'
EOF
chmod 700 "$fixture/bin/docker" "$fixture/bin/date"
PATH="$fixture/bin:$PATH"
export PATH
run() { sh "$tool" --directory "$fixture/backups" --revision abcdef1 --container test-db; }
no_partials() { [ "$(find "$fixture/backups" -name '.predeploy-partial.*' | wc -l)" -eq 0 ]; }
must_fail() {
  if "$@" > "$fixture/output" 2>&1; then
    printf 'Unexpected success: %s\n' "$*" >&2
    exit 1
  fi
  no_partials
}
umask 000
run > "$fixture/output"
archive="$fixture/backups/predeploy-abcdef1-20260904T030000Z.dump"
[ "$(stat -c %a "$archive")" = 600 ]
[ "$(stat -c %h "$archive")" = 1 ]
[ "$(cat "$archive")" = 'fake archive' ]
no_partials
grep -Fx 'mode=600' "$fixture/output" >/dev/null

# Same timestamp/revision cannot replace either a regular file or a symlink.
printf 'original' > "$archive"
must_fail run
[ "$(cat "$archive")" = original ]
mv "$archive" "$fixture/original"
ln -s "$fixture/original" "$archive"
must_fail run
[ -L "$archive" ] && [ "$(cat "$fixture/original")" = original ]
rm -- "$archive"
for BACKUP_TEST_MODE in dump_failure empty restore_failure; do
  export BACKUP_TEST_MODE
  must_fail run
  [ ! -e "$archive" ]
  [ "$(cat "$fixture/original")" = original ]
done
unset BACKUP_TEST_MODE
must_fail sh "$tool" --directory . --revision abcdef1
must_fail sh "$tool" --directory / --revision abcdef1
must_fail sh "$tool" --directory "$fixture/backups" --revision '../bad'
must_fail sh "$tool" --directory "$fixture/backups" --revision abcdef1 --container=-bad
must_fail sh "$tool" --directory "$fixture/backups" --revision abcdef1 --container -bad
must_fail sh "$tool" --directory
ln -s "$fixture/backups" "$fixture/link"
must_fail sh "$tool" --directory "$fixture/link" --revision abcdef1
must_fail sh "$tool" --directory "$fixture/link/." --revision abcdef1
chmod 777 "$fixture/backups"
must_fail run
chmod 700 "$fixture/backups"
if [ "$(id -u)" = 0 ]; then
  chown 65534 "$fixture/backups"
  must_fail run
  chown 0 "$fixture/backups"
fi
echo 'Predeploy backup creation contracts passed.'
