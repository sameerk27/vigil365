import React from "react";
import { DotMark } from "./DotMark";

/** Dashed empty state with an accent dot over muted copy. */
export function EmptyState({ children, className }: {
  children: React.ReactNode;
  className?: string;
}) {
  return (
    <div className={`ui-empty${className ? ` ${className}` : ""}`}>
      <DotMark tone="accent" className="ui-empty-dot" />
      <div className="ui-empty-copy">{children}</div>
    </div>
  );
}
