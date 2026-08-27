import React from "react";

export type TableColumn = {
  key: string;
  header: React.ReactNode;
  /** Right-align numeric columns. */
  align?: "start" | "end" | "center";
};

/**
 * Card framing a CSS-grid table. `template` is the raw
 * `grid-template-columns` value (a genuinely dynamic per-table value, so it
 * stays an inline style). Rows are pre-rendered cell arrays so callers keep
 * full control of cell markup, sort handlers, and truncation.
 */
export function TableCard({ title, chip, description, columns, template, rows, empty, className }: {
  title?: React.ReactNode;
  chip?: React.ReactNode;
  description?: React.ReactNode;
  columns: TableColumn[];
  template: string;
  rows: React.ReactNode[][];
  empty?: React.ReactNode;
  className?: string;
}) {
  return (
    <section className={`ui-card ui-tablecard${className ? ` ${className}` : ""}`}>
      {(title || chip) && (
        <div className="ui-tablecard-hdr">
          {title && <span className="ui-card-title">{title}</span>}
          {chip && <span className="ui-card-chip">{chip}</span>}
        </div>
      )}
      {description && <div className="ui-tablecard-desc">{description}</div>}
      <div className="ui-thead" style={{ gridTemplateColumns: template }}>
        {columns.map(c => (
          <span key={c.key} className={`ui-th align-${c.align ?? "start"}`}>{c.header}</span>
        ))}
      </div>
      <div className="ui-tbody">
        {rows.length === 0
          ? <div className="ui-trow-empty">{empty ?? "No rows."}</div>
          : rows.map((cells, ri) => (
              <div key={ri} className="ui-trow" style={{ gridTemplateColumns: template }}>
                {cells.map((cell, ci) => (
                  <span key={columns[ci]?.key ?? ci} className={`ui-td align-${columns[ci]?.align ?? "start"}`}>{cell}</span>
                ))}
              </div>
            ))}
      </div>
    </section>
  );
}
