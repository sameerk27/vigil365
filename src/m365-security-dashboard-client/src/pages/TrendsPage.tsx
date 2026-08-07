import React, { useEffect, useState, useMemo } from "react";
import { TrendingUp, TrendingDown, Minus, Info, Shield, Users, Smartphone, ShieldAlert, Activity, Download, Printer, FileText, ChevronDown, ChevronUp, BarChart3, AlertTriangle, CheckCircle } from "lucide-react";
import { Card, LineChart, StateMessage, LoadingSkeleton, InlineError, CircleGauge, Badge } from "../components/SharedComponents";
import { useAuth, apiFetch, apiBase } from "../services/api";
import { fmtDate } from "../services/utils";
import { selectTrendWindow } from "../services/trendWindow";

type TrendSnapshot = {
  id: string;
  capturedAt: string;
  riskyUsersCount: number;
  mfaCoveragePct: number;
  nonCompliantDevicesCount: number;
  criticalAlertsCount: number;
  highAlertsCount: number;
  secureScorePct: number;
  complianceIssuesCount: number;
};

// ─── Metric Configuration ─────────────────────────────────────────────────────
const METRICS = [
  { key: "secureScorePct" as const, label: "Secure Score", unit: "%", color: "#3b82f6", icon: Shield, lowerIsBetter: false, isPct: true, description: "Microsoft Secure Score percentage — measures overall security configuration posture across Identity, Devices, Apps, and Data." },
  { key: "riskyUsersCount" as const, label: "Risky Users", unit: "", color: "#ef4444", icon: Users, lowerIsBetter: true, isPct: false, description: "Users flagged by Azure AD Identity Protection with medium or high risk levels due to compromised credentials, leaked credentials, or anomalous sign-in patterns." },
  { key: "nonCompliantDevicesCount" as const, label: "Non-Compliant Devices", unit: "", color: "#f59e0b", icon: Smartphone, lowerIsBetter: true, isPct: false, description: "Devices failing one or more Intune compliance policies (e.g., missing encryption, outdated OS, no PIN, jailbroken)." },
  { key: "mfaCoveragePct" as const, label: "MFA Coverage", unit: "%", color: "#10b981", icon: CheckCircle, lowerIsBetter: false, isPct: true, description: "Percentage of enabled users registered for Multi-Factor Authentication. Target: ≥99% for all users." },
  { key: "criticalAlertsCount" as const, label: "Critical Alerts", unit: "", color: "#dc2626", icon: ShieldAlert, lowerIsBetter: true, isPct: false, description: "Active unresolved security alerts with Critical severity from Microsoft 365 Defender, Azure AD, and Intune." },
  { key: "highAlertsCount" as const, label: "High Alerts", unit: "", color: "#ea580c", icon: AlertTriangle, lowerIsBetter: true, isPct: false, description: "Active unresolved security alerts with High severity across all integrated Microsoft 365 security services." },
  { key: "complianceIssuesCount" as const, label: "Compliance Issues", unit: "", color: "#8b5cf6", icon: FileText, lowerIsBetter: true, isPct: false, description: "DLP policy violations, Insider Risk alerts, and Microsoft Purview compliance findings requiring attention." },
] as const;

export function TrendsPage() {
  const [snapshots, setSnapshots] = useState<TrendSnapshot[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [timeRange, setTimeRange] = useState<number>(30);
  const [showTable, setShowTable] = useState(false);

  useEffect(() => {
    let active = true;
    const fetchTrends = async () => {
      try {
        const res = await apiFetch(`${apiBase}/api/dashboard/trends`);
        if (!res.ok) throw new Error("Failed to load trends data");
        const data = await res.json();
        if (active) { setSnapshots(data); setLoading(false); }
      } catch (err: any) {
        if (active) { setError(err.message); setLoading(false); }
      }
    };
    fetchTrends();
    return () => { active = false; };
  }, []);

  // A window holding fewer than two snapshots cannot be plotted. Substituting
  // the last two silently — which is what this used to do — draws a line
  // labelled "7 days" out of data captured outside those 7 days. selectTrendWindow
  // reports when it fell back so every place that states the range can say so.
  const { items: filteredSnapshots, usingFallback } = useMemo(
    () => selectTrendWindow(snapshots, timeRange, s => s.capturedAt),
    [snapshots, timeRange]);

  const reversedSnapshots = useMemo(() => [...filteredSnapshots].reverse(), [filteredSnapshots]);

  // ─── Computed analytics ───────────────────────────────────────────────────────
  const latest = filteredSnapshots[filteredSnapshots.length - 1];
  const oldest = filteredSnapshots[0];
  const previous = filteredSnapshots.length > 1 ? filteredSnapshots[filteredSnapshots.length - 2] : null;

  const getDelta = (cur: number, prev: number | undefined) => {
    if (prev === undefined || prev === cur) return { diff: 0, pctChange: 0 };
    const diff = cur - prev;
    const pctChange = prev !== 0 ? (diff / prev) * 100 : diff > 0 ? 100 : -100;
    return { diff, pctChange };
  };

  const getPeriodDelta = (cur: number, old: number) => {
    const diff = cur - old;
    const pctChange = old !== 0 ? (diff / old) * 100 : diff > 0 ? 100 : diff < 0 ? -100 : 0;
    return { diff, pctChange };
  };

  // ─── Auto-generated insights ──────────────────────────────────────────────────
  const insights = useMemo(() => {
    if (!latest || !oldest || filteredSnapshots.length < 2) return [];
    const result: { type: "good" | "warning" | "critical" | "info"; text: string }[] = [];

    // Secure Score
    const ssDelta = getPeriodDelta(latest.secureScorePct, oldest.secureScorePct);
    if (ssDelta.diff > 0) result.push({ type: "good", text: `Secure Score improved by ${ssDelta.diff.toFixed(1)} points over the selected period (${oldest.secureScorePct.toFixed(1)}% → ${latest.secureScorePct.toFixed(1)}%).` });
    else if (ssDelta.diff < 0) result.push({ type: "critical", text: `Secure Score declined by ${Math.abs(ssDelta.diff).toFixed(1)} points over the selected period (${oldest.secureScorePct.toFixed(1)}% → ${latest.secureScorePct.toFixed(1)}%). Review recent security configuration changes.` });

    // Risky Users
    const ruDelta = getPeriodDelta(latest.riskyUsersCount, oldest.riskyUsersCount);
    if (latest.riskyUsersCount > 0 && ruDelta.diff > 0) result.push({ type: "critical", text: `Risky users increased by ${ruDelta.diff} (${ruDelta.pctChange > 0 ? "+" : ""}${ruDelta.pctChange.toFixed(0)}%). Investigate Identity Protection findings and enforce password resets.` });
    else if (latest.riskyUsersCount === 0) result.push({ type: "good", text: "No risky users detected — Identity Protection posture is clean." });

    // MFA
    if (latest.mfaCoveragePct < 95) result.push({ type: "warning", text: `MFA coverage is at ${latest.mfaCoveragePct.toFixed(1)}%, below the recommended 95% threshold. ${(100 - latest.mfaCoveragePct).toFixed(1)}% of users remain unprotected.` });
    else if (latest.mfaCoveragePct >= 99) result.push({ type: "good", text: `MFA coverage is at ${latest.mfaCoveragePct.toFixed(1)}% — excellent coverage across the organization.` });

    // Device compliance
    if (latest.nonCompliantDevicesCount > 5) result.push({ type: "warning", text: `${latest.nonCompliantDevicesCount} devices are non-compliant. Review Intune compliance policies and enforce remediation.` });
    else if (latest.nonCompliantDevicesCount === 0) result.push({ type: "good", text: "All managed devices are compliant with Intune policies." });

    // Critical alerts trend
    if (latest.criticalAlertsCount > 0) result.push({ type: "critical", text: `${latest.criticalAlertsCount} critical alert${latest.criticalAlertsCount > 1 ? "s" : ""} currently active. Immediate investigation recommended.` });

    // Data coverage — state what is actually plotted, not what was requested.
    result.push(usingFallback
      ? { type: "warning", text: `Not enough history for the last ${timeRange} days — showing the ${filteredSnapshots.length} most recent snapshots instead, which fall outside that window. Trend figures below cover only that shorter span.` }
      : { type: "info", text: `Showing ${filteredSnapshots.length} data points over ${timeRange} days. One snapshot is captured per collection cycle (15 minutes by default).` });

    return result;
  }, [filteredSnapshots, latest, oldest, timeRange, usingFallback]);

  // ─── Export CSV ────────────────────────────────────────────────────────────────
  const exportCsv = () => {
    if (!filteredSnapshots.length) return;
    const headers = ["Date", ...METRICS.map(m => m.label)].join(",");
    const rows = filteredSnapshots.map(s =>
      [s.capturedAt.substring(0, 10), ...METRICS.map(m => s[m.key])].join(",")
    );
    const csv = [headers, ...rows].join("\n");
    const blob = new Blob([csv], { type: "text/csv" });
    const url = window.URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.setAttribute("href", url);
    a.setAttribute("download", `vigil365-trends-${timeRange}days-${new Date().toISOString().slice(0,10)}.csv`);
    a.click();
    window.URL.revokeObjectURL(url);
  };

  const toChartData = (key: keyof TrendSnapshot) => filteredSnapshots.map(s => ({
    date: s.capturedAt.substring(0, 10),
    value: s[key] as number
  }));

  // ─── Loading state ────────────────────────────────────────────────────────────
  if (loading) {
    return (
      <div className="page" data-inline-style="inline-64ede2cab0">
        <div data-inline-style="inline-fec3619ed0"><LoadingSkeleton type="card" /></div>
        {/* One slot per metric, derived — a hardcoded five made the layout jump
            when the seven real tiles arrived. */}
        <div className="kpi-row">{METRICS.map(m => <LoadingSkeleton key={m.key} type="kpi" />)}</div>
        <div data-inline-style="inline-321d1ea5c6"><LoadingSkeleton type="card" /></div>
        <div className="mid-row" data-inline-style="inline-b034d55537"><LoadingSkeleton type="card" /><LoadingSkeleton type="card" /><LoadingSkeleton type="card" /></div>
      </div>
    );
  }

  if (error) return <div className="page"><InlineError title="Failed to load Trends" message={error} /></div>;

  if (snapshots.length === 0) {
    return (
      <div className="page" data-inline-style="inline-64ede2cab0">
        <Card title="Trends & History">
          <StateMessage type="empty" title="No history yet" message="Run a collection cycle to capture the first trend snapshot. Historical data is captured automatically on each cycle." icon={<TrendingUp size={32} color="var(--color-muted)"/>} />
        </Card>
      </div>
    );
  }

  // ─── Helper components ────────────────────────────────────────────────────────
  const KpiCard = ({ metric }: { metric: typeof METRICS[number] }) => {
    const cur = latest?.[metric.key] as number;
    const prev = previous?.[metric.key] as number | undefined;
    const periodOld = oldest?.[metric.key] as number;
    const { diff } = getDelta(cur, prev);
    const periodDelta = getPeriodDelta(cur, periodOld);
    const improving = metric.lowerIsBetter ? diff <= 0 : diff >= 0;
    const periodImproving = metric.lowerIsBetter ? periodDelta.diff <= 0 : periodDelta.diff >= 0;
    const Icon = metric.icon;
    const ArrowIcon = diff > 0 ? TrendingUp : diff < 0 ? TrendingDown : Minus;
    const dirColor = diff === 0 ? "var(--color-muted)" : improving ? "#10b981" : "#ef4444";

    return (
      <div className="trends-kpi-card" title={metric.description}>
        <div style={{ display: "flex", alignItems: "center", gap: 8, marginBottom: 12 }}>
          <div style={{ width: 32, height: 32, borderRadius: 8, background: `${metric.color}18`, display: "flex", alignItems: "center", justifyContent: "center" }}>
            <Icon size={16} color={metric.color} />
          </div>
          <span data-inline-style="inline-7b438bee7b">{metric.label}</span>
        </div>
        <div data-inline-style="inline-228481f666">
          {metric.isPct ? cur.toFixed(1) : cur}{metric.unit && <span data-inline-style="inline-a02a17fba3">{metric.unit}</span>}
        </div>
        <div data-inline-style="inline-a93a24d0aa">
          <ArrowIcon size={14} color={dirColor} />
          <span style={{ color: dirColor, fontWeight: 600 }}>
            {diff === 0 ? "No change" : `${diff > 0 ? "+" : ""}${metric.isPct ? diff.toFixed(1) : diff}`}
          </span>
          {/* Snapshots are one collection cycle apart (~15 min by default), so
              this is the most recent sample-to-sample move, not a trend. Saying
              "vs previous" invited reading normal jitter as direction. */}
          <span data-inline-style="inline-26de0ebb15" title="Change since the previous snapshot — one collection cycle, not a trend">
            vs last snapshot
          </span>
        </div>
        <div style={{ marginTop: 4, fontSize: 11, color: "var(--color-muted)" }}>
          <span style={{ color: periodImproving ? "#10b981" : periodDelta.diff === 0 ? "var(--color-muted)" : "#ef4444", fontWeight: 600 }}>
            {periodDelta.diff === 0 ? "Flat" : `${periodDelta.diff > 0 ? "+" : ""}${periodDelta.pctChange.toFixed(1)}%`}
          </span> over {timeRange}d
        </div>
      </div>
    );
  };

  const InsightRow = ({ insight }: { insight: { type: string; text: string } }) => {
    const iconMap: Record<string, React.ReactNode> = {
      good: <CheckCircle size={14} color="#10b981" />,
      warning: <AlertTriangle size={14} color="#f59e0b" />,
      critical: <ShieldAlert size={14} color="#ef4444" />,
      info: <Info size={14} color="#3b82f6" />,
    };
    const bgMap: Record<string, string> = {
      good: "rgba(16,185,129,0.08)", warning: "rgba(245,158,11,0.08)",
      critical: "rgba(239,68,68,0.08)", info: "rgba(59,130,246,0.06)",
    };
    const borderMap: Record<string, string> = {
      good: "#10b981", warning: "#f59e0b", critical: "#ef4444", info: "#3b82f6",
    };
    return (
      <div style={{ display: "flex", alignItems: "flex-start", gap: 10, padding: "10px 14px", background: bgMap[insight.type], borderLeft: `3px solid ${borderMap[insight.type]}`, borderRadius: "0 6px 6px 0", fontSize: 13, color: "var(--color-text)", lineHeight: 1.5 }}>
        <span style={{ marginTop: 2, flexShrink: 0 }}>{iconMap[insight.type]}</span>
        <span>{insight.text}</span>
      </div>
    );
  };

  return (
    <div className="page trends-page" data-inline-style="inline-64ede2cab0">

      {/* ─── Report Header (visible always, styled for print) ─── */}
      <div className="trends-report-header print-only" data-inline-style="inline-6e22c58a7a">
        <h1 data-inline-style="inline-4ab7779bc6">Vigil365 — Security Trends & History Report</h1>
        <p data-inline-style="inline-f0d87d4de4">
          Generated {new Date().toLocaleDateString("en-US", { weekday: "long", year: "numeric", month: "long", day: "numeric" })} · Period: {usingFallback ? `${filteredSnapshots.length} most recent snapshots (insufficient history for ${timeRange} days)` : `Last ${timeRange} days`} · {filteredSnapshots.length} data points
        </p>
      </div>

      {/* ─── Page Header ─── */}
      <div data-inline-style="inline-44c27107bd">
        <div>
          <h2 data-inline-style="inline-97c01e73be">
            <BarChart3 size={22} color="var(--color-primary)" /> Trends & History
          </h2>
          <p data-inline-style="inline-fcb52039cc">
            Security posture tracking across {filteredSnapshots.length} snapshots · Last capture: {latest?.capturedAt ? fmtDate(latest.capturedAt) : "—"}
          </p>
        </div>
        <div className="trends-controls no-print" data-inline-style="inline-553d16a161">
          <div data-inline-style="inline-b085e04e16">
            {[7, 30, 90].map(days => (
              <button key={days} onClick={() => setTimeRange(days)}
                style={{
                  padding: "6px 14px", borderRadius: 6, border: "none", fontSize: 12, fontWeight: 600, cursor: "pointer",
                  background: timeRange === days ? "var(--color-primary)" : "transparent",
                  color: timeRange === days ? "#fff" : "var(--color-text)",
                  transition: "all 0.2s"
                }}>
                {days}D
              </button>
            ))}
          </div>
          <div data-inline-style="inline-d240bddbe0" />
          <button onClick={exportCsv} className="trends-action-btn" title="Export all trend data as CSV">
            <Download size={14} /> CSV
          </button>
          <button onClick={() => window.print()} className="trends-action-btn" title="Print or save as PDF">
            <Printer size={14} /> Print
          </button>
        </div>
      </div>

      {/* ─── Executive Summary KPI Row ─── */}
      <div className="trends-kpi-grid">
        {METRICS.map(m => <KpiCard key={m.key} metric={m} />)}
      </div>

      {/* ─── Hero Chart: Secure Score ─── */}
      <Card title="Secure Score Trend" className="trends-hero-chart">
        <div data-inline-style="inline-6f5dc4ccd9">
          <div data-inline-style="inline-b27220fc04">
            <div data-inline-style="inline-0a5009a32d">
              <span data-inline-style="inline-485ddc26bf">{latest?.secureScorePct.toFixed(1)}</span>
              <span data-inline-style="inline-cb698dfdf8">%</span>
            </div>
            <CircleGauge pct={latest?.secureScorePct ?? 0} size={52} />
            <div data-inline-style="inline-13d9343556">
              {METRICS[0].description}
            </div>
          </div>
        </div>
        <div data-inline-style="inline-b6edae67dd">
          <LineChart data={toChartData("secureScorePct")} color="#3b82f6" />
        </div>
      </Card>

      {/* ─── Risk Metrics Grid ─── */}
      <h3 data-inline-style="inline-004b7689b0">
        <Activity size={16} color="var(--color-primary)" /> Risk & Compliance Metrics
      </h3>
      <div className="trends-chart-grid">
        {METRICS.slice(1).map(m => (
          <div key={m.key} className="trends-chart-card">
            <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", padding: "12px 16px 0" }}>
              <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
                <div style={{ width: 8, height: 8, borderRadius: "50%", background: m.color }} />
                <span style={{ fontSize: 13, fontWeight: 600, color: "var(--color-text)" }}>{m.label}</span>
              </div>
              <div data-inline-style="inline-360f4d7d08">
                <span data-inline-style="inline-3c624a053a">
                  {m.isPct ? (latest?.[m.key] as number).toFixed(1) : latest?.[m.key]}
                </span>
                {m.unit && <span data-inline-style="inline-af7da65b76">{m.unit}</span>}
              </div>
            </div>
            <p data-inline-style="inline-63b7e5b6b6">{m.description}</p>
            <div data-inline-style="inline-367739a4c5">
              <LineChart data={toChartData(m.key)} color={m.color} />
            </div>
          </div>
        ))}
      </div>

      {/* ─── Automated Insights ─── */}
      <h3 data-inline-style="inline-004b7689b0">
        <Info size={16} color="var(--color-primary)" /> Automated Insights
      </h3>
      <div data-inline-style="inline-f754d9eb46">
        {insights.map((insight, i) => <InsightRow key={i} insight={insight} />)}
      </div>

      {/* ─── Historical Data Table ─── */}
      <div className="no-print">
        <button onClick={() => setShowTable(!showTable)}
          style={{ display: "flex", alignItems: "center", gap: 8, padding: "10px 16px", background: "var(--color-raised)", border: "1px solid var(--color-border)", borderRadius: 8, cursor: "pointer", fontSize: 13, fontWeight: 600, color: "var(--color-text)", width: "100%", marginBottom: showTable ? 0 : 24 }}>
          {showTable ? <ChevronUp size={14} /> : <ChevronDown size={14} />}
          Historical Data Table ({filteredSnapshots.length} records)
        </button>
      </div>
      {showTable && (
        <div data-inline-style="inline-94a4ef55d7">
          <table data-inline-style="inline-f767132568">
            <thead>
              <tr style={{ background: "var(--color-raised)" }}>
                <th scope="col" style={thStyle}>Date</th>
                {METRICS.map(m => <th key={m.key} style={thStyle}>{m.label}</th>)}
              </tr>
            </thead>
            <tbody>
              {reversedSnapshots.map((s, i) => (
                <tr key={s.id} style={{ background: i % 2 === 0 ? "transparent" : "var(--color-raised)" }}>
                  <td style={tdStyle}>{fmtDate(s.capturedAt)}</td>
                  {METRICS.map(m => {
                    const val = s[m.key] as number;
                    const prevRow = i < reversedSnapshots.length - 1 ? reversedSnapshots[i + 1] : null;
                    const prevVal = prevRow ? prevRow[m.key] as number : null;
                    const diff = prevVal !== null ? val - prevVal : 0;
                    const improving = diff === 0 ? null : m.lowerIsBetter ? diff < 0 : diff > 0;
                    return (
                      <td key={m.key} style={tdStyle}>
                        <span style={{ fontWeight: 600 }}>{m.isPct ? val.toFixed(1) : val}{m.unit}</span>
                        {diff !== 0 && (
                          <span style={{ marginLeft: 6, fontSize: 10, color: improving ? "#10b981" : "#ef4444", fontWeight: 600 }}>
                            {diff > 0 ? "▲" : "▼"}{m.isPct ? Math.abs(diff).toFixed(1) : Math.abs(diff)}
                          </span>
                        )}
                      </td>
                    );
                  })}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {/* ─── Print-only data table (always included in PDF) ─── */}
      <div className="print-only" data-inline-style="inline-6e22c58a7a">
        <h3 data-inline-style="inline-cb5894d92d">Historical Data</h3>
        <table data-inline-style="inline-5f1b134668">
          <thead>
            <tr>
              <th scope="col" style={{ ...thStyle, fontSize: 10, padding: "4px 6px" }}>Date</th>
              {METRICS.map(m => <th key={m.key} style={{ ...thStyle, fontSize: 10, padding: "4px 6px" }}>{m.label}</th>)}
            </tr>
          </thead>
          <tbody>
            {reversedSnapshots.slice(0, 30).map((s, i) => (
              <tr key={s.id} style={{ background: i % 2 === 0 ? "transparent" : "#f8f9fa" }}>
                <td style={{ ...tdStyle, fontSize: 10, padding: "3px 6px" }}>{fmtDate(s.capturedAt)}</td>
                {METRICS.map(m => (
                  <td key={m.key} style={{ ...tdStyle, fontSize: 10, padding: "3px 6px" }}>
                    {m.isPct ? (s[m.key] as number).toFixed(1) : s[m.key]}{m.unit}
                  </td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

const thStyle: React.CSSProperties = { textAlign: "left", padding: "8px 12px", borderBottom: "1px solid var(--color-border)", fontSize: 11, fontWeight: 600, color: "var(--color-muted)", whiteSpace: "nowrap" };
const tdStyle: React.CSSProperties = { padding: "6px 12px", borderBottom: "1px solid var(--color-border)", whiteSpace: "nowrap", color: "var(--color-text)" };
