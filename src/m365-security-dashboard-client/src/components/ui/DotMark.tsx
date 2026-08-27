import React from "react";

/**
 * Accent dot mark. Replaces the former icon placeholders across the redesign.
 * `square` renders the dense-table 8×8 rounded square instead of a circle.
 */
export type DotTone = "accent" | "critical" | "high" | "medium" | "low" | "info" | "good" | "muted";

export function DotMark({ tone = "accent", square = false, className }: {
  tone?: DotTone; square?: boolean; className?: string;
}) {
  return (
    <span
      className={`ui-dot tone-${tone}${square ? " ui-dot-square" : ""}${className ? ` ${className}` : ""}`}
      aria-hidden="true"
    />
  );
}
