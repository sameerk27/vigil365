import React from "react";

export type KpiTone = "good" | "warn" | "err" | "neutral";

/** Headline KPI tile — tone drives the border and value color. */
export function KpiCard({ label, value, sub, tone = "neutral", className }: {
  label: React.ReactNode;
  value: React.ReactNode;
  sub?: React.ReactNode;
  tone?: KpiTone;
  className?: string;
}) {
  return (
    <div className={`ui-kpi tone-${tone}${className ? ` ${className}` : ""}`}>
      <div className="ui-kpi-label">{label}</div>
      <div className="ui-kpi-value">{value}</div>
      {sub !== undefined && <div className="ui-kpi-sub">{sub}</div>}
    </div>
  );
}
