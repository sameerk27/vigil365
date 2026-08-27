import React from "react";

/** Inline code / endpoint chip — mono, subtle background, ellipsis-truncated. */
export function CodeBlock({ children, title, className }: {
  children: React.ReactNode;
  title?: string;
  className?: string;
}) {
  return (
    <code className={`ui-code${className ? ` ${className}` : ""}`} title={title}>
      {children}
    </code>
  );
}
