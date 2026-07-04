import React, { useState, useRef, useEffect } from "react";
import { Download, ChevronRight, Copy, ClipboardCheck, ExternalLink, AlertTriangle, Activity, X, Lock } from "lucide-react";
import { Tone, SecurityAlert } from "../services/types";
import { fmtService, fmtDate, relTime, downloadCsv, copyToClipboard } from "../services/utils";
import { showToast } from "../services/toast";

// ─── Export dropdown ──────────────────────────────────────────────────────────
export function ExportDropdown({ rows, filename }: { rows: Record<string, unknown>[]; filename: string }) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const handler = (e: MouseEvent) => { if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false); };
    document.addEventListener("mousedown", handler);
    return () => document.removeEventListener("mousedown", handler);
  }, []);
  return (
    <div className="export-wrap" ref={ref}>
      <button className="btn-export" onClick={() => setOpen(o => !o)} aria-label="Export options">
        <Download size={13}/> Export
        <ChevronRight size={11} style={{ marginLeft:2, transform: open?"rotate(90deg)":"none", transition:"transform .15s" }}/>
      </button>
      {open && (
        <div className="export-dropdown" role="menu">
          <button role="menuitem" onClick={() => {
            downloadCsv(rows, filename);
            showToast(`Exported ${rows.length} rows to ${filename}`);
            setOpen(false);
          }}><Download size={13}/> Export CSV</button>
          <hr/>
          <button role="menuitem" onClick={() => {
            copyToClipboard(rows).then(() => { showToast("Copied JSON to clipboard"); setOpen(false); });
          }}><Copy size={13}/> Copy as JSON</button>
        </div>
      )}
    </div>
  );
}

// ─── Copy to clipboard button ───────────────────────────────────────────────────
export function CopyButton({ value, label, size = 13 }: { value: string; label?: string; size?: number }) {
  const [copied, setCopied] = useState(false);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  useEffect(() => () => { if (timer.current) clearTimeout(timer.current); }, []);
  const copy = async (e: React.MouseEvent) => {
    e.stopPropagation();
    try {
      await navigator.clipboard.writeText(value);
      setCopied(true);
      showToast(`Copied ${label ?? "value"} to clipboard`);
      if (timer.current) clearTimeout(timer.current);
      timer.current = setTimeout(() => setCopied(false), 1500);
    } catch {
      showToast("Copy failed — clipboard unavailable", "error");
    }
  };
  return (
    <button
      type="button"
      className="copy-btn"
      onClick={copy}
      aria-label={copied ? "Copied" : `Copy ${label ?? "value"}`}
      title={copied ? "Copied!" : `Copy ${label ?? "value"}`}
      style={{ background: "none", border: "none", cursor: "pointer", padding: 2, display: "inline-flex", alignItems: "center", color: copied ? "var(--status-good-icon)" : "var(--color-muted)", flexShrink: 0 }}
    >
      {copied ? <ClipboardCheck size={size}/> : <Copy size={size}/>}
    </button>
  );
}

// A value (e.g. an ID) shown with an inline copy button.
export function CopyableId({ value, label, mono = true }: { value: string; label?: string; mono?: boolean }) {
  return (
    <span style={{ display: "inline-flex", alignItems: "center", gap: 4, minWidth: 0 }}>
      <span style={{ fontFamily: mono ? "var(--font-mono, monospace)" : undefined, overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }} title={value}>{value}</span>
      <CopyButton value={value} label={label}/>
    </span>
  );
}

// ─── SVG Components ───────────────────────────────────────────────────────────
export function CircleGauge({ pct, size = 72, color }: { pct: number; size?: number; color?: string }) {
  const r = (size - 10) / 2;
  const circ = 2 * Math.PI * r;
  const dash = Math.min(1, pct / 100) * circ;
  const c = color ?? (pct >= 90 ? "var(--status-good-icon)" : pct >= 70 ? "var(--status-warn-icon)" : "var(--status-error-icon)");
  return (
    <svg width={size} height={size} style={{ flexShrink: 0 }}>
      <circle cx={size/2} cy={size/2} r={r} fill="none" stroke="var(--color-border)" strokeWidth="6" />
      <circle cx={size/2} cy={size/2} r={r} fill="none" stroke={c} strokeWidth="6"
        strokeDasharray={`${dash} ${circ}`} strokeLinecap="round"
        transform={`rotate(-90 ${size/2} ${size/2})`} />
      <text x={size/2} y={size/2+5} textAnchor="middle" fontSize="13" fontWeight="700" fill={c}>
        {Math.round(pct)}%
      </text>
    </svg>
  );
}

export function LineChart({ data, color = "#3b82f6", onClick }: { data: { date: string; value: number }[]; color?: string, onClick?: (date: string) => void }) {
  const chartId = React.useId().replace(/:/g, "");
  const [hoverIdx, setHoverIdx] = useState<number | null>(null);

  if (data.length < 2) return <div className="chart-empty">Collecting trend data…</div>;

  const w = 500, h = 140;
  const pad = { t: 18, r: 16, b: 28, l: 42 };
  const cw = w - pad.l - pad.r, ch = h - pad.t - pad.b;
  const vals = data.map(d => d.value);
  const rawMin = Math.min(...vals), rawMax = Math.max(...vals) || 1;
  
  // Domain padding: the lowest point must never sit on the plot floor —
  // a real dip then reads as "crashed to zero" even when the value is 38%.
  const diff = rawMax - rawMin;
  const padAmt = Math.max(diff * 0.35, rawMax <= 100 ? 4 : Math.max(1, diff * 0.35));
  let min = rawMin - padAmt;
  let max = rawMax + padAmt * 0.6;
  if (rawMin >= 0) min = Math.max(0, min);
  if (rawMax <= 100 && rawMax > 1) max = Math.min(100, max);
  const range = max - min || 1;

  const pts = data.map((d, i) => ({
    x: pad.l + (i / (data.length - 1)) * cw,
    y: pad.t + ch - ((d.value - min) / range) * ch, ...d
  }));

  // Smooth monotone X curve
  let line = `M ${pts[0].x.toFixed(1)} ${pts[0].y.toFixed(1)}`;
  for (let i = 1; i < pts.length; i++) {
    const p0 = pts[i - 1], p1 = pts[i];
    const cx = (p0.x + p1.x) / 2;
    line += ` C ${cx.toFixed(1)},${p0.y.toFixed(1)} ${cx.toFixed(1)},${p1.y.toFixed(1)} ${p1.x.toFixed(1)},${p1.y.toFixed(1)}`;
  }

  const area = `${line} L ${pts.at(-1)!.x.toFixed(1)} ${(pad.t+ch).toFixed(1)} L ${pts[0].x.toFixed(1)} ${(pad.t+ch).toFixed(1)} Z`;

  // Only 3 Y-axis labels: min, mid, max — prevents overlap
  const yLabels = [min, min + range / 2, max].map((v) => ({
    v: Number.isInteger(v) ? Math.round(v) : +v.toFixed(1),
    y: pad.t + ch - ((v - min) / range) * ch
  }));

  // Show max 5 X labels, well spaced
  const maxXLabels = 5;
  const step = Math.max(1, Math.ceil(data.length / maxXLabels));

  // Format date for display — show "Jun 03" style
  const fmtLabel = (dateStr: string) => {
    try {
      const d = new Date(dateStr + (dateStr.includes("T") ? "" : "T00:00:00"));
      return d.toLocaleDateString("en-US", { month: "short", day: "numeric" });
    } catch { return dateStr.slice(5); }
  };

  return (
    <svg viewBox={`0 0 ${w} ${h}`} className="line-chart-svg" preserveAspectRatio="xMidYMid meet" style={{ width: "100%", height: "100%", display: "block" }} onMouseLeave={() => setHoverIdx(null)}
      role="img" aria-label={`Line chart, ${data.length} points, from ${data[0].value} (${data[0].date}) to ${data.at(-1)!.value} (${data.at(-1)!.date}); min ${rawMin}, max ${rawMax}`}>
      <defs>
        <linearGradient id={`grad-${chartId}`} x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" stopColor={color} stopOpacity="0.3" />
          <stop offset="100%" stopColor={color} stopOpacity="0.02" />
        </linearGradient>
      </defs>

      {/* Grid lines */}
      {yLabels.map((_, i) => <line key={`grid-${i}`} x1={pad.l} y1={yLabels[i].y} x2={w - pad.r} y2={yLabels[i].y} stroke="var(--color-border)" strokeWidth="0.8" strokeDasharray="4 3"/>)}

      {/* Y-axis labels */}
      {yLabels.map(({ v, y }, i) => <text key={`y-${i}`} x={pad.l - 6} y={y + 4} textAnchor="end" fontSize="10" fill="var(--color-muted, #94a3b8)" fontWeight="500">{v}</text>)}

      {/* Area fill and line */}
      <path d={area} fill={`url(#grad-${chartId})`} style={{ pointerEvents: "none" }} />
      <path d={line} fill="none" stroke={color} strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round" style={{ pointerEvents: "none" }}/>
      <circle cx={pts.at(-1)!.x} cy={pts.at(-1)!.y} r="4" fill="var(--color-bg, #fff)" stroke={color} strokeWidth="2" style={{ pointerEvents: "none" }} />

      {/* X-axis labels — short format, well spaced */}
      {data.map((d, i) => {
        if (i !== 0 && i !== data.length - 1 && i % step !== 0) return null;
        // Skip last if too close to previous step label
        if (i === data.length - 1 && i % step !== 0 && (data.length - 1) % step < step * 0.5) return null;
        const x = pad.l + (i / (data.length - 1)) * cw;
        return <text key={`x-${i}`} x={x} y={h - 2} textAnchor="middle" fontSize="9.5" fill="var(--color-muted, #94a3b8)" fontWeight="500">{fmtLabel(d.date)}</text>;
      })}

      {/* Hover hit zones */}
      {pts.map((p, i) => {
        const sliceW = cw / Math.max(data.length - 1, 1);
        return (
          <rect key={`hit-${i}`}
            x={i === 0 ? pad.l - 4 : p.x - sliceW / 2}
            y={0} width={i === 0 || i === pts.length - 1 ? sliceW / 2 + 4 : sliceW} height={h}
            fill="transparent" onMouseEnter={() => setHoverIdx(i)}
            onClick={() => onClick && onClick(p.date)}
            style={{ cursor: onClick ? "pointer" : "default" }}
          />
        );
      })}

      {/* Tooltip on hover */}
      {hoverIdx !== null && (
        <g pointerEvents="none" className="chart-tooltip">
          <line x1={pts[hoverIdx].x} y1={pad.t} x2={pts[hoverIdx].x} y2={pad.t + ch} stroke={color} strokeWidth="1" strokeDasharray="3 3" opacity="0.5" />
          <circle cx={pts[hoverIdx].x} cy={pts[hoverIdx].y} r="5" fill="var(--color-bg, #fff)" stroke={color} strokeWidth="2.5" />
          <g transform={`translate(${pts[hoverIdx].x > w * 0.65 ? pts[hoverIdx].x - 90 : pts[hoverIdx].x + 12}, ${Math.max(4, Math.min(pts[hoverIdx].y - 20, h - 48))})`}>
            <rect x="0" y="0" width="82" height="42" rx="6" fill="var(--color-card, #fff)" stroke="var(--color-border)" strokeWidth="1" style={{ filter: "drop-shadow(0 2px 8px rgba(0,0,0,0.12))" }} />
            <text x="41" y="16" textAnchor="middle" fontSize="9.5" fill="var(--color-muted, #94a3b8)" fontWeight="600">{fmtLabel(pts[hoverIdx].date)}</text>
            <text x="41" y="33" textAnchor="middle" fontSize="15" fill="var(--color-text)" fontWeight="700">{pts[hoverIdx].value}</text>
          </g>
        </g>
      )}
    </svg>
  );
}

export function MiniBarChart({ items }: { items: { label: string; value: number; color?: string }[] }) {
  const max = Math.max(...items.map(i => i.value), 1);
  return (
    <div className="mini-bar-chart">
      {items.map(item => (
        <div key={item.label} className="mbc-row">
          <span className="mbc-label">{item.label}</span>
          <div className="mbc-track">
            <div className="mbc-fill" style={{ width: `${(item.value/max)*100}%`, background: item.color ?? "#3b82f6" }} />
          </div>
          <span className="mbc-val">{item.value}</span>
        </div>
      ))}
    </div>
  );
}

// ─── Primitives ───────────────────────────────────────────────────────────────
export function Badge({ label, tone }: { label: string; tone: Tone }) {
  return <span className={`badge badge-${tone}`}>{label}</span>;
}

export function StatusDot({ status }: { status: Tone }) {
  const c = { good:"var(--status-good-icon)", warning:"var(--status-warn-icon)", error:"var(--status-error-icon)", neutral:"var(--color-faint)", info:"var(--color-primary)" }[status];
  return <span className="status-dot" style={{ background: c }} />;
}

export function StatBox({ value, label, color, sub }: { value: string|number; label: string; color?: string; sub?: string }) {
  return (
    <div className="stat-box">
      <div className="stat-val" style={color ? { color } : undefined}>{value}</div>
      <div className="stat-lbl">{label}</div>
      {sub && <div className="stat-sub">{sub}</div>}
    </div>
  );
}

export function Card({ title, badge, action, children, className="" }:
  { title: string; badge?: React.ReactNode; action?: React.ReactNode; children: React.ReactNode; className?: string }) {
  return (
    <div className={`card ${className}`}>
      <div className="card-head">
        <span className="card-title">{title}</span>
        <div className="card-head-right">{badge}{action}</div>
      </div>
      {children}
    </div>
  );
}

export function KpiTile({ icon, label, value, sub, tone="neutral", needsPerm, onClick, active }:
  { icon: React.ReactNode; label: string; value: React.ReactNode; sub?: string; tone?: Tone; needsPerm?: boolean; onClick?: () => void; active?: boolean }) {
  const inner = (
    <>
      <div className="kpi-icon">{icon}</div>
      <div className="kpi-body">
        <div className="kpi-label">{label}</div>
        <div className="kpi-value">{value}</div>
        {needsPerm
          ? <div className="kpi-perm"><Lock size={9}/> Needs permission</div>
          : sub && <div className="kpi-sub" title={sub}>{sub}</div>}
      </div>
    </>
  );
  if (onClick) {
    return (
      <button
        type="button"
        className={`kpi-tile kpi-${tone} kpi-clickable${active ? " kpi-active" : ""}`}
        onClick={onClick}
        title={`${label} — click to filter`}
        style={{ textAlign: "left", font: "inherit", cursor: "pointer", width: "100%" }}
      >
        {inner}
      </button>
    );
  }
  return (
    <div className={`kpi-tile kpi-${tone}`}>
      {inner}
    </div>
  );
}

export function StateMessage({ type = "empty", title, message, icon, onAction, actionLabel }: { type?: "empty"|"error"|"permission", title?: string, message: React.ReactNode, icon?: React.ReactNode, onAction?: ()=>void, actionLabel?: string }) {
  const defaultIcon = type === "empty" ? <Activity size={28}/> : type === "error" ? <AlertTriangle size={28}/> : <Lock size={28}/>;
  return (
    <div className={`state-message state-${type}`}>
      <div className="sm-icon">{icon ?? defaultIcon}</div>
      {title && <div className="sm-title">{title}</div>}
      <div className="sm-body">{message}</div>
      {onAction && actionLabel && (
        <div className="sm-action"><button onClick={onAction}>{actionLabel}</button></div>
      )}
    </div>
  );
}

export function LoadingSkeleton({ type = "card" }: { type?: "card"|"table"|"kpi"|"list" }) {
  if (type === "kpi") return <div className="skeleton-box" style={{ height: "90px" }}/>;
  if (type === "table") return (
    <div className="skeleton-container">
      <div className="skeleton-row"><div className="skeleton-box" style={{ flex: 1, height: "30px" }}/></div>
      <div className="skeleton-row"><div className="skeleton-box" style={{ flex: 1, height: "40px" }}/></div>
      <div className="skeleton-row"><div className="skeleton-box" style={{ flex: 1, height: "40px" }}/></div>
    </div>
  );
  if (type === "list") return (
    <div className="skeleton-container">
      <div className="skeleton-row"><div className="skeleton-circle" style={{ width: "24px", height: "24px" }}/><div className="skeleton-box" style={{ flex: 1, height: "20px" }}/></div>
      <div className="skeleton-row"><div className="skeleton-circle" style={{ width: "24px", height: "24px" }}/><div className="skeleton-box" style={{ flex: 1, height: "20px" }}/></div>
    </div>
  );
  return <div className="skeleton-box" style={{ height: "200px" }}/>;
}

export function DashboardSkeleton() {
  return (
    <div className="page" style={{ padding: "24px" }}>
      <div className="kpi-row">
        <LoadingSkeleton type="kpi" />
        <LoadingSkeleton type="kpi" />
        <LoadingSkeleton type="kpi" />
        <LoadingSkeleton type="kpi" />
        <LoadingSkeleton type="kpi" />
        <LoadingSkeleton type="kpi" />
      </div>
      <div className="mid-row" style={{ marginTop: "24px" }}>
        <LoadingSkeleton type="card" />
        <LoadingSkeleton type="card" />
      </div>
    </div>
  );
}

export function InlineError({ title, perm, message }: { title: string; perm?: string; message?: string }) {
  return (
    <StateMessage 
      type={perm ? "permission" : "error"}
      title={title}
      message={
        <>
          {message ?? "This data source returned an error."}
          {perm && <> Add <code>{perm}</code> permission in Azure Portal &rarr; App Registrations &rarr; API Permissions, grant admin consent, then restart the API.</>}
        </>
      }
    />
  );
}

// ─── Detail Modal ─────────────────────────────────────────────────────────────
export function DetailField({ label, value, copy, copyValue, title, onNavigate, navLabel }: { label: string; value?: React.ReactNode; copy?: boolean; copyValue?: string; title?: string; onNavigate?: () => void; navLabel?: string }) {
  if (value === undefined || value === null || value === "") return null;
  const rawStr = typeof value === "string" ? value : (copyValue ?? "");
  return (
    <div className="detail-field">
      <span className="detail-label">{label}</span>
      <span className="detail-value" style={{ display: "flex", alignItems: "center", gap: 6, minWidth: 0, flexWrap: "wrap" }}>
        <span style={{ overflow: "hidden", textOverflow: "ellipsis" }} title={title ?? (typeof value === "string" ? value : undefined)}>{value}</span>
        {copy && rawStr && <CopyButton value={copyValue ?? rawStr} label={label}/>}
        {onNavigate && (
          <button type="button" onClick={onNavigate} style={{ background: "none", border: "1px solid var(--color-border)", borderRadius: 4, padding: "1px 6px", fontSize: 11, cursor: "pointer", color: "var(--color-primary)", marginLeft: 4, display: "inline-flex", alignItems: "center", gap: 3 }}>
            {navLabel ?? "View →"}
          </button>
        )}
      </span>
    </div>
  );
}

export function DetailModal({ title, subtitle, onClose, portalUrl, portalLabel, children }: {
  title: string;
  subtitle?: string;
  onClose: () => void;
  portalUrl?: string;
  portalLabel?: string;
  children: React.ReactNode;
}) {
  const panelRef = React.useRef<HTMLDivElement>(null);
  useEffect(() => {
    const previouslyFocused = document.activeElement as HTMLElement | null;
    const panel = panelRef.current;
    // Move focus into the dialog, and trap Tab within it.
    panel?.querySelector<HTMLElement>("button, a, input, select, [tabindex]")?.focus();
    const handler = (e: KeyboardEvent) => {
      if (e.key === "Escape") { onClose(); return; }
      if (e.key === "Tab" && panel) {
        const focusable = panel.querySelectorAll<HTMLElement>('button, a[href], input, select, textarea, [tabindex]:not([tabindex="-1"])');
        if (focusable.length === 0) return;
        const first = focusable[0], last = focusable[focusable.length - 1];
        if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
        else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
      }
    };
    window.addEventListener("keydown", handler);
    return () => { window.removeEventListener("keydown", handler); previouslyFocused?.focus?.(); };
  }, [onClose]);
  return (
    <div className="detail-modal-backdrop" onClick={onClose}>
      <div className="detail-modal" ref={panelRef} onClick={e => e.stopPropagation()}
        role="dialog" aria-modal="true" aria-label={title}>
        <div className="detail-modal-hdr">
          <div style={{ flex: 1, minWidth: 0 }}>
            <div className="dm-title">{title}</div>
            {subtitle && <div className="dm-sub">{subtitle}</div>}
          </div>
          <button className="modal-close" onClick={onClose} aria-label="Close"><X size={16}/></button>
        </div>
        <div className="detail-modal-body">{children}</div>
        <div className="detail-modal-footer">
          {portalUrl && (
            <a href={portalUrl} target="_blank" rel="noopener noreferrer" className="dm-portal-btn">
              <ExternalLink size={13}/>{portalLabel ?? "Open in portal"} →
            </a>
          )}
          <button className="dm-close-btn" onClick={onClose}>Close</button>
        </div>
      </div>
    </div>
  );
}

export function SectHdr({ children }: { children: React.ReactNode }) {
  return <div className="sect-hdr">{children}</div>;
}

export function EmptyState({ icon, message }: { icon?: React.ReactNode; message: string }) {
  return <StateMessage type="empty" message={message} icon={icon} />;
}

export function ProgressBar({ pct, color }: { pct: number; color?: string }) {
  const c = color ?? (pct>=90?"var(--status-good-icon)":pct>=70?"var(--status-warn-icon)":"var(--status-error-icon)");
  return (
    <div className="prog-track">
      <div className="prog-fill" style={{ width:`${Math.min(100,pct)}%`, background:c }}/>
    </div>
  );
}

export function InfoRow({ label, value, tone }: { label: string; value: React.ReactNode; tone?: Tone }) {
  return (
    <div className="info-row">
      <span className="info-label">{label}</span>
      <span className={`info-value ${tone?`tone-${tone}`:""}`}>{value}</span>
    </div>
  );
}

// ─── Alert Detail Modal ───────────────────────────────────────────────────────
export function AlertDetailModal({ alert, allAlerts, onSelectAlert, onClose }: {
  alert: SecurityAlert;
  allAlerts?: SecurityAlert[];
  onSelectAlert?: (a: SecurityAlert) => void;
  onClose: () => void;
}) {
  const sevTone: Tone = alert.severity === "Critical" || alert.severity === "High" ? "error"
    : alert.severity === "Medium" ? "warning"
    : alert.severity === "Low" ? "info" : "neutral";

  // Investigation context: other open alerts touching the same user or device.
  const entity = alert.userPrincipalName || alert.deviceName;
  const related = (allAlerts ?? []).filter(a =>
    a.id !== alert.id && !a.isResolved && !!entity &&
    (a.userPrincipalName === alert.userPrincipalName && !!alert.userPrincipalName ||
     a.deviceName === alert.deviceName && !!alert.deviceName)
  ).slice(0, 6);

  const portalUrl = alert.portalUrl ?? (() => {
    if (alert.service === "Intune") {
      return "https://intune.microsoft.com/#view/Microsoft_Intune_Devices/DevicesMenu";
    }
    if (alert.service === "EntraId") {
      if (alert.alertType === "RiskyUser") return "https://entra.microsoft.com/#view/Microsoft_AAD_IAM/RiskyUsersBlade";
      if (alert.alertType === "RiskySignIn") return "https://entra.microsoft.com/#view/Microsoft_AAD_IAM/RiskySignInsBlade";
      if (alert.alertType === "MfaStatus" && alert.userPrincipalName)
        return `https://entra.microsoft.com/#view/Microsoft_AAD_IAM/UsersManagementMenuBlade/~/MsGraphUsers`;
    }
    return "https://security.microsoft.com/alerts";
  })();

  const portalLabel = alert.service === "Intune" ? "View in Intune"
    : alert.service === "EntraId" ? "View in Entra ID"
    : "View in M365 Portal";

  return (
    <DetailModal
      title={alert.title}
      subtitle={`${fmtService(alert.service)} · ${alert.alertType}`}
      onClose={onClose}
      portalUrl={portalUrl}
      portalLabel={portalLabel}
    >
      <DetailField label="Alert ID" value={String(alert.id)} copy/>
      <DetailField label="Alert Type" value={alert.alertType}/>
      <DetailField label="Severity" value={alert.severity}/>
      <DetailField label="Service" value={fmtService(alert.service)}/>
      <DetailField label="Status" value={alert.isResolved ? "Resolved" : "Active"}/>
      <DetailField label="User" value={alert.userPrincipalName} copy={!!alert.userPrincipalName}/>
      <DetailField label="Device" value={alert.deviceName} copy={!!alert.deviceName}/>
      <DetailField label="External ID" value={alert.externalId} copy={!!alert.externalId}/>
      <DetailField label="Detected" value={alert.detectedAt ? `${relTime(alert.detectedAt)} (${fmtDate(alert.detectedAt)})` : undefined} title={fmtDate(alert.detectedAt)}/>
      <DetailField label="Last Updated" value={alert.lastUpdatedAt ? `${relTime(alert.lastUpdatedAt)} (${fmtDate(alert.lastUpdatedAt)})` : undefined} title={fmtDate(alert.lastUpdatedAt)}/>
      {alert.description && (
        <>
          <div className="dm-section-hdr">Description</div>
          <div className="dm-desc-block">{alert.description}</div>
        </>
      )}
      <div style={{display:"flex",gap:6,flexWrap:"wrap",marginTop:4}}>
        <Badge label={alert.severity} tone={sevTone}/>
        <Badge label={fmtService(alert.service)} tone="neutral"/>
        <Badge label={alert.isResolved ? "Resolved" : "Active"} tone={alert.isResolved ? "good" : "error"}/>
      </div>
      {entity && (
        <>
          <div className="dm-section-hdr">Related open alerts for {alert.userPrincipalName ? "this user" : "this device"}</div>
          {related.length === 0 ? (
            <div style={{ fontSize: 12, color: "var(--color-muted)", padding: "4px 0" }}>
              No other open alerts involve {entity}.
            </div>
          ) : (
            <div className="mini-list">
              {related.map(r => (
                <div key={r.id} className="mini-row al-clickable"
                  onClick={() => onSelectAlert?.(r)} style={{ cursor: onSelectAlert ? "pointer" : "default" }}
                  role={onSelectAlert ? "button" : undefined} tabIndex={onSelectAlert ? 0 : undefined}
                  onKeyDown={e => { if (onSelectAlert && e.key === "Enter") onSelectAlert(r); }}>
                  <span className={`sev-dot sev-${r.severity.toLowerCase()}`}/>
                  <span className="mr-user" style={{ flex: 1 }}>{r.title}</span>
                  <Badge label={fmtService(r.service)} tone="neutral"/>
                </div>
              ))}
            </div>
          )}
        </>
      )}
    </DetailModal>
  );
}
