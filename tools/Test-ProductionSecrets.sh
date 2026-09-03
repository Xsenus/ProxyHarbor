#!/bin/sh
set -eu

tool=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd -P)/Check-ProductionSecrets.sh
repository_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd -P)
fixture=$(mktemp -d "${TMPDIR:-/tmp}/proxyharbor-secret-test.XXXXXX")
cleanup() {
  resolved_fixture=$(CDPATH= cd -- "$fixture" 2>/dev/null && pwd -P || true)
  case "$resolved_fixture" in
    "${TMPDIR:-/tmp}"/proxyharbor-secret-test.*)
      find "$resolved_fixture" -mindepth 1 -delete
      rmdir -- "$resolved_fixture"
      ;;
  esac
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

secret_names='postgres_password admin_password admin_api_key backup_encryption_key telegram_bot_token telegram_chat_id alertmanager_webhook_token yookassa_secret yoomoney_notification_secret cloudpayments_secret robokassa_password1 robokassa_password2 tbank_password stripe_secret stripe_webhook_secret cryptomus_payment_key nowpayments_api_key nowpayments_ipn_secret'
for name in $secret_names; do
  : > "$fixture/$name"
done
printf 'database-password' > "$fixture/postgres_password"
printf 'Admin-password-at-least-24-characters!' > "$fixture/admin_password"
printf 'admin-api-key-at-least-24-characters' > "$fixture/admin_api_key"
printf 'backup-encryption-key-at-least-32-characters' > "$fixture/backup_encryption_key"
printf '9000000000:TEST_PLACEHOLDER_NOT_A_REAL_TOKEN' > "$fixture/telegram_bot_token"
printf '%s' '-1001234567890' > "$fixture/telegram_chat_id"
printf 'alertmanager-webhook-token-at-least-32-characters' > "$fixture/alertmanager_webhook_token"
chmod 700 "$fixture"
chmod 444 "$fixture"/*

output=$($tool --directory "$fixture" --expected-owner "$(id -u)")
printf '%s\n' "$output" | grep -Fx 'checked=18' >/dev/null
printf '%s\n' "$output" | grep -Fx 'status=ok' >/dev/null
if printf '%s\n' "$output" | grep -F 'Admin-password-at-least-24-characters!' >/dev/null; then
  echo 'Secret value was printed by preflight.' >&2
  exit 1
fi

chmod 600 "$fixture/alertmanager_webhook_token"
if $tool --directory "$fixture" --expected-owner "$(id -u)" >/dev/null 2>&1; then
  echo 'Unreadable container secret mode was accepted.' >&2
  exit 1
fi
chmod 444 "$fixture/alertmanager_webhook_token"

chmod 440 "$fixture/telegram_bot_token" "$fixture/telegram_chat_id"
$tool --directory "$fixture" --expected-owner "$(id -u)" >/dev/null
chmod 444 "$fixture/telegram_bot_token" "$fixture/telegram_chat_id"

printf '\n' >> "$fixture/admin_api_key"
$tool --directory "$fixture" --expected-owner "$(id -u)" >/dev/null
printf '\n' >> "$fixture/admin_api_key"
if $tool --directory "$fixture" --expected-owner "$(id -u)" >/dev/null 2>&1; then
  echo 'Secret with multiple trailing newlines was accepted.' >&2
  exit 1
fi
printf 'admin-api-key-at-least-24-characters' > "$fixture/admin_api_key"
chmod 444 "$fixture/admin_api_key"

printf 'backup-encryption-key-at-least-32-characters\r\n' > "$fixture/backup_encryption_key"
chmod 444 "$fixture/backup_encryption_key"
$tool --directory "$fixture" --expected-owner "$(id -u)" >/dev/null
printf 'backup-encryption-key-at-least-32-characters' > "$fixture/backup_encryption_key"
chmod 444 "$fixture/backup_encryption_key"

rm -- "$fixture/yookassa_secret"
ln -s admin_api_key "$fixture/yookassa_secret"
if $tool --directory "$fixture" --expected-owner "$(id -u)" >/dev/null 2>&1; then
  echo 'Symbolic-link secret was accepted.' >&2
  exit 1
fi
rm -- "$fixture/yookassa_secret"
: > "$fixture/yookassa_secret"
chmod 444 "$fixture/yookassa_secret"

chmod 755 "$fixture"
if $tool --directory "$fixture" --expected-owner "$(id -u)" >/dev/null 2>&1; then
  echo 'World-traversable secret directory was accepted.' >&2
  exit 1
fi
chmod 700 "$fixture"

printf 'alertmanager webhook token with spaces invalid' > "$fixture/alertmanager_webhook_token"
chmod 444 "$fixture/alertmanager_webhook_token"
if $tool --directory "$fixture" --expected-owner "$(id -u)" >/dev/null 2>&1; then
  echo 'Alertmanager token containing spaces was accepted.' >&2
  exit 1
fi
printf 'alertmanager-webhook-token-at-least-32-characters' > "$fixture/alertmanager_webhook_token"
chmod 444 "$fixture/alertmanager_webhook_token"

if $tool --directory . >/dev/null 2>&1; then
  echo 'Relative secret directory was accepted.' >&2
  exit 1
fi
if $tool --directory / >/dev/null 2>&1; then
  echo 'Filesystem root was accepted.' >&2
  exit 1
fi

grep -F './tools/Check-ProductionSecrets.sh --directory /opt/proxyharbor/.secrets --expected-owner 0' \
  "$repository_root/docs/DEPLOYMENT.md" >/dev/null
grep -F 'bash ./tools/Test-ProductionSecrets.sh' "$repository_root/.github/workflows/ci.yml" >/dev/null
grep -F 'bash ./tools/Test-ProductionSecrets.sh' "$repository_root/.github/workflows/release.yml" >/dev/null

echo 'Production secret preflight contracts passed.'
