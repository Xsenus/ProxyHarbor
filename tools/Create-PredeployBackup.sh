#!/bin/sh
set -eu
umask 077

fail() { printf 'Error: %s\n' "$1" >&2; exit 2; }
directory=''
revision=''
container='proxyharbor-postgres-1'
while [ "$#" -gt 0 ]; do
  case "$1" in
    --directory|--revision|--container)
      [ "$#" -ge 2 ] || fail "missing value for $1"
      case "$1" in
        --directory) directory=$2 ;;
        --revision) revision=$2 ;;
        --container) container=$2 ;;
      esac
      shift 2 ;;
    *) fail "unknown argument: $1" ;;
  esac
done
case "$directory" in /*) ;; *) fail 'an absolute --directory is required' ;; esac
[ "$directory" != / ] && [ -d "$directory" ] && [ ! -L "$directory" ] || fail 'unsafe directory'
resolved=$(CDPATH= cd -- "$directory" && pwd -P)
[ "$resolved" = "$directory" ] || fail 'directory must be canonical, without symbolic links'
[ "$(stat -c %u -- "$directory")" = "$(id -u)" ] || fail 'directory must belong to the current user'
mode=$(stat -c %a -- "$directory")
[ "$((0$mode & 0022))" -eq 0 ] || fail 'directory must not be writable by group or other users'
case "$revision" in ''|*[!0-9a-fA-F]*) fail 'revision must be hexadecimal' ;; esac
[ "${#revision}" -ge 7 ] && [ "${#revision}" -le 40 ] || fail 'revision must contain 7 to 40 hexadecimal characters'
case "$container" in ''|-*|*[!a-zA-Z0-9_.-]*) fail 'invalid container name' ;; esac
command -v docker >/dev/null 2>&1 || fail 'docker is required'

# mktemp creates the private inode before pg_dump starts writing sensitive data.
partial=$(mktemp "$directory/.predeploy-partial.XXXXXXXX")
cleanup() { [ -z "$partial" ] || rm -f -- "$partial"; }
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM
chmod 0600 -- "$partial"
docker exec "$container" sh -c 'exec pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" --lock-wait-timeout=30s -Fc' > "$partial"
[ -s "$partial" ] || fail 'pg_dump produced an empty archive'
docker exec -i "$container" pg_restore --list < "$partial" >/dev/null
[ "$(stat -c %a -- "$partial")" = 600 ] || fail 'unexpected archive permissions'

destination="$directory/predeploy-$revision-$(date -u +%Y%m%dT%H%M%SZ).dump"
# A hard-link publication is atomic and fails if a file or symlink already exists.
ln -- "$partial" "$destination"
rm -f -- "$partial"
partial=''
printf 'backup=%s\n' "$destination"
printf 'mode=%s\n' "$(stat -c %a -- "$destination")"
sha256sum -- "$destination"
