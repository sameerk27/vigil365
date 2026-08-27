import React from "react";

/** Navy feature card (uses --sidebar background) with an accent eyebrow. */
export function DarkCard({ eyebrow, title, children, className }: {
  eyebrow?: React.ReactNode;
  title?: React.ReactNode;
  children: React.ReactNode;
  className?: string;
}) {
  return (
    <section className={`ui-darkcard${className ? ` ${className}` : ""}`}>
      {eyebrow && <div className="ui-darkcard-eyebrow">{eyebrow}</div>}
      {title && <div className="ui-darkcard-title">{title}</div>}
      {children}
    </section>
  );
}
