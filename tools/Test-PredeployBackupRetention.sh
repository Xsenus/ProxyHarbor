#!/bin/sh
set -eu

tool=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd -P)/Prune-PredeployBackups.sh
fixture=$(mktemp -d "${TMPDIR:-/tmp}/proxyharbor-retention-test.XXXXXX")
cleanup() {
  resolved_fixture=$(CDPATH= cd -- "$fixture" 2>/dev/null && pwd -P || true)
  case "$resolved_fixture" in
    "${TMPDIR:-/tmp}"/proxyharbor-retention-test.*)
      find "$resolved_fixture" -mindepth 1 -delete
      rmdir -- "$resolved_fixture"
      ;;
  esac
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

index=1
while [ "$index" -le 10 ]; do
  name=$(printf 'predeploy-%07x-202609%02d.dump' "$index" "$index")
  printf '%s' "$index" > "$fixture/$name"
  touch -d "@$((1700000000 + index))" "$fixture/$name"
  index=$((index + 1))
done

printf 'legacy' > "$fixture/predeploy-abcdef1-20260901T120000Z.dump"
touch -d '@1600000001' "$fixture/predeploy-abcdef1-20260901T120000Z.dump"
printf 'legacy' > "$fixture/predeploy-abcdef2-20260901120000.dump"
touch -d '@1600000002' "$fixture/predeploy-abcdef2-20260901120000.dump"
printf 'legacy' > "$fixture/predeploy-abcdef3-1788282295644.dump"
touch -d '@1600000003' "$fixture/predeploy-abcdef3-1788282295644.dump"

printf 'unrelated' > "$fixture/manual.dump"
printf 'invalid' > "$fixture/predeploy-not-a-revision-20260901.dump"
ln -s manual.dump "$fixture/predeploy-fffffff-20260930.dump"

cat > "$fixture/pg_restore" <<'EOF'
#!/bin/sh
exit 1
EOF
chmod 700 "$fixture/pg_restore"
PATH="$fixture:$PATH"
export PATH

preview=$($tool --directory "$fixture" --keep-count 3)
printf '%s\n' "$preview" | grep -Fx 'matched=13' >/dev/null
printf '%s\n' "$preview" | grep -Fx 'candidates=10' >/dev/null
printf '%s\n' "$preview" | grep -Fx 'mode=dry-run' >/dev/null
[ "$(find "$fixture" -maxdepth 1 -type f -name 'predeploy-*.dump' | wc -l)" -eq 14 ]

# A corrupt retained archive must stop the run before any candidate is removed.
if $tool --directory "$fixture" --keep-count 3 --apply >/dev/null 2>&1; then
  echo 'Unreadable retained backups were accepted.' >&2
  exit 1
fi
[ "$(find "$fixture" -maxdepth 1 -type f -name 'predeploy-*.dump' | wc -l)" -eq 14 ]

cat > "$fixture/pg_restore" <<'EOF'
#!/bin/sh
exit 0
EOF
chmod 700 "$fixture/pg_restore"
applied=$($tool --directory "$fixture" --keep-count 3 --apply)
printf '%s\n' "$applied" | grep -Fx 'verified_retained=3' >/dev/null
printf '%s\n' "$applied" | grep -Fx 'removed_count=10' >/dev/null
printf '%s\n' "$applied" | grep -Fx 'mode=apply' >/dev/null
[ "$(find "$fixture" -maxdepth 1 -type f -name 'predeploy-*.dump' | wc -l)" -eq 4 ]
[ -f "$fixture/predeploy-0000008-20260908.dump" ]
[ -f "$fixture/predeploy-0000009-20260909.dump" ]
[ -f "$fixture/predeploy-000000a-20260910.dump" ]
[ -f "$fixture/predeploy-not-a-revision-20260901.dump" ]
[ -L "$fixture/predeploy-fffffff-20260930.dump" ]
[ -f "$fixture/manual.dump" ]

if $tool --directory . >/dev/null 2>&1; then
  echo 'Relative directory was accepted.' >&2
  exit 1
fi
if $tool --directory / --keep-count 1 >/dev/null 2>&1; then
  echo 'Filesystem root was accepted.' >&2
  exit 1
fi
if $tool --directory "$fixture" --keep-count 0 >/dev/null 2>&1; then
  echo 'Zero keep count was accepted.' >&2
  exit 1
fi

echo 'Predeploy backup retention contracts passed.'
