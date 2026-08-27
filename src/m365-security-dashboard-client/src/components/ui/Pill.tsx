import React from "react";

export type PillTone = "neutral" | "good" | "warn" | "err" | "info";

/** Small status pill from the token status palette. */
export function Pill({ tone = "neutral", children, className }: {
  tone?: PillTone; children: React.ReactNode; className?: string;
}) {
  return <span className={`ui-pill tone-${tone}${className ? ` ${className}` : ""}`}>{children}</span>;
}
