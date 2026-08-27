import React from "react";

/** Neutral stat tile — quieter than a KPI card. */
export function StatCard({ label, value, sub, className }: {
  label: React.ReactNode;
  value: React.ReactNode;
  sub?: React.ReactNode;
  className?: string;
}) {
  return (
    <div className={`ui-stat${className ? ` ${className}` : ""}`}>
      <div className="ui-stat-label">{label}</div>
      <div className="ui-stat-value">{value}</div>
      {sub !== undefined && <div className="ui-stat-sub">{sub}</div>}
    </div>
  );
}
