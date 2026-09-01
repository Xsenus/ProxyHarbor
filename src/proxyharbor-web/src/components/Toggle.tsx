export function Toggle({
  checked,
  onChange,
  label,
  danger = false,
  disabled = false,
}: {
  checked: boolean;
  onChange: (value: boolean) => void;
  label: string;
  danger?: boolean;
  disabled?: boolean;
}) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      className={`ui-switch ${checked ? "on" : ""} ${danger ? "danger" : ""}`}
      disabled={disabled}
      onClick={() => onChange(!checked)}
    >
      <i>
        <span />
      </i>
      <b>{label}</b>
    </button>
  );
}
