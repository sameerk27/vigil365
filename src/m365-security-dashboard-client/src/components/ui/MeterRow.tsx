import React from "react";

export type MeterTone = "good" | "high" | "med" | "bad" | "accent";

/**
 * Label + track + value meter. `pct` (0–100) drives the fill width — a
 * genuinely dynamic value, so it stays inline.
 */
export function MeterRow({ label, pct, value, tone = "accent", labelWidth }: {
  label: React.ReactNode;
  pct: number;
  value?: React.ReactNode;
  tone?: MeterTone;
  /** Fixed label column width, e.g. "120px". */
  labelWidth?: string;
}) {
  const clamped = Math.max(0, Math.min(100, pct));
  return (
    <div className="ui-meter">
      <span className="ui-meter-label" style={labelWidth ? { width: labelWidth } : undefined}>{label}</span>
      <span className="ui-meter-track">
        <span className={`ui-meter-fill tone-${tone}`} style={{ width: `${clamped}%` }} />
      </span>
      {value !== undefined && <span className="ui-meter-value">{value}</span>}
    </div>
  );
}
