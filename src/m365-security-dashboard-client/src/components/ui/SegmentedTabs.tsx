import React from "react";

export type SegmentedTab<T extends string> = {
  id: T;
  label: React.ReactNode;
  count?: number;
};

/** Pill-in-container segmented tab bar. Purely presentational — parent owns state. */
export function SegmentedTabs<T extends string>({ tabs, active, onChange, ariaLabel }: {
  tabs: SegmentedTab<T>[];
  active: T;
  onChange: (id: T) => void;
  ariaLabel?: string;
}) {
  // Roving arrow-key navigation across the tablist (WAI-ARIA tabs pattern):
  // only the active tab is in the tab order; Left/Right/Home/End move selection.
  const onKeyDown = (e: React.KeyboardEvent<HTMLButtonElement>, idx: number) => {
    let next = idx;
    if (e.key === "ArrowRight" || e.key === "ArrowDown") next = (idx + 1) % tabs.length;
    else if (e.key === "ArrowLeft" || e.key === "ArrowUp") next = (idx - 1 + tabs.length) % tabs.length;
    else if (e.key === "Home") next = 0;
    else if (e.key === "End") next = tabs.length - 1;
    else return;
    e.preventDefault();
    onChange(tabs[next].id);
    const parent = e.currentTarget.parentElement;
    (parent?.children[next] as HTMLElement | undefined)?.focus();
  };

  return (
    <div className="ui-segtabs" role="tablist" aria-label={ariaLabel}>
      {tabs.map((t, i) => (
        <button
          key={t.id}
          role="tab"
          aria-selected={active === t.id}
          tabIndex={active === t.id ? 0 : -1}
          className={`ui-segtab${active === t.id ? " active" : ""}`}
          onClick={() => onChange(t.id)}
          onKeyDown={e => onKeyDown(e, i)}
        >
          {t.label}
          {t.count !== undefined && t.count > 0 && <span className="ui-segtab-count">{t.count}</span>}
        </button>
      ))}
    </div>
  );
}
