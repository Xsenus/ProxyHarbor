#!/bin/sh
set -eu

usage() {
  cat <<'EOF'
Usage: Check-ProductionSecrets.sh --directory ABSOLUTE_PATH [--expected-owner UID]

Validates the file-backed Docker Compose secrets used by ProxyHarbor production.
The command is read-only and never prints secret values.
EOF
}

fail() {
  printf 'Error: %s\n' "$1" >&2
  exit 2
}

directory=''
expected_owner=$(id -u)

while [ "$#" -gt 0 ]; do
  case "$1" in
    --directory)
      [ "$#" -ge 2 ] || fail '--directory requires a value.'
      directory=$2
      shift 2
      ;;
    --expected-owner)
      [ "$#" -ge 2 ] || fail '--expected-owner requires a value.'
      expected_owner=$2
      shift 2
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
case "$expected_owner" in
  ''|*[!0-9]*) fail '--expected-owner must be a numeric UID.' ;;
esac
[ -d "$directory" ] || fail 'secret directory does not exist.'
[ ! -L "$directory" ] || fail 'secret directory must not be a symbolic link.'

resolved_directory=$(CDPATH= cd -- "$directory" && pwd -P)
[ "$resolved_directory" != '/' ] || fail 'refusing to inspect the filesystem root.'
[ "$(stat -c '%a' -- "$resolved_directory")" = '700' ] ||
  fail 'secret directory mode must be exactly 0700.'
[ "$(stat -c '%u' -- "$resolved_directory")" = "$expected_owner" ] ||
  fail 'secret directory has an unexpected owner.'

secret_names='postgres_password
admin_password
admin_api_key
backup_encryption_key
telegram_bot_token
telegram_chat_id
alertmanager_webhook_token
yookassa_secret
yoomoney_notification_secret
cloudpayments_secret
robokassa_password1
robokassa_password2
tbank_password
stripe_secret
stripe_webhook_secret
cryptomus_payment_key
nowpayments_api_key
nowpayments_ipn_secret'

required_names=' postgres_password admin_password admin_api_key backup_encryption_key telegram_bot_token telegram_chat_id alertmanager_webhook_token '
checked=0
for name in $secret_names; do
  path="$resolved_directory/$name"
  [ -f "$path" ] || fail "missing regular secret file: $name"
  [ ! -L "$path" ] || fail "secret file must not be a symbolic link: $name"
  [ "$(stat -c '%u' -- "$path")" = "$expected_owner" ] ||
    fail "secret file has an unexpected owner: $name"
  mode=$(stat -c '%a' -- "$path")
  case "$name:$mode" in
    telegram_bot_token:440|telegram_chat_id:440|*:444) ;;
    *) fail "active secret mode must be 0444 (retired Telegram bootstrap files may use 0440): $name" ;;
  esac
  [ "$(stat -c '%h' -- "$path")" = '1' ] ||
    fail "secret file must not have additional hard links: $name"
  size=$(stat -c '%s' -- "$path")
  [ "$size" -le 4096 ] || fail "secret file exceeds 4096 bytes: $name"
  case "$required_names" in
    *" $name "*) [ "$size" -gt 0 ] || fail "required secret file is empty: $name" ;;
  esac
  checked=$((checked + 1))
done

read_normalized_secret() {
  path=$1
  file_bytes=$(stat -c '%s' -- "$path")
  logical_bytes=$file_bytes
  if [ "$logical_bytes" -gt 0 ] &&
    [ "$(tail -c 1 -- "$path" | od -An -t u1 | tr -d ' ')" = '10' ]; then
    logical_bytes=$((logical_bytes - 1))
    if [ "$logical_bytes" -gt 0 ] &&
      [ "$(dd if="$path" bs=1 skip=$((logical_bytes - 1)) count=1 2>/dev/null | od -An -t u1 | tr -d ' ')" = '13' ]; then
      logical_bytes=$((logical_bytes - 1))
    fi
  fi
  dd if="$path" bs=1 count="$logical_bytes" 2>/dev/null
}

check_single_line_no_control() {
  name=$1
  minimum=$2
  maximum=$3
  path="$resolved_directory/$name"
  value=$(read_normalized_secret "$path")
  bytes=$(printf '%s' "$value" | wc -c | tr -d ' ')
  characters=$(printf '%s' "$value" | wc -m | tr -d ' ')
  file_bytes=$(stat -c '%s' -- "$path")
  last_byte=$(tail -c 1 -- "$path" | od -An -t u1 | tr -d ' ')
  line_ending_bytes=0
  if [ "$last_byte" = '10' ]; then
    line_ending_bytes=1
    if [ "$file_bytes" -gt 1 ] &&
      [ "$(dd if="$path" bs=1 skip=$((file_bytes - 2)) count=1 2>/dev/null | od -An -t u1 | tr -d ' ')" = '13' ]; then
      line_ending_bytes=2
    fi
  fi
  [ "$bytes" -eq $((file_bytes - line_ending_bytes)) ] ||
    fail "secret contains NUL bytes or more than one trailing line ending: $name"
  [ "$characters" -ge "$minimum" ] && [ "$characters" -le "$maximum" ] ||
    fail "secret length is outside the accepted range: $name"
  if printf '%s' "$value" | LC_ALL=C grep -q '[[:cntrl:]]'; then
    fail "secret contains control bytes: $name"
  fi
  unset value
}

check_single_line_no_control admin_password 24 4096
check_single_line_no_control admin_api_key 24 256
check_single_line_no_control backup_encryption_key 32 1024
check_single_line_no_control telegram_bot_token 20 256
check_single_line_no_control alertmanager_webhook_token 32 256

telegram_bot_token=$(read_normalized_secret "$resolved_directory/telegram_bot_token")
printf '%s' "$telegram_bot_token" | LC_ALL=C grep -Eq '^[!-~]+$' ||
  fail 'telegram_bot_token must contain path-safe printable ASCII.'
case "$telegram_bot_token" in
  *'/'*|*'\'*|*'?'*|*'#'*|*'%'*) fail 'telegram_bot_token contains a forbidden URL-path character.' ;;
esac
unset telegram_bot_token

alertmanager_webhook_token=$(read_normalized_secret "$resolved_directory/alertmanager_webhook_token")
printf '%s' "$alertmanager_webhook_token" | LC_ALL=C grep -Eq '^[!-~]+$' ||
  fail 'alertmanager_webhook_token must contain non-space printable ASCII.'
unset alertmanager_webhook_token

telegram_chat_id=$(read_normalized_secret "$resolved_directory/telegram_chat_id")
printf '%s' "$telegram_chat_id" | grep -Eq '^-?[0-9]{1,20}$' ||
  fail 'telegram_chat_id must contain one numeric chat identifier.'
[ "$telegram_chat_id" != '0' ] && [ "$telegram_chat_id" != '-0' ] ||
  fail 'telegram_chat_id must not be zero.'
unset telegram_chat_id

printf 'directory=%s\n' "$resolved_directory"
printf 'checked=%s\n' "$checked"
printf 'status=ok\n'
