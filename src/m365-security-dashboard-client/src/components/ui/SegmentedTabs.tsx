import React from "react";

export type SegmentedTab<T extends string> = {
  id: T;
  label: React.ReactNode;
  count?: number;
};

/** Underline segmented tab bar. Purely presentational — parent owns state. */
export function SegmentedTabs<T extends string>({ tabs, active, onChange, ariaLabel }: {
  tabs: SegmentedTab<T>[];
  active: T;
  onChange: (id: T) => void;
  ariaLabel?: string;
}) {
  return (
    <div className="ui-segtabs" role="tablist" aria-label={ariaLabel}>
      {tabs.map(t => (
        <button
          key={t.id}
          role="tab"
          aria-selected={active === t.id}
          className={`ui-segtab${active === t.id ? " active" : ""}`}
          onClick={() => onChange(t.id)}
        >
          {t.label}
          {t.count !== undefined && t.count > 0 && <span className="ui-segtab-count">{t.count}</span>}
        </button>
      ))}
    </div>
  );
}
