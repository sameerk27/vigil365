import React from "react";

/**
 * Base surface card. Optional title row with a right-aligned chip
 * (`margin-left:auto`). `bodyClassName` styles the inner content wrapper.
 */
export function Card({ title, chip, children, className, bodyClassName, titleRight }: {
  title?: React.ReactNode;
  chip?: React.ReactNode;
  titleRight?: React.ReactNode;
  children: React.ReactNode;
  className?: string;
  bodyClassName?: string;
}) {
  return (
    <section className={`ui-card${className ? ` ${className}` : ""}`}>
      {(title || chip || titleRight) && (
        <div className="ui-card-hdr">
          {title && <span className="ui-card-title">{title}</span>}
          {chip && <span className="ui-card-chip">{chip}</span>}
          {titleRight && <span className="ui-card-right">{titleRight}</span>}
        </div>
      )}
      <div className={bodyClassName}>{children}</div>
    </section>
  );
}
