import React, { useEffect, useMemo, useRef, useState } from "react";
import { Search, FileText, ShieldAlert, User, Monitor } from "lucide-react";
import { NavPage, SecurityAlert } from "../services/types";
import { openEntity } from "../services/api";
import { sevClass } from "../services/utils";

type Result =
  | { kind: "page"; label: string; page: NavPage }
  | { kind: "alert"; label: string; sub: string; severity: string; alert: SecurityAlert }
  | { kind: "user"; label: string }
  | { kind: "device"; label: string };

/**
 * Ctrl+K command palette: search pages, alerts, users, and devices from
 * anywhere. Pure client-side over already-loaded data — instant results.
 */
export function GlobalSearch({ open, onClose, alerts, pages, onOpenAlert, onNavigatePage }: {
  open: boolean;
  onClose: () => void;
  alerts: SecurityAlert[];
  pages: { id: NavPage; label: string }[];
  onOpenAlert: (a: SecurityAlert) => void;
  onNavigatePage: (p: NavPage) => void;
}) {
  const [query, setQuery] = useState("");
  const [activeIdx, setActiveIdx] = useState(0);
  const inputRef = useRef<HTMLInputElement>(null);
  const listRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (open) { setQuery(""); setActiveIdx(0); setTimeout(() => inputRef.current?.focus(), 0); }
  }, [open]);

  const results = useMemo((): Result[] => {
    const q = query.trim().toLowerCase();
    if (!q) return pages.slice(0, 8).map(p => ({ kind: "page" as const, label: p.label, page: p.id }));

    const pageHits: Result[] = pages
      .filter(p => p.label.toLowerCase().includes(q))
      .slice(0, 4)
      .map(p => ({ kind: "page", label: p.label, page: p.id }));

    const alertHits: Result[] = alerts
      .filter(a => a.title.toLowerCase().includes(q) ||
                   a.userPrincipalName?.toLowerCase().includes(q) ||
                   a.deviceName?.toLowerCase().includes(q))
      .sort((a, b) => Number(a.isResolved) - Number(b.isResolved)) // open first
      .slice(0, 6)
      .map(a => ({
        kind: "alert", alert: a, severity: a.severity,
        label: a.title,
        sub: [a.isResolved ? "Resolved" : "Active", a.userPrincipalName ?? a.deviceName].filter(Boolean).join(" · "),
      }));

    const userHits: Result[] = [...new Set(
      alerts.map(a => a.userPrincipalName).filter((u): u is string => !!u && u.toLowerCase().includes(q))
    )].slice(0, 4).map(u => ({ kind: "user", label: u }));

    const deviceHits: Result[] = [...new Set(
      alerts.map(a => a.deviceName).filter((d): d is string => !!d && d.toLowerCase().includes(q))
    )].slice(0, 4).map(d => ({ kind: "device", label: d }));

    return [...alertHits, ...userHits, ...deviceHits, ...pageHits];
  }, [query, alerts, pages]);

  useEffect(() => { setActiveIdx(0); }, [results.length]);

  const select = (r: Result) => {
    onClose();
    switch (r.kind) {
      case "page":   onNavigatePage(r.page); break;
      case "alert":  onOpenAlert(r.alert); break;
      case "user":   openEntity("user", r.label); break;
      case "device": openEntity("device", r.label); break;
    }
  };

  const onKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === "Escape") { onClose(); return; }
    if (e.key === "ArrowDown") { e.preventDefault(); setActiveIdx(i => Math.min(i + 1, results.length - 1)); }
    if (e.key === "ArrowUp")   { e.preventDefault(); setActiveIdx(i => Math.max(i - 1, 0)); }
    if (e.key === "Enter" && results[activeIdx]) { e.preventDefault(); select(results[activeIdx]); }
  };

  // Keep the active row in view while arrowing.
  useEffect(() => {
    listRef.current?.querySelector<HTMLElement>(`[data-idx="${activeIdx}"]`)
      ?.scrollIntoView({ block: "nearest" });
  }, [activeIdx]);

  if (!open) return null;

  const icon = (r: Result) => r.kind === "page" ? <FileText size={14}/>
    : r.kind === "alert" ? <ShieldAlert size={14}/>
    : r.kind === "user" ? <User size={14}/>
    : <Monitor size={14}/>;

  const groupLabel = (r: Result) => r.kind === "page" ? "Go to page"
    : r.kind === "alert" ? "Alerts" : r.kind === "user" ? "Users" : "Devices";

  return (
    <div className="gs-backdrop" onClick={onClose}>
      <div className="gs-palette" role="dialog" aria-modal="true" aria-label="Global search"
        onClick={e => e.stopPropagation()} onKeyDown={onKeyDown}>
        <div className="gs-input-row">
          <Search size={16}/>
          <input ref={inputRef} className="gs-input" value={query}
            onChange={e => setQuery(e.target.value)}
            placeholder="Search alerts, users, devices, pages…"
            role="combobox" aria-expanded aria-controls="gs-results" aria-activedescendant={`gs-item-${activeIdx}`}/>
          <kbd className="gs-kbd">Esc</kbd>
        </div>
        <div className="gs-results" id="gs-results" role="listbox" ref={listRef}>
          {results.length === 0 ? (
            <div className="gs-empty">No matches for "{query}"</div>
          ) : results.map((r, i) => {
            const prev = results[i - 1];
            const showGroup = !prev || groupLabel(prev) !== groupLabel(r);
            return (
              <React.Fragment key={`${r.kind}-${r.label}-${i}`}>
                {showGroup && <div className="gs-group">{groupLabel(r)}</div>}
                <div id={`gs-item-${i}`} data-idx={i} role="option" aria-selected={i === activeIdx}
                  className={`gs-item${i === activeIdx ? " active" : ""}`}
                  onMouseEnter={() => setActiveIdx(i)} onClick={() => select(r)}>
                  {r.kind === "alert"
                    ? <span className={sevClass(r.severity)} style={{ marginTop: 0 }}/>
                    : <span className="gs-icon">{icon(r)}</span>}
                  <span className="gs-label">{r.label}</span>
                  {r.kind === "alert" && <span className="gs-sub">{r.sub}</span>}
                </div>
              </React.Fragment>
            );
          })}
        </div>
      </div>
    </div>
  );
}
