import React, { useState, useMemo, useEffect, useCallback } from "react";
import { X, Bell, AlertCircle, Clock, ShieldAlert, Activity, CheckCircle, Search, ExternalLink, ArrowRight, ShieldCheck, AlertTriangle, PlusCircle, ChevronUp, ChevronDown, ChevronsUpDown } from "lucide-react";
import { AlertPolicy, TriggeredAlert, NotificationSettings, NotificationLogEntry, Tone, AlertCoverageScorecard, AlertBaselineRule } from "../services/types";
import { acApi, recApi, wbApi, useAuth, crossNavigate, consumeNavTab } from "../services/api";
import { showToast } from "../services/toast";
import { confirmAction } from "../services/confirm";
import { DetailField, KpiTile, Card, Badge, EmptyState, MiniBarChart, ExportDropdown, ProgressBar, CopyButton, LoadingSkeleton, TriageSection, rowActivation, SeverityFilter} from "../components/SharedComponents";
import { CollectionHealthCard } from "../components/CollectionHealthCard";
import { CollectionStatusBanner } from "../components/CollectionStatusBanner";
import { CollectionRunHistory } from "../components/CollectionRunHistory";
import { AlertMetricsTab } from "../components/AlertMetricsTab";
import { SuppressionRulesTab } from "../components/SuppressionRulesTab";
import { PolicyDryRun } from "../components/PolicyDryRun";
import { PolicyPackControls } from "../components/PolicyPackControls";
import { FilterPresets } from "../components/FilterPresets";
import { relTime, fmtDate, fmtShort, sevTone } from "../services/utils";

/** Human-readable status labels — raw enums like "auto_resolved" never reach the UI. */
const STATUS_LABELS: Record<string, string> = {
  new: "New", acknowledged: "Acknowledged", snoozed: "Snoozed",
  auto_resolved: "Auto-resolved", resolved: "Resolved",
};
export const fmtStatus = (s: string) => STATUS_LABELS[s] ?? s.replace(/_/g, " ");

type AcTab = "dashboard" | "alerts" | "policies" | "templates" | "coverage" | "notifications" | "metrics" | "suppression" | "runs";

export const POLICY_TEMPLATES_CATALOG = [
  { name: "Critical Alerts Monitor",   desc: "Triggers when any critical security alert is detected",              metric: "criticalAlertCount", threshold: 1, severity: "critical" as const, category: "identity"   as const },
  { name: "MFA Coverage Drop",         desc: "Triggers when more than 5 users are missing MFA",                   metric: "mfaMissingCount",    threshold: 5, severity: "high"     as const, category: "identity"   as const },
  { name: "Risky User Detected",       desc: "Triggers immediately when any user is marked as risky",             metric: "riskyUsersCount",    threshold: 1, severity: "high"     as const, category: "identity"   as const },
  { name: "Device Compliance Breach",  desc: "Triggers when non-compliant devices are found",                     metric: "nonCompliantCount",  threshold: 1, severity: "medium"   as const, category: "devices"    as const },
  { name: "Email Threat Surge",        desc: "Triggers when high-priority email alerts exceed threshold",          metric: "highAlertCount",     threshold: 3, severity: "high"     as const, category: "email"      as const },
  { name: "Stale Device",             desc: "Triggers when devices haven't checked in for 30+ days",             metric: "staleDeviceCount",   threshold: 1, severity: "medium"   as const, category: "devices"    as const },
  { name: "Sign-in Anomaly",          desc: "Triggers when failed sign-ins spike above threshold",               metric: "alertCount",         threshold: 10,severity: "high"     as const, category: "identity"   as const },
  { name: "Insider Risk Alert",       desc: "Triggers on any insider risk management alert",                     metric: "alertCount",         threshold: 1, severity: "high"     as const, category: "compliance" as const },
  { name: "Admin Role Change",        desc: "Tracks privileged role assignments via audit log",                  metric: "alertCount",         threshold: 1, severity: "medium"   as const, category: "identity"   as const },
];

// Delegates to the shared app-wide severity mapping (services/utils.sevTone).
function sevToneAC(s: string): Tone {
  return sevTone(s);
}

function statusTone(s: string): Tone {
  return s === "new" ? "error"
    : s === "acknowledged" ? "warning"
    : s === "snoozed" ? "neutral"
    : s === "auto_resolved" ? "info"
    : "good"; // resolved
}

function AlertEvidenceTimeline({ alert, noteVersion }: { alert: TriggeredAlert; noteVersion: number }) {
  const [notes, setNotes] = useState<import("../services/types").AlertNote[] | null>(null);

  useEffect(() => {
    let cancelled = false;
    wbApi.listNotes("policy", alert.id).then(items => { if (!cancelled) setNotes(items); });
    return () => { cancelled = true; };
  }, [alert.id, noteVersion]);

  const events = useMemo(() => {
    const items: { at: string; label: string; detail: string; kind: "trigger" | "action" | "note" }[] = [
      { at: alert.triggeredAt, label: "Alert triggered", detail: `${alert.metricValue} observed; threshold ${alert.threshold}.`, kind: "trigger" },
    ];
    if (alert.acknowledgedAt) items.push({ at: alert.acknowledgedAt, label: "Alert acknowledged", detail: alert.acknowledgedBy ? `By ${alert.acknowledgedBy}.` : "", kind: "action" });
    if (alert.lastEvaluatedAt) items.push({ at: alert.lastEvaluatedAt, label: "Last evaluated", detail: `Current state: ${fmtStatus(alert.status)}.`, kind: "action" });
    if (alert.snoozedUntil) items.push({ at: alert.snoozedUntil, label: "Snooze active until", detail: alert.snoozedBy ? `Set by ${alert.snoozedBy}.` : "", kind: "action" });
    for (const note of notes ?? []) items.push({ at: note.createdAt, label: `Note by ${note.author}`, detail: note.text, kind: "note" });
    return items.sort((a, b) => new Date(b.at).getTime() - new Date(a.at).getTime());
  }, [alert, notes]);

  return (
    <section className="alert-evidence-timeline" aria-label="Alert evidence timeline">
      <div className="dm-section-hdr">Evidence timeline</div>
      {events.map((event, index) => (
        <div className="evidence-event" key={`${event.kind}-${event.at}-${index}`}>
          <span className={`evidence-dot evidence-${event.kind}`} aria-hidden="true"/>
          <div>
            <div className="evidence-event-meta"><strong>{event.label}</strong><span title={fmtDate(event.at)}>{relTime(event.at)}</span></div>
            {event.detail && <div className="evidence-event-detail">{event.detail}</div>}
          </div>
        </div>
      ))}
    </section>
  );
}

/** Sortable table header — click to sort, click again to flip direction. */
function SortTh<T extends string>({ label, col, sortBy, sortDir, onSort }: {
  label: string; col: T; sortBy: string; sortDir: "asc" | "desc"; onSort: (c: T) => void;
}) {
  const active = sortBy === col;
  return (
    <th scope="col" aria-sort={active ? (sortDir === "asc" ? "ascending" : "descending") : undefined}>
      <button type="button" className="th-sort" onClick={() => onSort(col)}>
        {label}
        {active
          ? (sortDir === "asc" ? <ChevronUp size={12}/> : <ChevronDown size={12}/>)
          : <ChevronsUpDown size={12} className="sort-icon-muted"/>}
      </button>
    </th>
  );
}

function PolicyModal({ policy, onSave, onClose }: {
  policy: Partial<AlertPolicy> | null;
  onSave: (p: AlertPolicy) => void;
  onClose: () => void;
}) {
  const [form, setForm] = useState<Partial<AlertPolicy>>(policy ?? { enabled: true, severity: "medium", category: "identity", threshold: 1, triggerCount: 0, notifyEmail: "" });
  const set = (k: keyof AlertPolicy, v: unknown) => setForm(f => ({ ...f, [k]: v }));

  const metricOptions: Record<string, { label: string; value: string }[]> = {
    identity:   [{ label: "Critical alert count", value: "criticalAlertCount" }, { label: "Risky users", value: "riskyUsersCount" }, { label: "MFA missing count", value: "mfaMissingCount" }, { label: "High alert count", value: "highAlertCount" }],
    devices:    [{ label: "Non-compliant count", value: "nonCompliantCount" }, { label: "Stale device count", value: "staleDeviceCount" }],
    email:      [{ label: "High alert count", value: "highAlertCount" }, { label: "Critical alert count", value: "criticalAlertCount" }],
    compliance: [{ label: "Alert count", value: "alertCount" }],
    licenses:   [{ label: "Expired license count", value: "expiredLicenseCount" }],
  };

  const kind = form.kind ?? "metric";

  // Shared by save and dry-run so the backtest measures exactly the policy that
  // would be saved — a preview of a different shape than the real thing is worse
  // than no preview.
  const buildDraft = (): AlertPolicy => {
    const threshold = Number(form.threshold ?? 1);
    const windowMinutes = Number(form.windowMinutes ?? 60);
    const baselineMultiplier = Number(form.baselineMultiplier ?? 3);
    const baselineDays = Number(form.baselineDays ?? 30);
    return {
      id: form.id ?? crypto.randomUUID(),
      name: form.name?.trim() ?? "",
      enabled: form.enabled ?? true,
      category: form.category ?? "identity",
      kind,
      condition: kind === "activity"
        ? `Activity "${form.activityPattern?.trim() ?? ""}" ≥ ${threshold} in ${windowMinutes}m`
        : kind === "anomaly"
        ? `${form.metric} ≥ ${threshold} and ≥ ${baselineMultiplier}× ${baselineDays}d baseline`
        : (form.condition ?? `${form.metric} >= ${threshold}`),
      metric: kind === "activity" ? "" : (form.metric ?? "criticalAlertCount"),
      activityPattern: kind === "activity" ? (form.activityPattern?.trim() ?? "") : null,
      windowMinutes,
      baselineMultiplier,
      baselineDays,
      threshold,
      severity: form.severity ?? "medium",
      notifyEmail: form.notifyEmail ?? "",
      createdAt: form.createdAt ?? new Date().toISOString(),
      lastTriggered: form.lastTriggered,
      triggerCount: form.triggerCount ?? 0,
    };
  };

  const handleSave = () => {
    if (!form.name?.trim()) { showToast("Policy name is required", "error"); return; }
    if (kind === "activity" && !form.activityPattern?.trim()) { showToast("Activity pattern is required for activity policies", "error"); return; }
    onSave(buildDraft());
  };

  useEffect(() => {
    const handler = (e: KeyboardEvent) => { if (e.key === "Escape") onClose(); };
    window.addEventListener("keydown", handler);
    return () => window.removeEventListener("keydown", handler);
  }, [onClose]);

  return (
    <div className="detail-modal-backdrop" onClick={onClose}>
      <div className="detail-modal policy-modal" onClick={e => e.stopPropagation()}>
        <div className="detail-modal-hdr">
          <div className="dm-title">{form.id ? "Edit Policy" : "New Policy"}</div>
          <button className="modal-close" onClick={onClose}><X size={16}/></button>
        </div>
        <div className="detail-modal-body">
          <div className="policy-field">
            <label className="policy-label">Policy Name</label>
            <input className="policy-input" value={form.name ?? ""} onChange={e => set("name", e.target.value)} placeholder="e.g. Critical Alert Monitor"/>
          </div>
          <div className="policy-field">
            <label className="policy-label">Category</label>
            <select className="policy-input" value={form.category ?? "identity"} onChange={e => set("category", e.target.value)}>
              <option value="identity">Identity</option>
              <option value="devices">Devices</option>
              <option value="email">Email</option>
              <option value="compliance">Compliance</option>
              <option value="licenses">Licenses</option>
            </select>
          </div>
          <div className="policy-field">
            <label className="policy-label">Policy Type</label>
            <select className="policy-input" value={kind} onChange={e => set("kind", e.target.value)}>
              <option value="metric">Metric threshold — fire when a count crosses a limit</option>
              <option value="activity">Tenant activity — fire when something happens (audit event)</option>
              <option value="anomaly">Anomaly — fire when a trend spikes above baseline</option>
            </select>
          </div>
          {kind === "activity" ? (
            <>
              <div className="policy-field">
                <label className="policy-label">Activity to Match (* = wildcard, e.g. "*conditional access policy")</label>
                <input className="policy-input" value={form.activityPattern ?? ""} onChange={e => set("activityPattern", e.target.value)}
                  placeholder='e.g. "Add member to role" or "Consent to application"'/>
              </div>
              <div className="policy-field">
                <label className="policy-label">Time Window (minutes)</label>
                <input type="number" className="policy-input" min={1} value={form.windowMinutes ?? 60} onChange={e => set("windowMinutes", Number(e.target.value))}/>
              </div>
              <div className="policy-field">
                <label className="policy-label">Threshold (fire when &ge; this many matching events in the window)</label>
                <input type="number" className="policy-input" min={1} value={form.threshold ?? 1} onChange={e => set("threshold", Number(e.target.value))}/>
              </div>
            </>
          ) : kind === "anomaly" ? (
            <>
              <div className="policy-field">
                <label className="policy-label">Trend Metric to Watch</label>
                <select className="policy-input" value={form.metric ?? ""} onChange={e => set("metric", e.target.value)}>
                  <option value="">Select metric…</option>
                  {(metricOptions[form.category ?? "identity"] ?? []).map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
                  <option value="complianceIssuesCount">Compliance issues</option>
                  <option value="secureScorePct">Secure score percentage</option>
                </select>
              </div>
              <div className="policy-field">
                <label className="policy-label">Absolute Floor (latest value must be ≥ this)</label>
                <input type="number" className="policy-input" min={1} value={form.threshold ?? 1} onChange={e => set("threshold", Number(e.target.value))}/>
              </div>
              <div className="policy-field">
                <label className="policy-label">Baseline Multiplier</label>
                <input type="number" className="policy-input" min={1} step={0.5} value={form.baselineMultiplier ?? 3} onChange={e => set("baselineMultiplier", Number(e.target.value))}/>
              </div>
              <div className="policy-field">
                <label className="policy-label">Baseline Lookback (days, excluding last 24h)</label>
                <input type="number" className="policy-input" min={1} value={form.baselineDays ?? 30} onChange={e => set("baselineDays", Number(e.target.value))}/>
              </div>
            </>
          ) : (
            <>
              <div className="policy-field">
                <label className="policy-label">Metric to Watch</label>
                <select className="policy-input" value={form.metric ?? ""} onChange={e => set("metric", e.target.value)}>
                  <option value="">Select metric…</option>
                  {(metricOptions[form.category ?? "identity"] ?? []).map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
                </select>
              </div>
              <div className="policy-field">
                <label className="policy-label">Threshold (trigger when metric &ge; this value)</label>
                <input type="number" className="policy-input" min={1} value={form.threshold ?? 1} onChange={e => set("threshold", Number(e.target.value))}/>
              </div>
            </>
          )}
          <div className="policy-field">
            <label className="policy-label">Severity</label>
            <select className="policy-input" value={form.severity ?? "medium"} onChange={e => set("severity", e.target.value)}>
              <option value="critical">Critical</option>
              <option value="high">High</option>
              <option value="medium">Medium</option>
              <option value="low">Low</option>
            </select>
          </div>
          <div className="policy-field">
            <label className="policy-label">Notify Email (overrides global SMTP recipient for this policy)</label>
            <input className="policy-input" type="email" value={form.notifyEmail ?? ""} onChange={e => set("notifyEmail", e.target.value)} placeholder="admin@contoso.com"/>
          </div>
        </div>
        <PolicyDryRun buildDraft={buildDraft}/>
        <div className="detail-modal-footer">
          <button className="dm-close-btn" onClick={onClose}>Cancel</button>
          <button className="btn-run" data-inline-style="inline-0e2d4430ae" onClick={handleSave}>Save Policy</button>
        </div>
      </div>
    </div>
  );
}

function NotificationSettingsTab() {
  const [cfg, setCfg] = useState<NotificationSettings | null>(null);
  const [log, setLog] = useState<NotificationLogEntry[]>([]);
  const [health, setHealth] = useState<import("../services/types").NotificationHealth | null>(null);
  const [saving, setSaving] = useState(false);
  const [testing, setTesting] = useState(false);

  const reload = useCallback(async () => {
    const [s, l, h] = await Promise.all([acApi.getSettings(), acApi.getLog(), acApi.getHealth()]);
    setCfg(s ?? { teamsEnabled:false, emailEnabled:false, smtpPort:587, smtpUseSsl:true, webhookEnabled:false, minSeverity:"low" });
    setLog(l);
    setHealth(h);
  }, []);
  useEffect(() => { reload(); }, [reload]);

  if (!cfg) return <Card title="Notification Channels"><EmptyState icon={<Bell size={28}/>} message="Loading settings…"/></Card>;

  const set = <K extends keyof NotificationSettings>(k: K, v: NotificationSettings[K]) => setCfg(c => c ? { ...c, [k]: v } : c);

  const save = async () => {
    setSaving(true);
    const ok = await acApi.saveSettings(cfg);
    setSaving(false);
    showToast(ok ? "Notification settings saved" : "Failed to save", ok ? "success" : "error");
    if (ok) reload();
  };

  const test = async () => {
    setTesting(true);
    const res = await acApi.testNotifications();
    setTesting(false);
    if (res.results?.length) {
      const summary = res.results.map(r => `${r.channel}: ${r.success ? "✓" : "✗"}`).join("  ");
      showToast(`Test sent — ${summary}`, res.ok ? "success" : "error");
    } else {
      showToast("No channels enabled to test", "error");
    }
    reload();
  };

  const digestChip = (key: "teamsDigest"|"emailDigest"|"webhookDigest") => (
    <label className="toggle-label" data-inline-style="inline-94b6caffd5" title="Batch this channel's alerts into a single daily rollup instead of sending each one instantly.">
      <input type="checkbox" checked={!!cfg[key]} onChange={e=>set(key, e.target.checked)}/> Daily digest
    </label>
  );

  return (
    <>
      {health?.anyFailing && (
        <div className="err-banner" role="alert" data-inline-style="inline-c804002a41">
          <AlertTriangle size={15} data-inline-style="inline-d0bf3b1007"/>
          Notification delivery is failing on: {health.channels.filter(c=>c.consecutiveFailures>=health.threshold).map(c=>`${c.channel} (${c.consecutiveFailures}×)`).join(", ")}. Check the endpoint URL/credentials below.
        </div>
      )}
      <div className="two-col">
        <Card title="Microsoft Teams / Slack" badge={<label className="toggle-label"><input type="checkbox" checked={cfg.teamsEnabled} onChange={e=>set("teamsEnabled", e.target.checked)}/> Enabled</label>}>
          <div className="policy-field">
            <span className="policy-label">Incoming Webhook URL</span>
            <input className="policy-input" placeholder="https://outlook.office.com/webhook/…" value={cfg.teamsWebhookUrl ?? ""} onChange={e=>set("teamsWebhookUrl", e.target.value)}/>
          </div>
          <div data-inline-style="inline-5313025c2e">{digestChip("teamsDigest")}</div>
          <p className="hdr-sub">Paste a Teams channel "Incoming Webhook" connector URL (or a Slack incoming webhook). A formatted alert card is posted on each trigger.</p>
        </Card>

        <Card title="Generic Webhook / SIEM" badge={<label className="toggle-label"><input type="checkbox" checked={cfg.webhookEnabled} onChange={e=>set("webhookEnabled", e.target.checked)}/> Enabled</label>}>
          <div className="policy-field">
            <span className="policy-label">Endpoint URL</span>
            <input className="policy-input" placeholder="https://…  (Sentinel, Splunk HEC, Power Automate)" value={cfg.webhookUrl ?? ""} onChange={e=>set("webhookUrl", e.target.value)}/>
          </div>
          <div data-inline-style="inline-5313025c2e">{digestChip("webhookDigest")}</div>
          <p className="hdr-sub">Each alert is POSTed as JSON. Use for SIEM ingestion or custom automation.</p>
        </Card>
      </div>

      <Card title="Email (SMTP)" badge={<div data-inline-style="inline-2b63254f19">{digestChip("emailDigest")}<label className="toggle-label"><input type="checkbox" checked={cfg.emailEnabled} onChange={e=>set("emailEnabled", e.target.checked)}/> Enabled</label></div>}>
        <div className="settings-grid">
          <div className="policy-field"><span className="policy-label">SMTP Host</span><input className="policy-input" placeholder="smtp.office365.com" value={cfg.smtpHost ?? ""} onChange={e=>set("smtpHost", e.target.value)}/></div>
          <div className="policy-field"><span className="policy-label">Port</span><input className="policy-input" type="number" value={cfg.smtpPort} onChange={e=>set("smtpPort", Number(e.target.value))}/></div>
          <div className="policy-field"><span className="policy-label">Use SSL/TLS</span><label className="toggle-label" data-inline-style="inline-5313025c2e"><input type="checkbox" checked={cfg.smtpUseSsl} onChange={e=>set("smtpUseSsl", e.target.checked)}/> Enabled</label></div>
          <div className="policy-field"><span className="policy-label">Username</span><input className="policy-input" value={cfg.smtpUsername ?? ""} onChange={e=>set("smtpUsername", e.target.value)}/></div>
          <div className="policy-field"><span className="policy-label">Password</span><input className="policy-input" type="password" placeholder={cfg.hasSmtpPassword ? "•••••• (unchanged)" : ""} value={cfg.smtpPassword ?? ""} onChange={e=>set("smtpPassword", e.target.value)}/></div>
          <div className="policy-field"><span className="policy-label">From Address</span><input className="policy-input" placeholder="vigil365@yourdomain.com" value={cfg.fromAddress ?? ""} onChange={e=>set("fromAddress", e.target.value)}/></div>
          <div className="policy-field"><span className="policy-label">Default Recipient</span><input className="policy-input" placeholder="secops@yourdomain.com" value={cfg.defaultRecipient ?? ""} onChange={e=>set("defaultRecipient", e.target.value)}/></div>
        </div>
      </Card>

      <Card title="Delivery Rules" action={<div data-inline-style="inline-a9c77021d6">
        <button className="btn-export" onClick={test} disabled={testing}>{testing ? "Testing…" : "Send test"}</button>
        <button className="btn-run" data-inline-style="inline-0e2d4430ae" onClick={save} disabled={saving}>{saving ? "Saving…" : "Save settings"}</button>
      </div>}>
        <div className="settings-grid">
          <div className="policy-field">
            <span className="policy-label">Minimum severity to notify</span>
            <select className="policy-input" value={cfg.minSeverity} onChange={e=>set("minSeverity", e.target.value)}>
              <option value="low">Low and above</option>
              <option value="medium">Medium and above</option>
              <option value="high">High and above</option>
              <option value="critical">Critical only</option>
            </select>
          </div>
          <div className="policy-field">
            <span className="policy-label">Daily digest send hour (UTC)</span>
            <input className="policy-input" type="number" min={0} max={23} value={cfg.digestHourUtc ?? 8} onChange={e=>set("digestHourUtc", Number(e.target.value))}/>
          </div>
          <div className="policy-field">
            <span className="policy-label">Alert after N consecutive channel failures</span>
            <input className="policy-input" type="number" min={1} value={cfg.failureAlertThreshold ?? 3} onChange={e=>set("failureAlertThreshold", Number(e.target.value))}/>
          </div>
        </div>
        <p className="hdr-sub">Digest channels batch their alerts into one daily message at the send hour. If a channel fails to deliver this many times in a row, Vigil365 raises a high-severity delivery-failure alert on the still-working channels.</p>
      </Card>

      <Card title="Notification History" badge={<Badge label={`${log.length} sent`} tone="neutral"/>}>
        {log.length === 0 ? (
          <EmptyState icon={<Bell size={28}/>} message="No notifications sent yet. They appear here once an alert fires with a channel enabled."/>
        ) : (
          <div className="tbl-wrap">
            <table className="data-tbl">
              <thead><tr><th scope="col">Status</th><th scope="col">Channel</th><th scope="col">Policy</th><th scope="col">Target</th><th scope="col">Sent</th><th scope="col">Detail</th></tr></thead>
              <tbody>
                {log.map(l => (
                  <tr key={l.id}>
                    <td><Badge label={l.success ? "Sent" : "Failed"} tone={l.success ? "good" : "error"}/></td>
                    <td data-inline-style="inline-b7b96646ae">{l.channel}</td>
                    <td className="trunc" title={l.policyName}>{l.policyName}</td>
                    <td className="trunc" title={l.target}>{l.target}</td>
                    <td>{relTime(l.sentAt)}</td>
                    <td className="trunc" title={l.error}>{l.error ?? "—"}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Card>
    </>
  );
}

function CoverageScorecardTab({ onChanged }: { onChanged: () => void | Promise<void> }) {
  const [data, setData] = useState<AlertCoverageScorecard | null>(null);
  const [loading, setLoading] = useState(true);
  const [enablingId, setEnablingId] = useState<string | null>(null);
  const [filter, setFilter] = useState<"all" | "missing" | "active">("all");

  const load = useCallback(async () => {
    setLoading(true);
    try {
      const res = await recApi.getAlertCoverage();
      setData(res);
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { load(); }, [load]);

  const handleEnable = async (rule: AlertBaselineRule) => {
    setEnablingId(rule.id);
    try {
      const updated = await recApi.enableCoverageRule(rule.id);
      if (updated) {
        setData(updated);
        showToast(`Enabled rule: ${rule.title}`);
        onChanged();
      } else {
        showToast("Failed to enable rule via API", "error");
      }
    } finally {
      setEnablingId(null);
    }
  };

  if (loading) return <LoadingSkeleton type="table"/>;
  if (!data) return <EmptyState icon={<AlertTriangle size={28}/>} message="Could not load the alert coverage baseline — the API request failed. Refresh to retry."/>;

  const rules = data.rules.filter(r => filter === "all" ? true : filter === "missing" ? !r.isActive : r.isActive);
  const missingCount = data.totalRules - data.activeRules;

  return (
    <div data-inline-style="inline-31cc15eda3">
      <Card title="Alerting Baseline Scorecard"
        badge={<Badge label={missingCount > 0 ? `${missingCount} blind spot${missingCount > 1 ? "s" : ""}` : "Full coverage"} tone={missingCount > 0 ? "warning" : "good"}/>}
        action={
          <div data-inline-style="inline-3633e433a1">
            {(["all", "missing", "active"] as const).map(f => (
              <button key={f} onClick={() => setFilter(f)}
                className={filter === f ? "btn-apply" : "btn-export"}
                data-inline-style="inline-02dfbae3d8">
                {f === "all" ? `All (${data.totalRules})` : f === "missing" ? `Missing (${missingCount})` : `Active (${data.activeRules})`}
              </button>
            ))}
          </div>
        }>
        <div data-inline-style="inline-c44bd08c16">
          <div data-inline-style="inline-1940209700">{data.coveragePercentage}%</div>
          <div data-inline-style="inline-126244f135">
            <ProgressBar pct={data.coveragePercentage}/>
            <div data-inline-style="inline-cc755f9542">
              <strong>{data.activeRules} of {data.totalRules}</strong> Microsoft best-practice alerting rules are actively monitored.
              {missingCount > 0 ? ` The ${missingCount} unmonitored rule${missingCount > 1 ? "s are" : " is"} listed below.` : " Full baseline coverage achieved."}
            </div>
          </div>
        </div>
      </Card>

      <Card title="Baseline Alerting Rules Catalog">
        <div className="tbl-wrap">
          <table className="data-tbl">
            <thead>
              <tr><th scope="col">Status</th><th scope="col">Rule name</th><th scope="col">Engine</th><th scope="col">Severity</th><th scope="col">Description</th><th scope="col" data-inline-style="inline-37dd0c2a64">Action</th></tr>
            </thead>
            <tbody>
              {rules.map(r => (
                <tr key={r.id}>
                  <td><Badge label={r.isActive ? "Monitored" : "Blind spot"} tone={r.isActive ? "good" : "error"}/></td>
                  <td data-inline-style="inline-3d9df89ef8">{r.title}</td>
                  <td><Badge label={r.ruleType === "Vigil365" ? "Vigil365 Alerts" : "Native M365"} tone={r.ruleType === "Vigil365" ? "info" : "neutral"}/></td>
                  <td><Badge label={r.severity} tone={sevToneAC(r.severity)}/></td>
                  <td data-inline-style="inline-4656125047">{r.description}</td>
                  <td data-inline-style="inline-37dd0c2a64">
                    {r.isActive ? (
                      <span data-inline-style="inline-af7da65b76">Active monitoring</span>
                    ) : r.ruleType === "Vigil365" ? (
                      <button className="btn-apply" data-inline-style="inline-02dfbae3d8"
                        onClick={() => handleEnable(r)} disabled={enablingId === r.id}>
                        {enablingId === r.id ? "Enabling…" : "Enable in Vigil365"}
                      </button>
                    ) : (
                      <a className="btn-export" data-inline-style="inline-0ab1bd7012"
                        href={r.nativePortalDeepLink} target="_blank" rel="noopener noreferrer">
                        Configure in Defender <ExternalLink size={13}/>
                      </a>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </Card>
    </div>
  );
}

export function AlertCenterPage({ policies, triggeredAlerts, onChanged, deepLinkAlertId, onDeepLinkConsumed }: {
  policies: AlertPolicy[];
  triggeredAlerts: TriggeredAlert[];
  onChanged: () => void | Promise<void>;
  deepLinkAlertId?: string | null;
  onDeepLinkConsumed?: () => void;
}) {
  const { canMutate } = useAuth();

  // The product is alert-first: land an analyst in the open, worst-first queue.
  // The dashboard remains available when they need the aggregate view.
  // A cross-navigation may request a specific tab (e.g. "show me the collection
  // runs"); honour it on mount instead of dropping them on the default.
  const [tab, setTab] = useState<AcTab>(() => (consumeNavTab("alertcenter") as AcTab) ?? "alerts");

  // Later cross-navigations arrive while this page is already mounted.
  useEffect(() => {
    const listener = (e: Event) => {
      const target = (e as CustomEvent<{ page: string; tab?: string }>).detail;
      if (target?.page === "alertcenter" && target.tab) {
        consumeNavTab("alertcenter");
        setTab(target.tab as AcTab);
      }
    };
    window.addEventListener("nav-seed-update", listener);
    return () => window.removeEventListener("nav-seed-update", listener);
  }, []);
  const [search, setSearch] = useState("");
  const [sevFilter, setSevFilter] = useState("");
  const [catFilter, setCatFilter] = useState("");
  const [statusFilter, setStatusFilter] = useState("new");
  const [dateFilter, setDateFilter] = useState<string>("");
  const [assignedFilter, setAssignedFilter] = useState("");
  const [ageFilter, setAgeFilter] = useState("");
  const [editPolicy, setEditPolicy] = useState<Partial<AlertPolicy> | null>(null);
  const [showModal, setShowModal] = useState(false);
  const [selectedTriggered, setSelectedTriggered] = useState<TriggeredAlert | null>(null);
  const [noteVersion, setNoteVersion] = useState(0);

  // Notification permalink (#/alertcenter?alert={guid}): open that alert's
  // detail directly once the data is available.
  useEffect(() => {
    if (!deepLinkAlertId || triggeredAlerts.length === 0) return;
    const target = triggeredAlerts.find(a => a.id.toLowerCase() === deepLinkAlertId.toLowerCase());
    if (target) {
      setTab("alerts");
      setSelectedTriggered(target);
    } else {
      // The link came from a Teams card or email and the alert is not in the
      // loaded set — usually resolved since, or aged out. Silently swallowing it
      // left the user staring at the queue wondering if the link was broken.
      setTab("alerts");
      showToast(
        "That alert is no longer in the active queue — it may have been resolved or aged out. Showing all alerts instead.",
        "info");
    }
    onDeepLinkConsumed?.();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [deepLinkAlertId, triggeredAlerts]);

  const refresh = () => { onChanged(); };

  // ── KPI ──────────────────────────────────────────────────────────────────
  const enabledCount = policies.filter(p => p.enabled).length;
  const activeAlertsCount = triggeredAlerts.filter(a => a.status === "new").length;
  const today = new Date().toDateString();
  const triggeredToday = triggeredAlerts.filter(a => new Date(a.triggeredAt).toDateString() === today).length;
  const criticalCount = triggeredAlerts.filter(a => a.severity === "critical" && a.status === "new").length;

  // ── Bar chart: last 7 days ────────────────────────────────────────────────
  const last7 = useMemo(() => {
    const days: { date: string; count: number }[] = [];
    for (let i = 6; i >= 0; i--) {
      const d = new Date(); d.setDate(d.getDate() - i);
      const label = d.toLocaleDateString("en-US", { month: "short", day: "numeric" });
      const ds = d.toDateString();
      days.push({ date: label, count: triggeredAlerts.filter(a => new Date(a.triggeredAt).toDateString() === ds).length });
    }
    return days;
  }, [triggeredAlerts]);

  const barMax = Math.max(...last7.map(d => d.count), 1);

  // ── Donut: by category ────────────────────────────────────────────────────
  const catCounts = useMemo(() => {
    const cats: Record<string, number> = {};
    triggeredAlerts.forEach(a => { cats[a.category] = (cats[a.category] ?? 0) + 1; });
    return Object.entries(cats).sort((a, b) => b[1] - a[1]);
  }, [triggeredAlerts]);

  const catColors: Record<string, string> = { identity: "#3b82f6", devices: "#8b5cf6", email: "#f59e0b", compliance: "#10b981", licenses: "#ec4899" };
  const assignees = useMemo(() => [...new Set(triggeredAlerts.map(a => a.assignedTo).filter((email): email is string => !!email))].sort(), [triggeredAlerts]);

  // ── Active alerts: filter → sort → paginate ──────────────────────────────
  const [sortBy, setSortBy] = useState<"severity" | "policyName" | "triggeredAt" | "status" | "assignedTo">("severity");
  const [sortDir, setSortDir] = useState<"asc" | "desc">("asc");
  const [pageNum, setPageNum] = useState(1);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const PAGE_SIZE = 25;

  const toggleSort = (col: typeof sortBy) => {
    if (sortBy === col) setSortDir(d => d === "asc" ? "desc" : "asc");
    else { setSortBy(col); setSortDir(col === "triggeredAt" ? "desc" : "asc"); }
    setPageNum(1);
  };

  const sortLabel: Record<typeof sortBy, string> = {
    severity: "severity",
    policyName: "policy name",
    triggeredAt: "triggered time",
    status: "status",
    assignedTo: "assignee",
  };
  const sortAnnouncement = `Active alerts sorted by ${sortLabel[sortBy]}, ${sortDir === "asc" ? "ascending" : "descending"}`;

  const filteredTA = useMemo(() => {
    let items = triggeredAlerts;
    if (search) { const q = search.toLowerCase(); items = items.filter(a => a.policyName.toLowerCase().includes(q) || a.condition.toLowerCase().includes(q)); }
    if (sevFilter) items = items.filter(a => a.severity === sevFilter);
    if (catFilter) items = items.filter(a => a.category === catFilter);
    if (statusFilter) items = items.filter(a => a.status === statusFilter);
    if (dateFilter) items = items.filter(a => a.triggeredAt.startsWith(dateFilter));
    if (assignedFilter) items = items.filter(a => a.assignedTo === assignedFilter);
    if (ageFilter) {
      const now = Date.now();
      items = items.filter(a => {
        const ageHours = (now - new Date(a.triggeredAt).getTime()) / 3_600_000;
        return ageFilter === "under4" ? ageHours < 4
          : ageFilter === "4to24" ? ageHours >= 4 && ageHours < 24
          : ageFilter === "over24" ? ageHours >= 24
          : ageFilter === "overdue" ? a.status === "new" && ageHours >= 24
          : true;
      });
    }
    const sevRank: Record<string, number> = { critical: 0, high: 1, medium: 2, low: 3 };
    const statusRank: Record<string, number> = { new: 0, acknowledged: 1, snoozed: 2, auto_resolved: 3, resolved: 4 };
    const dir = sortDir === "asc" ? 1 : -1;
    return [...items].sort((a, b) => {
      switch (sortBy) {
        case "severity":   return ((sevRank[a.severity] ?? 5) - (sevRank[b.severity] ?? 5)) * dir;
        case "policyName": return a.policyName.localeCompare(b.policyName) * dir;
        case "status":     return ((statusRank[a.status] ?? 5) - (statusRank[b.status] ?? 5)) * dir;
        case "assignedTo": return (a.assignedTo ?? "￿").localeCompare(b.assignedTo ?? "￿") * dir;
        default:           return (new Date(a.triggeredAt).getTime() - new Date(b.triggeredAt).getTime()) * dir;
      }
    });
  }, [triggeredAlerts, search, sevFilter, catFilter, statusFilter, dateFilter, assignedFilter, ageFilter, sortBy, sortDir]);

  const pageCount = Math.max(1, Math.ceil(filteredTA.length / PAGE_SIZE));
  const safePage = Math.min(pageNum, pageCount);
  const pagedTA = useMemo(() => filteredTA.slice((safePage - 1) * PAGE_SIZE, safePage * PAGE_SIZE), [filteredTA, safePage]);

  /** SLA age: how long an alert has waited. Overdue = still "new" after 24h. */
  const slaAge = (a: TriggeredAlert) => {
    const ms = Date.now() - new Date(a.triggeredAt).getTime();
    const h = Math.floor(ms / 3600000);
    const label = h < 1 ? `${Math.max(1, Math.floor(ms / 60000))}m` : h < 48 ? `${h}h` : `${Math.floor(h / 24)}d`;
    return { label, overdue: a.status === "new" && h >= 24 };
  };

  const toggleSelect = (id: string) => setSelected(prev => {
    const next = new Set(prev);
    if (next.has(id)) next.delete(id); else next.add(id);
    return next;
  });
  const pageAllSelected = pagedTA.length > 0 && pagedTA.every(a => selected.has(a.id));
  const togglePageAll = () => setSelected(prev => {
    const next = new Set(prev);
    if (pageAllSelected) pagedTA.forEach(a => next.delete(a.id));
    else pagedTA.forEach(a => next.add(a.id));
    return next;
  });

  const [bulkBusy, setBulkBusy] = useState(false);
  const bulkAction = async (action: "acknowledge" | "resolve") => {
    setBulkBusy(true);
    try {
      const targets = filteredTA.filter(a => selected.has(a.id) && a.status !== "resolved" && a.status !== "auto_resolved");
      let ok = 0;
      // Keyed by id, not policyName — several alerts can share a policy name.
      const failedIds = new Set<string>();
      const failed: string[] = [];
      for (const a of targets) {
        if (action === "acknowledge" ? await acApi.acknowledge(a.id) : await acApi.resolve(a.id)) ok++;
        else { failedIds.add(a.id); failed.push(a.policyName); }
      }

      // Counting only successes meant "Acknowledged 7 alerts" after 3 silently
      // failed — the analyst believes the queue is clear when it is not.
      const verb = action === "acknowledge" ? "Acknowledged" : "Resolved";
      if (failed.length === 0) {
        showToast(`${verb} ${ok} alert${ok !== 1 ? "s" : ""}`);
      } else {
        const names = failed.slice(0, 3).join(", ");
        const more = failed.length > 3 ? ` and ${failed.length - 3} more` : "";
        showToast(
          `${verb} ${ok} of ${targets.length}. Failed: ${names}${more}. Those alerts are still open.`,
          "error");
      }

      // Keep failed alerts selected so the analyst can retry without re-finding
      // them; clear only what actually succeeded.
      setSelected(failedIds);
      await onChanged();
    } finally { setBulkBusy(false); }
  };

  const undoTo = async (id: string) => {
    if (await acApi.reopen(id)) { showToast("Alert reopened"); await onChanged(); }
    else showToast("Could not reopen alert", "error");
  };

  const acknowledge = async (id: string) => {
    if (await acApi.acknowledge(id)) {
      showToast("Alert acknowledged", "success", { label: "Undo", onAction: () => undoTo(id) });
      await onChanged();
    }
  };

  const resolve = async (id: string) => {
    if (await acApi.resolve(id)) {
      showToast("Alert resolved", "success", { label: "Undo", onAction: () => undoTo(id) });
      await onChanged();
    }
  };

  const snooze = async (id: string, durationHours: 4 | 24 | 168) => {
    if (await acApi.snooze(id, durationHours)) { showToast(`Snoozed for ${durationHours}h`); await onChanged(); }
  };

  const unsnooze = async (id: string) => {
    if (await acApi.unsnooze(id)) { showToast("Snooze cleared"); await onChanged(); }
  };

  const handleSavePolicy = async (p: AlertPolicy) => {
    const exists = policies.some(x => x.id === p.id);
    const ok = exists ? await acApi.updatePolicy(p) : !!(await acApi.createPolicy(p));
    setShowModal(false);
    if (ok) { showToast("Policy saved"); await onChanged(); }
    else showToast("Failed to save policy", "error");
  };

  const handleDeletePolicy = async (id: string) => {
    const policy = policies.find(p => p.id === id);
    const ok = await confirmAction({
      title: "Delete alert policy?",
      message: policy
        ? `"${policy.name}" will stop evaluating and no new alerts will be raised for it. Alerts it already triggered are kept.`
        : "This policy will stop evaluating. Alerts it already triggered are kept.",
      confirmLabel: "Delete policy",
      danger: true,
    });
    if (!ok) return;
    if (await acApi.deletePolicy(id)) { showToast("Policy deleted"); await onChanged(); }
  };

  const togglePolicy = async (id: string) => {
    const p = policies.find(x => x.id === id);
    if (!p) return;
    if (await acApi.updatePolicy({ ...p, enabled: !p.enabled })) await onChanged();
  };

  const useTemplate = (t: typeof POLICY_TEMPLATES_CATALOG[0]) => {
    setEditPolicy({
      name: t.name,
      category: t.category,
      metric: t.metric,
      threshold: t.threshold,
      severity: t.severity,
      condition: t.desc,
      enabled: true,
      notifyEmail: "",
      triggerCount: 0,
    });
    setShowModal(true);
    setTab("policies");
  };

  return (
    <div className="page">
      {showModal && (
        <PolicyModal
          policy={editPolicy}
          onSave={handleSavePolicy}
          onClose={() => { setShowModal(false); setEditPolicy(null); }}
        />
      )}
      {selectedTriggered && (
        <div className="detail-modal-backdrop" onClick={() => setSelectedTriggered(null)}>
          <div className="detail-modal" onClick={e => e.stopPropagation()}>
            <div className="detail-modal-hdr">
              <div className="dm-title">{selectedTriggered.policyName}</div>
              <button className="modal-close" onClick={() => setSelectedTriggered(null)}><X size={16}/></button>
            </div>
            <div className="detail-modal-body">
              <DetailField label="Policy ID" value={selectedTriggered.policyId} copy/>
              <DetailField label="Severity" value={selectedTriggered.severity.charAt(0).toUpperCase() + selectedTriggered.severity.slice(1)}/>
              <DetailField label="Category" value={selectedTriggered.category.charAt(0).toUpperCase() + selectedTriggered.category.slice(1)}/>
              <DetailField label="Condition" value={selectedTriggered.condition}/>
              <DetailField label="Metric Value" value={String(selectedTriggered.metricValue)}/>
              <DetailField label="Threshold" value={String(selectedTriggered.threshold)}/>
              <DetailField label="Status" value={selectedTriggered.status.replace(/_/g, " ").replace(/^./, c => c.toUpperCase())}/>
              <DetailField label="Triggered" value={`${relTime(selectedTriggered.triggeredAt)} (${fmtDate(selectedTriggered.triggeredAt)})`} title={fmtDate(selectedTriggered.triggeredAt)}/>
              {selectedTriggered.acknowledgedAt && <DetailField label="Acknowledged" value={`${relTime(selectedTriggered.acknowledgedAt)} (${fmtDate(selectedTriggered.acknowledgedAt)})`} title={fmtDate(selectedTriggered.acknowledgedAt)}/>}
              {selectedTriggered.snoozedUntil && <DetailField label="Snoozed until" value={`${relTime(selectedTriggered.snoozedUntil)} (${fmtDate(selectedTriggered.snoozedUntil)})`} title={fmtDate(selectedTriggered.snoozedUntil)}/>}
               {selectedTriggered.lastEvaluatedAt && <DetailField label="Last evaluated" value={`${relTime(selectedTriggered.lastEvaluatedAt)} (${fmtDate(selectedTriggered.lastEvaluatedAt)})`} title={fmtDate(selectedTriggered.lastEvaluatedAt)}/>}
               <AlertEvidenceTimeline alert={selectedTriggered} noteVersion={noteVersion}/>
               {/* Triage is the primary action — it comes before the entity list,
                   never below it (a long entity list buried it off-screen). */}
               <TriageSection kind="policy" targetId={selectedTriggered.id} assignedTo={selectedTriggered.assignedTo} showNotes={false} onNoteAdded={() => setNoteVersion(v => v + 1)}/>
              {selectedTriggered.affectedEntities && (() => {
                try {
                  const parsed = JSON.parse(selectedTriggered.affectedEntities) as {
                    id: number;
                    userPrincipalName?: string;
                    deviceName?: string;
                    title?: string;
                    portalUrl?: string;
                    detectedAt?: string;
                    externalId?: string;
                  }[];
                  // Only rows that actually identify something are worth a table
                  // row; metric-count matches with no entity detail are noise.
                  const meaningful = (parsed ?? []).filter(e => e.userPrincipalName || e.deviceName || (e.title && e.title !== "System"));
                  const MAX_SHOWN = 6;
                  const entities = meaningful.slice(0, MAX_SHOWN);
                  if ((parsed?.length ?? 0) > 0 && meaningful.length === 0) {
                    return (
                      <div data-inline-style="inline-d04f4ababb">
                        {parsed.length} matching record{parsed.length !== 1 ? "s" : ""} — no entity-level detail is available for this metric.
                      </div>
                    );
                  }
                  if (entities.length > 0) {
                    return (
                      <div data-inline-style="inline-ff270db33d">
                        <div data-inline-style="inline-0395267c0d">Affected Entities ({meaningful.length})</div>
                        <div data-inline-style="inline-bd2dc3a1da">
                          <table data-inline-style="inline-f767132568">
                            <thead>
                              <tr data-inline-style="inline-564a3c3e76">
                                <th scope="col" data-inline-style="inline-3f4b724c43">Entity</th>
                                <th scope="col" data-inline-style="inline-3f4b724c43">Details</th>
                                <th scope="col" data-inline-style="inline-3f4b724c43">Detected At</th>
                                <th scope="col" data-inline-style="inline-3184f97af8">Actions</th>
                              </tr>
                            </thead>
                            <tbody>
                              {entities.map((e, idx) => {
                                const name = e.userPrincipalName || e.deviceName || "System";
                                const typeLabel = e.userPrincipalName ? "User" : e.deviceName ? "Device" : "Other";
                                const isCopyable = !!(e.userPrincipalName || e.deviceName);
                                return (
                                  <tr key={e.id || idx} style={{ borderBottom: idx < entities.length - 1 ? "1px solid var(--color-border-subtle)" : "none" }}>
                                    <td style={{ padding: "8px 12px" }}>
                                      <div style={{ fontWeight: 500, color: "var(--color-text)", display: "flex", alignItems: "center", gap: 4 }}>
                                        <span title={name} style={{ overflow: "hidden", textOverflow: "ellipsis" }}>{name}</span>
                                        {isCopyable && <CopyButton value={name} label={typeLabel} size={11}/>}
                                      </div>
                                      <div data-inline-style="inline-405b98fd97">
                                        {typeLabel}
                                        {e.externalId && <> · <span title={e.externalId}>{e.externalId.length > 16 ? e.externalId.slice(0, 16) + "…" : e.externalId}</span></>}
                                      </div>
                                    </td>
                                    <td data-inline-style="inline-aee7a84582" title={e.title}>{e.title}</td>
                                    <td data-inline-style="inline-3f9e637a11" title={e.detectedAt ? fmtDate(e.detectedAt) : undefined}>{e.detectedAt ? `${relTime(e.detectedAt)} (${fmtDate(e.detectedAt)})` : "N/A"}</td>
                                    <td data-inline-style="inline-6457a71ac2">
                                      {e.userPrincipalName && (
                                        <button
                                          onClick={() => { setSelectedTriggered(null); crossNavigate({ page: "identity", search: e.userPrincipalName! }); }}
                                          title={`View ${e.userPrincipalName} in Identity`}
                                          data-inline-style="inline-3e8fdbf290">
                                          Identity <ArrowRight size={10}/>
                                        </button>
                                      )}
                                      {!e.userPrincipalName && e.deviceName && (
                                        <button
                                          onClick={() => { setSelectedTriggered(null); crossNavigate({ page: "devices", search: e.deviceName! }); }}
                                          title={`View ${e.deviceName} in Devices`}
                                          data-inline-style="inline-3e8fdbf290">
                                          Devices <ArrowRight size={10}/>
                                        </button>
                                      )}
                                      {e.portalUrl && (
                                        <a href={e.portalUrl} target="_blank" rel="noopener noreferrer" data-inline-style="inline-77bb2699b3">
                                          Portal <ExternalLink size={10}/>
                                        </a>
                                      )}
                                    </td>
                                  </tr>
                                );
                              })}
                            </tbody>
                          </table>
                        </div>
                        {meaningful.length > MAX_SHOWN && (
                          <div data-inline-style="inline-5eaedbb850">
                            +{meaningful.length - MAX_SHOWN} more matching entit{meaningful.length - MAX_SHOWN === 1 ? "y" : "ies"} — export or open the source page for the full list.
                          </div>
                        )}
                      </div>
                    );
                  }
                } catch (e) {
                  console.error("Failed to parse affected entities", e);
                }
                return null;
              })()}
            </div>
            <div className="detail-modal-footer">
              <button className="dm-close-btn" onClick={() => setSelectedTriggered(null)}>Close</button>
            </div>
          </div>
        </div>
      )}

      {/* Tabs — underline style so they read as a level below the section tabs */}
      <div className="ac-tabs ac-tabs-underline" role="tablist" aria-label="Alert Center views">
        {(["dashboard","alerts","policies","templates","coverage","notifications","metrics","suppression","runs"] as AcTab[]).map(t => (
          <button key={t} className={`ac-tab${tab===t?" active":""}`} onClick={() => { setTab(t); if (t === "alerts" || t === "dashboard") refresh(); }}>
            {t === "dashboard" ? "Dashboard" : t === "alerts" ? "Active Alerts" : t === "policies" ? "Policies" : t === "templates" ? "Templates" : t === "coverage" ? "Coverage Scorecard" : t === "notifications" ? "Notifications" : t === "metrics" ? "Metrics" : t === "suppression" ? "Suppression" : "Collection Runs"}
          </button>
        ))}
      </div>

      {/* ── TAB: Coverage Scorecard ── */}
      {tab === "coverage" && <CoverageScorecardTab onChanged={onChanged}/>}

      {/* ── TAB: Notifications ── */}
      {tab === "notifications" && <NotificationSettingsTab/>}

      {/* ── TAB: Suppression ── */}
      {tab === "suppression" && <SuppressionRulesTab policies={policies}/>}

      {/* ── TAB: Metrics ── */}
      {tab === "metrics" && <AlertMetricsTab/>}

      {/* ── TAB: Collection Runs ── */}
      {tab === "runs" && <CollectionRunHistory/>}

      {/* ── TAB: Dashboard ── */}
      {tab === "dashboard" && (
        <>
          <div className="kpi-row">
            <KpiTile icon={<Bell size={18}/>}         label="ACTIVE POLICIES"   value={enabledCount}      sub={`${policies.length} total policies`}        tone={enabledCount>0?"good":"neutral"} onClick={() => setTab("policies")}/>
            <KpiTile icon={<AlertCircle size={18}/>}  label="ACTIVE ALERTS"     value={activeAlertsCount} sub="Unacknowledged"                              tone={activeAlertsCount>0?"error":"good"} onClick={() => { setSevFilter(""); setDateFilter(""); setStatusFilter("new"); setTab("alerts"); }}/>
            <KpiTile icon={<Clock size={18}/>}        label="TRIGGERED TODAY"   value={triggeredToday}    sub={fmtShort(new Date().toISOString())}          tone={triggeredToday>0?"warning":"good"} onClick={() => { setSevFilter(""); setStatusFilter(""); setDateFilter(new Date().toLocaleDateString("en-CA")); setTab("alerts"); }}/>
            <KpiTile icon={<ShieldAlert size={18}/>}  label="CRITICAL ALERTS"   value={criticalCount}     sub="Severity: critical"                          tone={criticalCount>0?"error":"good"} onClick={() => { setDateFilter(""); setSevFilter("critical"); setStatusFilter("new"); setTab("alerts"); }}/>
          </div>

          <div className="ac-collection-health">
            <CollectionHealthCard refreshKey={triggeredAlerts.length}/>
          </div>

          <div className="mid-row">
            <Card title="Alerts Triggered (Last 7 Days)" className="card-score">
              {triggeredAlerts.length === 0 ? (
                <EmptyState icon={<Bell size={28}/>} message="No alerts triggered yet. Policies are monitoring the environment."/>
              ) : (
                <svg viewBox={`0 0 420 110`} data-inline-style="inline-10f1c87ffb">
                  {last7.map((d, i) => {
                    const barH = barMax > 0 ? Math.max(4, (d.count / barMax) * 80) : 4;
                    const x = 10 + i * 58;
                    return (
                      <g key={d.date}>
                        <rect x={x} y={90 - barH} width={42} height={barH} rx={4} fill="#3b82f6" opacity="0.8"/>
                        {d.count > 0 && <text x={x+21} y={85-barH} textAnchor="middle" fontSize="10" fill="#3b82f6" fontWeight="600">{d.count}</text>}
                        <text x={x+21} y={106} textAnchor="middle" fontSize="9" fill="#94a3b8">{d.date}</text>
                      </g>
                    );
                  })}
                </svg>
              )}
            </Card>

            <Card title="Alerts by Category">
              {catCounts.length === 0 ? (
                <EmptyState icon={<Activity size={28}/>} message="No triggered alerts yet"/>
              ) : (
                <div data-inline-style="inline-c44bd08c16">
                  <svg viewBox="0 0 100 100" width={100} height={100} data-inline-style="inline-69271fc98e">
                    {(() => {
                      const total = catCounts.reduce((s,[,v]) => s+v, 0);
                      let offset = 0;
                      return catCounts.map(([cat, count]) => {
                        const pct = count / total;
                        const circ = 2 * Math.PI * 38;
                        const dash = pct * circ;
                        const el = (
                          <circle key={cat} cx="50" cy="50" r="38" fill="none"
                            stroke={catColors[cat] ?? "#94a3b8"} strokeWidth="18"
                            strokeDasharray={`${dash} ${circ}`}
                            strokeDashoffset={-offset * circ}
                            transform="rotate(-90 50 50)"/>
                        );
                        offset += pct;
                        return el;
                      });
                    })()}
                    <circle cx="50" cy="50" r="29" data-inline-style="inline-5c9713dbd5"/>
                    <text x="50" y="54" textAnchor="middle" fontSize="12" fontWeight="700" data-inline-style="inline-15ed414312">{catCounts.reduce((s,[,v])=>s+v,0)}</text>
                  </svg>
                  <div data-inline-style="inline-126244f135">
                    {catCounts.map(([cat, count]) => (
                      <div key={cat} style={{ display:"flex", alignItems:"center", gap:6, marginBottom:4 }}>
                        <span style={{ width:10, height:10, borderRadius:"50%", background: catColors[cat]??"#94a3b8", flexShrink:0 }}/>
                        <span style={{ fontSize:12, flex:1, textTransform:"capitalize" }}>{cat}</span>
                        <span style={{ fontSize:12, fontWeight:600 }}>{count}</span>
                      </div>
                    ))}
                  </div>
                </div>
              )}
            </Card>

            <Card title="Recent Alerts" action={<button className="btn-export" onClick={() => setTab("alerts")}>View All</button>}>
              {triggeredAlerts.length === 0 ? (
                <EmptyState icon={<CheckCircle size={24} color="var(--status-good-icon)"/>} message="No alerts triggered yet"/>
              ) : (
                <div className="mini-list">
                  {[...triggeredAlerts].sort((a,b) => new Date(b.triggeredAt).getTime()-new Date(a.triggeredAt).getTime()).slice(0,10).map((a,i) => (
                    <div key={i} className="mini-row" data-inline-style="inline-7c0f86ab54" onClick={() => setSelectedTriggered(a)}>
                      <span className={`sev-dot sev-${a.severity}`}/>
                      <span className="mr-user" data-inline-style="inline-126244f135">{a.policyName}</span>
                      <Badge label={fmtStatus(a.status)} tone={statusTone(a.status)}/>
                      {a.snoozedUntil && new Date(a.snoozedUntil) > new Date() && (
                        <span data-inline-style="inline-405b98fd97">snoozed until {relTime(a.snoozedUntil)}</span>
                      )}
                      <span className="mr-date">{relTime(a.triggeredAt)}</span>
                      {canMutate && a.status === "new" && (
                        <button className="btn-ack" onClick={e => { e.stopPropagation(); acknowledge(a.id); }}>Ack</button>
                      )}
                    </div>
                  ))}
                </div>
              )}
            </Card>
          </div>
        </>
      )}

      {/* ── TAB: Active Alerts ── */}
      {tab === "alerts" && (
        <>
          <CollectionStatusBanner refreshKey={triggeredAlerts.length}/>
          <Card title="Active Alerts" badge={<Badge label={`${filteredTA.length} shown`} tone="neutral"/>}
            action={
            <div data-inline-style="inline-a0c5370730">
              <label className="search-box">
                <Search size={14}/>
                <input className="search-input" value={search} onChange={e=>setSearch(e.target.value)} placeholder="Search policy…"/>
              </label>
              <input type="date" className="filter-sel" value={dateFilter} onChange={e=>setDateFilter(e.target.value)} title="Filter by date"/>
              <SeverityFilter value={sevFilter} onChange={setSevFilter}/>
              <select className="filter-sel" value={catFilter} onChange={e=>setCatFilter(e.target.value)}>
                <option value="">All categories</option>
                {["identity","devices","email","compliance","licenses"].map(c=><option key={c} value={c}>{c.charAt(0).toUpperCase()+c.slice(1)}</option>)}
              </select>
              <select className="filter-sel" value={statusFilter} onChange={e=>setStatusFilter(e.target.value)}>
                <option value="">All statuses</option>
                <option value="new">New</option>
                <option value="acknowledged">Acknowledged</option>
                <option value="snoozed">Snoozed</option>
                <option value="auto_resolved">Auto-resolved</option>
                <option value="resolved">Resolved</option>
              </select>
              <select className="filter-sel" value={assignedFilter} onChange={e=>setAssignedFilter(e.target.value)}>
                <option value="">All owners</option>
                {assignees.map(email => <option key={email} value={email}>{email}</option>)}
              </select>
              <select className="filter-sel" value={ageFilter} onChange={e=>setAgeFilter(e.target.value)}>
                <option value="">Any age</option>
                <option value="under4">Under 4 hours</option>
                <option value="4to24">4–24 hours</option>
                <option value="over24">Over 24 hours</option>
                <option value="overdue">Unacknowledged over 24 hours</option>
              </select>
              <FilterPresets pageKey="alert-center" filters={{ search, sevFilter, catFilter, statusFilter, dateFilter, assignedFilter, ageFilter }} onLoad={f => {
                setSearch(f.search ?? ""); setSevFilter(f.sevFilter ?? ""); setCatFilter(f.catFilter ?? "");
                setStatusFilter(f.statusFilter ?? ""); setDateFilter(f.dateFilter ?? ""); setAssignedFilter(f.assignedFilter ?? ""); setAgeFilter(f.ageFilter ?? ""); setPageNum(1);
              }}/>
              <ExportDropdown rows={filteredTA.map(a=>({ Policy:a.policyName, Severity:a.severity, Category:a.category, Condition:a.condition, MetricValue:a.metricValue, Threshold:a.threshold, Triggered:a.triggeredAt, Status:a.status }))} filename="triggered-alerts.csv"/>
              {(search||sevFilter||catFilter||statusFilter||dateFilter||assignedFilter||ageFilter)&&<button className="btn-apply" data-inline-style="inline-84a31235d6" onClick={()=>{setSearch("");setSevFilter("");setCatFilter("");setStatusFilter("");setDateFilter("");setAssignedFilter("");setAgeFilter("");setPageNum(1);}}>Clear</button>}
            </div>
            }>
          {canMutate && selected.size > 0 && (
            <div className="bulk-bar">
              <span className="bulk-count">{selected.size} selected</span>
              <button className="btn-ack" disabled={bulkBusy} onClick={() => bulkAction("acknowledge")}>Acknowledge</button>
              <button className="btn-resolve" disabled={bulkBusy} onClick={() => bulkAction("resolve")}>Resolve</button>
              <button className="btn-export" data-inline-style="inline-8d467d8c7d" onClick={() => setSelected(new Set())}>Clear selection</button>
            </div>
          )}
          {filteredTA.length === 0 ? (
            <EmptyState icon={<CheckCircle size={28} color="var(--status-good-icon)"/>} message="No alerts triggered yet. Your policies are monitoring the environment."/>
          ) : (
            <>
            <div className="tbl-wrap">
              <table className="data-tbl">
                <caption className="sr-only">Active alerts. Select a column heading to change the sort order.</caption>
                <thead>
                  <tr>
                    {canMutate && <th scope="col" data-inline-style="inline-5c2c2a81c2">
                      <input type="checkbox" checked={pageAllSelected} onChange={togglePageAll} aria-label="Select all alerts on this page"/>
                    </th>}
                    <SortTh label="Severity"  col="severity"   sortBy={sortBy} sortDir={sortDir} onSort={toggleSort}/>
                    <SortTh label="Policy"    col="policyName" sortBy={sortBy} sortDir={sortDir} onSort={toggleSort}/>
                    <th scope="col">Condition</th>
                    <th scope="col">Value</th>
                    <SortTh label="Triggered" col="triggeredAt" sortBy={sortBy} sortDir={sortDir} onSort={toggleSort}/>
                    <th scope="col" title="How long this alert has waited. Red = unacknowledged for over 24h.">Age</th>
                    <SortTh label="Status"    col="status"     sortBy={sortBy} sortDir={sortDir} onSort={toggleSort}/>
                    <SortTh label="Assigned"  col="assignedTo" sortBy={sortBy} sortDir={sortDir} onSort={toggleSort}/>
                    <th scope="col">Actions</th>
                  </tr>
                </thead>
                <tbody>
                  {pagedTA.map(a => {
                    const sla = slaAge(a);
                    return (
                    <tr key={a.id} className="clickable" {...rowActivation(() => setSelectedTriggered(a), `Open triggered alert ${a.policyName}`)}>
                      {canMutate && <td onClick={e=>e.stopPropagation()}>
                        <input type="checkbox" checked={selected.has(a.id)} onChange={() => toggleSelect(a.id)} aria-label={`Select ${a.policyName}`}/>
                      </td>}
                      <td><Badge label={a.severity} tone={sevToneAC(a.severity)}/></td>
                      <td data-inline-style="inline-7ba9bad628">{a.policyName}</td>
                      <td data-inline-style="inline-af7da65b76">{a.condition}</td>
                      <td data-inline-style="inline-3d9df89ef8">{a.metricValue} <span data-inline-style="inline-3984b68182">/ {a.threshold}</span></td>
                      <td className="al-date">{relTime(a.triggeredAt)}</td>
                      <td className="al-date" style={sla.overdue ? { color:"var(--status-error-text)", fontWeight:700 } : undefined}
                        title={sla.overdue ? "Unacknowledged for over 24 hours" : undefined}>{sla.label}</td>
                      <td><Badge label={fmtStatus(a.status)} tone={statusTone(a.status)}/>
                        {a.snoozedUntil && new Date(a.snoozedUntil) > new Date() && (
                          <div data-inline-style="inline-a1430751ce">snoozed until {relTime(a.snoozedUntil)}</div>
                        )}
                      </td>
                      <td className="al-date">{a.assignedTo ? a.assignedTo.split("@")[0] : "—"}</td>
                      <td onClick={e=>e.stopPropagation()} data-inline-style="inline-4f34ecc34f">
                        {!canMutate && <span data-inline-style="inline-b53327a69e">—</span>}
                        {canMutate && a.status === "new" && <button className="btn-ack" onClick={()=>acknowledge(a.id)}>Acknowledge</button>}
                        {canMutate && a.status !== "resolved" && a.status !== "auto_resolved" && (
                          <select className="filter-sel" data-inline-style="inline-ceabc00151" defaultValue=""
                            onChange={e => { const h = Number(e.target.value); if (h) snooze(a.id, h as 4|24|168); e.currentTarget.value = ""; }}
                            title="Snooze for…">
                            <option value="" disabled>Snooze…</option>
                            <option value="4">4h</option>
                            <option value="24">24h</option>
                            <option value="168">7d</option>
                          </select>
                        )}
                        {canMutate && a.snoozedUntil && new Date(a.snoozedUntil) > new Date() && (
                          <button className="btn-apply" data-inline-style="inline-6ba117109b"
                            onClick={() => unsnooze(a.id)}
                            title={`Snoozed until ${a.snoozedUntil}`}>Unsnooze</button>
                        )}
                        {canMutate && a.status !== "resolved" && a.status !== "auto_resolved" && <button className="btn-resolve" onClick={()=>resolve(a.id)}>Resolve</button>}
                      </td>
                    </tr>
                  );})}
                </tbody>
              </table>
            </div>
            <div className="sr-only" role="status" aria-live="polite" aria-atomic="true">{sortAnnouncement}</div>
            <div className="tbl-footer">
              <span>Showing {(safePage - 1) * PAGE_SIZE + 1}–{Math.min(safePage * PAGE_SIZE, filteredTA.length)} of {filteredTA.length}</span>
              <div data-inline-style="inline-3633e433a1">
                <button className="btn-export" data-inline-style="inline-3e2b0a153b" disabled={safePage <= 1} onClick={() => setPageNum(p => p - 1)}>‹ Prev</button>
                <span data-inline-style="inline-7271ab91ae">Page {safePage} of {pageCount}</span>
                <button className="btn-export" data-inline-style="inline-3e2b0a153b" disabled={safePage >= pageCount} onClick={() => setPageNum(p => p + 1)}>Next ›</button>
              </div>
            </div>
            </>
          )}
          </Card>
        </>
      )}

      {/* ── TAB: Policies ── */}
      {tab === "policies" && (
        <Card title="Alert Policies" badge={<Badge label={`${policies.length} policies`} tone="neutral"/>}
          action={
            <div data-inline-style="inline-f8df590e45">
              <PolicyPackControls onChanged={onChanged}/>
              <button className="btn-run" data-inline-style="inline-6d8e211e39" onClick={() => { setEditPolicy(null); setShowModal(true); }}><Bell size={13}/> New Policy</button>
            </div>
          }>
          {policies.length === 0 ? (
            <EmptyState icon={<Bell size={28}/>} message="No policies yet. Create one or use a template."/>
          ) : (
            <div className="tbl-wrap">
              <table className="data-tbl">
                <thead>
                  <tr><th scope="col">Name</th><th scope="col">Category</th><th scope="col">Condition</th><th scope="col">Severity</th><th scope="col">Status</th><th scope="col">Last Triggered</th><th scope="col">Count</th><th scope="col">Actions</th></tr>
                </thead>
                <tbody>
                  {policies.map(p => (
                    <tr key={p.id}>
                      <td data-inline-style="inline-7ba9bad628">{p.name}</td>
                      <td data-inline-style="inline-b7b96646ae">{p.category}</td>
                      <td data-inline-style="inline-e1acedac9b">{p.condition}</td>
                      <td><Badge label={p.severity} tone={sevToneAC(p.severity)}/></td>
                      <td>
                        <button
                          onClick={() => togglePolicy(p.id)}
                          style={{ padding:"2px 10px", borderRadius:5, border:"1px solid", fontSize:11, fontWeight:600, cursor:"pointer",
                            borderColor: p.enabled?"var(--status-good-border)":"var(--color-border)", background: p.enabled?"var(--status-good-bg)":"var(--color-raised)", color: p.enabled?"var(--status-good-text)":"var(--color-muted)" }}>
                          {p.enabled ? "Enabled" : "Disabled"}
                        </button>
                      </td>
                      <td className="al-date">{p.lastTriggered ? relTime(p.lastTriggered) : "Never"}</td>
                      <td data-inline-style="inline-3d9df89ef8">{p.triggerCount}</td>
                      <td data-inline-style="inline-95e7b1fc4c">
                        <button className="btn-export" data-inline-style="inline-fcd3bb7174" onClick={() => { setEditPolicy(p); setShowModal(true); }}>Edit</button>
                        <button className="btn-ack" onClick={() => handleDeletePolicy(p.id)}>Delete</button>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </Card>
      )}

      {/* ── TAB: Templates ── */}
      {tab === "templates" && (
        <Card title="Policy Templates" badge={<Badge label={`${POLICY_TEMPLATES_CATALOG.length} templates`} tone="neutral"/>}>
          <div className="template-grid">
            {POLICY_TEMPLATES_CATALOG.map((t, i) => (
              <div key={i} className="template-card">
                <div className="template-card-title">{t.name}</div>
                <div className="template-card-desc">{t.desc}</div>
                <div className="template-card-footer">
                  <div data-inline-style="inline-95e7b1fc4c">
                    <Badge label={t.severity} tone={sevToneAC(t.severity)}/>
                    <Badge label={t.category} tone="neutral"/>
                  </div>
                  <button className="btn-run" data-inline-style="inline-ead46142c8" onClick={() => useTemplate(t)}>Use Template</button>
                </div>
              </div>
            ))}
          </div>
        </Card>
      )}
    </div>
  );
}
