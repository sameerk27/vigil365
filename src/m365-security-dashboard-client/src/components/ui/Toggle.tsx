import React from "react";

/**
 * Presentational on/off toggle. Drives the caller's existing enable/disable
 * handler — it holds no state of its own.
 */
export function Toggle({ checked, onChange, label, onLabel, offLabel, disabled, ariaLabel }: {
  checked: boolean;
  onChange: (next: boolean) => void;
  label?: React.ReactNode;
  onLabel?: string;
  offLabel?: string;
  disabled?: boolean;
  ariaLabel?: string;
}) {
  const text = label ?? (checked ? onLabel : offLabel);
  return (
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      aria-label={ariaLabel}
      disabled={disabled}
      className="ui-toggle-wrap"
      onClick={() => onChange(!checked)}
    >
      <span className={`ui-toggle-track${checked ? " on" : ""}`}>
        <span className="ui-toggle-knob" />
      </span>
      {text !== undefined && <span className={`ui-toggle-label${checked ? " on" : ""}`}>{text}</span>}
    </button>
  );
}
