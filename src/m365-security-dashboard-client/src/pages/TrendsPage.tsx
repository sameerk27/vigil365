import React, { useEffect, useState, useMemo } from "react";
import { TrendingUp, TrendingDown, Minus, Info, Shield, Users, Smartphone, ShieldAlert, Activity, Download, Printer, FileText, ChevronDown, ChevronUp, BarChart3, AlertTriangle, CheckCircle } from "lucide-react";
import { Card, LineChart, StateMessage, LoadingSkeleton, InlineError, CircleGauge, Badge } from "../components/SharedComponents";
import { useAuth, apiFetch, apiBase } from "../services/api";

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

  const filteredSnapshots = useMemo(() => {
    if (!snapshots.length) return [];
    const latestDate = new Date(snapshots[snapshots.length - 1].capturedAt).getTime();
    const cutoff = latestDate - (timeRange * 24 * 60 * 60 * 1000);
    const filtered = snapshots.filter(s => new Date(s.capturedAt).getTime() >= cutoff);
    return filtered.length > 1 ? filtered : snapshots.slice(-2);
  }, [snapshots, timeRange]);

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

    // Data coverage
    result.push({ type: "info", text: `Showing ${filteredSnapshots.length} data points over ${timeRange} days. Data is captured once per collection cycle (default: every 5 minutes).` });

    return result;
  }, [filteredSnapshots, latest, oldest, timeRange]);

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
      <div className="page" style={{ padding: "24px" }}>
        <div style={{ marginBottom: 24 }}><LoadingSkeleton type="card" /></div>
        <div className="kpi-row"><LoadingSkeleton type="kpi" /><LoadingSkeleton type="kpi" /><LoadingSkeleton type="kpi" /><LoadingSkeleton type="kpi" /><LoadingSkeleton type="kpi" /></div>
        <div style={{ marginTop: 24 }}><LoadingSkeleton type="card" /></div>
        <div className="mid-row" style={{ marginTop: 16 }}><LoadingSkeleton type="card" /><LoadingSkeleton type="card" /><LoadingSkeleton type="card" /></div>
      </div>
    );
  }

  if (error) return <div className="page"><InlineError title="Failed to load Trends" message={error} /></div>;

  if (snapshots.length === 0) {
    return (
      <div className="page" style={{ padding: "24px" }}>
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
          <span style={{ fontSize: 12, fontWeight: 600, color: "var(--color-muted)", letterSpacing: "0.02em", textTransform: "uppercase" }}>{metric.label}</span>
        </div>
        <div style={{ fontSize: 28, fontWeight: 700, color: "var(--color-text)", lineHeight: 1, marginBottom: 6 }}>
          {metric.isPct ? cur.toFixed(1) : cur}{metric.unit && <span style={{ fontSize: 16, fontWeight: 500, color: "var(--color-muted)" }}>{metric.unit}</span>}
        </div>
        <div style={{ display: "flex", alignItems: "center", gap: 6, fontSize: 12 }}>
          <ArrowIcon size={14} color={dirColor} />
          <span style={{ color: dirColor, fontWeight: 600 }}>
            {diff === 0 ? "No change" : `${diff > 0 ? "+" : ""}${metric.isPct ? diff.toFixed(1) : diff}`}
          </span>
          <span style={{ color: "var(--color-muted)" }}>vs previous</span>
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
    <div className="page trends-page" style={{ padding: "24px" }}>

      {/* ─── Report Header (visible always, styled for print) ─── */}
      <div className="trends-report-header print-only" style={{ display: "none" }}>
        <h1 style={{ fontSize: 22, fontWeight: 700, margin: 0 }}>Vigil365 — Security Trends & History Report</h1>
        <p style={{ fontSize: 12, color: "#6b7280", margin: "4px 0 0" }}>
          Generated {new Date().toLocaleDateString("en-US", { weekday: "long", year: "numeric", month: "long", day: "numeric" })} · Period: Last {timeRange} days · {filteredSnapshots.length} data points
        </p>
      </div>

      {/* ─── Page Header ─── */}
      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "flex-start", marginBottom: 20, flexWrap: "wrap", gap: 12 }}>
        <div>
          <h2 style={{ fontSize: 22, fontWeight: 700, color: "var(--color-text)", margin: 0, display: "flex", alignItems: "center", gap: 10 }}>
            <BarChart3 size={22} color="var(--color-primary)" /> Trends & History
          </h2>
          <p style={{ fontSize: 13, color: "var(--color-muted)", margin: "4px 0 0" }}>
            Security posture tracking across {filteredSnapshots.length} snapshots · Last capture: {latest?.capturedAt ? new Date(latest.capturedAt).toLocaleString() : "—"}
          </p>
        </div>
        <div className="trends-controls no-print" style={{ display: "flex", gap: 8, alignItems: "center", flexWrap: "wrap" }}>
          <div style={{ display: "flex", gap: 3, background: "var(--color-bg-alt)", padding: 3, borderRadius: 8, border: "1px solid var(--color-border)" }}>
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
          <div style={{ width: 1, height: 24, background: "var(--color-border)" }} />
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
        <div style={{ padding: "8px 16px 0" }}>
          <div style={{ display: "flex", alignItems: "center", gap: 16, marginBottom: 8, flexWrap: "wrap" }}>
            <div style={{ display: "flex", alignItems: "baseline", gap: 6 }}>
              <span style={{ fontSize: 32, fontWeight: 700, color: "var(--color-text)" }}>{latest?.secureScorePct.toFixed(1)}</span>
              <span style={{ fontSize: 16, color: "var(--color-muted)", fontWeight: 500 }}>%</span>
            </div>
            <CircleGauge pct={latest?.secureScorePct ?? 0} size={52} />
            <div style={{ fontSize: 12, color: "var(--color-muted)", maxWidth: 400 }}>
              {METRICS[0].description}
            </div>
          </div>
        </div>
        <div style={{ height: 220, padding: "0 16px 16px" }}>
          <LineChart data={toChartData("secureScorePct")} color="#3b82f6" />
        </div>
      </Card>

      {/* ─── Risk Metrics Grid ─── */}
      <h3 style={{ fontSize: 15, fontWeight: 600, color: "var(--color-text)", margin: "24px 0 12px", display: "flex", alignItems: "center", gap: 8 }}>
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
              <div style={{ display: "flex", alignItems: "baseline", gap: 4 }}>
                <span style={{ fontSize: 22, fontWeight: 700, color: "var(--color-text)" }}>
                  {m.isPct ? (latest?.[m.key] as number).toFixed(1) : latest?.[m.key]}
                </span>
                {m.unit && <span style={{ fontSize: 12, color: "var(--color-muted)" }}>{m.unit}</span>}
              </div>
            </div>
            <p style={{ margin: "4px 16px 0", fontSize: 11, color: "var(--color-muted)", lineHeight: 1.4 }}>{m.description}</p>
            <div style={{ height: 160, padding: "4px 12px 12px" }}>
              <LineChart data={toChartData(m.key)} color={m.color} />
            </div>
          </div>
        ))}
      </div>

      {/* ─── Automated Insights ─── */}
      <h3 style={{ fontSize: 15, fontWeight: 600, color: "var(--color-text)", margin: "24px 0 12px", display: "flex", alignItems: "center", gap: 8 }}>
        <Info size={16} color="var(--color-primary)" /> Automated Insights
      </h3>
      <div style={{ display: "flex", flexDirection: "column", gap: 6, marginBottom: 24 }}>
        {insights.map((insight, i) => <InsightRow key={i} insight={insight} />)}
      </div>

      {/* ─── Historical Data Table ─── */}
      <div className="no-print">
        <button onClick={() => setShowTable(!showTable)}
          style={{ display: "flex", alignItems: "center", gap: 8, padding: "10px 16px", background: "var(--color-bg-alt)", border: "1px solid var(--color-border)", borderRadius: 8, cursor: "pointer", fontSize: 13, fontWeight: 600, color: "var(--color-text)", width: "100%", marginBottom: showTable ? 0 : 24 }}>
          {showTable ? <ChevronUp size={14} /> : <ChevronDown size={14} />}
          Historical Data Table ({filteredSnapshots.length} records)
        </button>
      </div>
      {showTable && (
        <div style={{ overflowX: "auto", marginBottom: 24, border: "1px solid var(--color-border)", borderTop: "none", borderRadius: "0 0 8px 8px" }}>
          <table style={{ width: "100%", borderCollapse: "collapse", fontSize: 12 }}>
            <thead>
              <tr style={{ background: "var(--color-bg-alt)" }}>
                <th style={thStyle}>Date</th>
                {METRICS.map(m => <th key={m.key} style={thStyle}>{m.label}</th>)}
              </tr>
            </thead>
            <tbody>
              {[...filteredSnapshots].reverse().map((s, i) => (
                <tr key={s.id} style={{ background: i % 2 === 0 ? "transparent" : "var(--color-bg-alt)" }}>
                  <td style={tdStyle}>{new Date(s.capturedAt).toLocaleDateString("en-US", { month: "short", day: "numeric", year: "numeric" })}</td>
                  {METRICS.map(m => {
                    const val = s[m.key] as number;
                    const prevRow = i < filteredSnapshots.length - 1 ? [...filteredSnapshots].reverse()[i + 1] : null;
                    const prevVal = prevRow ? prevRow[m.key] as number : null;
                    const diff = prevVal !== null ? val - prevVal : 0;
                    const improving = diff === 0 ? null : m.lowerIsBetter ? diff < 0 : diff > 0;
                    return (
                      <td key={m.key} style={tdStyle}>
                        <span style={{ fontWeight: 600 }}>{m.isPct ? val.toFixed(1) : val}{m.unit}</span>
                        {diff !== 0 && (
                          <span style={{ marginLeft: 6, fontSize: 10, color: improving ? "#10b981" : "#ef4444", fontWeight: 600 }}>
                            {diff > 0 ? "▲" : "▼"}{Math.abs(m.isPct ? diff : diff)}
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
      <div className="print-only" style={{ display: "none" }}>
        <h3 style={{ fontSize: 14, fontWeight: 600, marginTop: 24 }}>Historical Data</h3>
        <table style={{ width: "100%", borderCollapse: "collapse", fontSize: 10 }}>
          <thead>
            <tr>
              <th style={{ ...thStyle, fontSize: 10, padding: "4px 6px" }}>Date</th>
              {METRICS.map(m => <th key={m.key} style={{ ...thStyle, fontSize: 10, padding: "4px 6px" }}>{m.label}</th>)}
            </tr>
          </thead>
          <tbody>
            {[...filteredSnapshots].reverse().slice(0, 30).map((s, i) => (
              <tr key={s.id} style={{ background: i % 2 === 0 ? "transparent" : "#f8f9fa" }}>
                <td style={{ ...tdStyle, fontSize: 10, padding: "3px 6px" }}>{new Date(s.capturedAt).toLocaleDateString("en-US", { month: "short", day: "numeric" })}</td>
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
