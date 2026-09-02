#!/bin/sh
set -eu

usage() {
  cat <<'EOF'
Usage: Prune-PredeployBackups.sh --directory ABSOLUTE_PATH [--keep-count N] [--apply]

Keeps the newest N managed predeploy PostgreSQL dumps. The default mode is a
read-only preview; files are removed only when --apply is supplied.
EOF
}

fail() {
  printf 'Error: %s\n' "$1" >&2
  exit 2
}

directory=''
keep_count=7
apply=false

while [ "$#" -gt 0 ]; do
  case "$1" in
    --directory)
      [ "$#" -ge 2 ] || fail '--directory requires a value.'
      directory=$2
      shift 2
      ;;
    --keep-count)
      [ "$#" -ge 2 ] || fail '--keep-count requires a value.'
      keep_count=$2
      shift 2
      ;;
    --apply)
      apply=true
      shift
      ;;
    --help|-h)
      usage
      exit 0
      ;;
    *)
      fail "unknown argument: $1"
      ;;
  esac
done

[ -n "$directory" ] || fail '--directory is required.'
case "$directory" in
  /*) ;;
  *) fail '--directory must be an absolute path.' ;;
esac
[ -d "$directory" ] || fail '--directory does not exist.'
case "$keep_count" in
  ''|*[!0-9]*) fail '--keep-count must be a positive integer.' ;;
esac
[ "$keep_count" -ge 1 ] || fail '--keep-count must be at least 1.'

resolved_directory=$(CDPATH= cd -- "$directory" && pwd -P)
[ "$resolved_directory" != '/' ] || fail 'refusing to operate on the filesystem root.'

command -v flock >/dev/null 2>&1 || fail 'flock is required.'
command -v find >/dev/null 2>&1 || fail 'find is required.'
command -v stat >/dev/null 2>&1 || fail 'stat is required.'

directory_identity=$(stat -c '%d-%i' -- "$resolved_directory")
lock_file="${TMPDIR:-/tmp}/proxyharbor-predeploy-retention-${directory_identity}.lock"
exec 9>"$lock_file"
flock -n 9 || fail 'another predeploy retention process is already running.'

inventory_file=$(mktemp "${TMPDIR:-/tmp}/proxyharbor-predeploy-inventory.XXXXXX")
candidate_file=$(mktemp "${TMPDIR:-/tmp}/proxyharbor-predeploy-candidates.XXXXXX")
retained_file=$(mktemp "${TMPDIR:-/tmp}/proxyharbor-predeploy-retained.XXXXXX")
cleanup() {
  rm -f -- "$inventory_file" "$candidate_file" "$retained_file"
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

# Only exact tool-owned names are eligible. Legacy deployments used four
# timestamp formats, all of which remain explicit here. Symlinks are excluded
# by `-type f`, and traversal is limited to the selected directory itself.
name_pattern='^predeploy-[0-9a-f]{7,40}-([0-9]{8}|[0-9]{12,14}|[0-9]{8}T[0-9]{6}Z)\.dump$'
find "$resolved_directory" -maxdepth 1 -type f -name 'predeploy-*.dump' -printf '%T@ %f\n' |
  LC_ALL=C sort -k1,1nr |
  while IFS=' ' read -r modified_at name; do
    if printf '%s\n' "$name" | grep -Eq "$name_pattern"; then
      printf '%s %s\n' "$modified_at" "$name"
    fi
  done > "$inventory_file"

matched_count=$(wc -l < "$inventory_file" | tr -d ' ')
if [ "$matched_count" -gt "$keep_count" ]; then
  sed -n "1,${keep_count}p" "$inventory_file" > "$retained_file"
  sed -n "$((keep_count + 1)),\$p" "$inventory_file" > "$candidate_file"
else
  cp -- "$inventory_file" "$retained_file"
  : > "$candidate_file"
fi

candidate_count=$(wc -l < "$candidate_file" | tr -d ' ')
candidate_bytes=0
while IFS=' ' read -r _ name; do
  [ -n "$name" ] || continue
  candidate_bytes=$((candidate_bytes + $(stat -c '%s' -- "$resolved_directory/$name")))
done < "$candidate_file"

printf 'directory=%s\n' "$resolved_directory"
printf 'matched=%s\n' "$matched_count"
printf 'keep_count=%s\n' "$keep_count"
printf 'candidates=%s\n' "$candidate_count"
printf 'candidate_bytes=%s\n' "$candidate_bytes"

if [ "$apply" != true ]; then
  printf 'mode=dry-run\n'
  while IFS=' ' read -r _ name; do
    [ -n "$name" ] && printf 'would_remove=%s\n' "$name"
  done < "$candidate_file"
  exit 0
fi

# Never remove an older recovery point unless every dump that will remain has
# a readable PostgreSQL archive catalog. Verification completes for the entire
# retained set before the first destructive operation begins.
verified_retained=0
if [ "$candidate_count" -gt 0 ]; then
  command -v pg_restore >/dev/null 2>&1 || fail 'pg_restore is required before applying retention.'
  while IFS=' ' read -r _ name; do
    [ -n "$name" ] || continue
    path="$resolved_directory/$name"
    [ -f "$path" ] || fail "retained backup disappeared before verification: $name"
    [ ! -L "$path" ] || fail "retained backup became a symbolic link: $name"
    pg_restore --list "$path" >/dev/null 2>&1 || fail "retained backup is not a readable PostgreSQL archive: $name"
    verified_retained=$((verified_retained + 1))
  done < "$retained_file"
fi
printf 'verified_retained=%s\n' "$verified_retained"

removed_count=0
removed_bytes=0
while IFS=' ' read -r _ name; do
  [ -n "$name" ] || continue
  path="$resolved_directory/$name"
  printf '%s\n' "$name" | grep -Eq "$name_pattern" || fail 'candidate name changed during retention.'
  [ -f "$path" ] || continue
  [ ! -L "$path" ] || fail 'refusing to remove a symbolic link.'
  size=$(stat -c '%s' -- "$path")
  rm -- "$path"
  removed_count=$((removed_count + 1))
  removed_bytes=$((removed_bytes + size))
  printf 'removed=%s\n' "$name"
done < "$candidate_file"

printf 'mode=apply\n'
printf 'removed_count=%s\n' "$removed_count"
printf 'removed_bytes=%s\n' "$removed_bytes"
