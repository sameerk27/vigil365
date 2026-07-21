import React, { useState, useEffect, useCallback } from "react";
import { FileText, Mail, Plus, Play, Trash2, Clock, Download } from "lucide-react";
import { ReportSchedule, DigestPreview } from "../services/types";
import { reportApi, useAuth } from "../services/api";
import { fmtShort } from "../services/utils";
import { Card, Badge, EmptyState, LoadingSkeleton } from "../components/SharedComponents";
import { showToast } from "../services/toast";
import { confirmAction } from "../services/confirm";

const DAYS = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

function blankSchedule(): Partial<ReportSchedule> {
  return { name: "Weekly executive digest", cadence: "weekly", dayOfWeek: 1, dayOfMonth: 1, hourUtc: 7, recipients: "", includeCsv: true, enabled: true };
}

function cadenceLabel(s: ReportSchedule): string {
  if (s.cadence === "daily") return `Daily at ${String(s.hourUtc).padStart(2, "0")}:00 UTC`;
  if (s.cadence === "monthly") return `Monthly on day ${s.dayOfMonth} at ${String(s.hourUtc).padStart(2, "0")}:00 UTC`;
  return `Weekly on ${DAYS[s.dayOfWeek]} at ${String(s.hourUtc).padStart(2, "0")}:00 UTC`;
}

export function ReportsPage() {
  const { canMutate } = useAuth();
  const [schedules, setSchedules] = useState<ReportSchedule[] | null>(null);
  const [preview, setPreview] = useState<DigestPreview | null>(null);
  const [windowDays, setWindowDays] = useState(7);
  const [editing, setEditing] = useState<Partial<ReportSchedule> | null>(null);
  const [busy, setBusy] = useState(false);

  const loadSchedules = useCallback(async () => setSchedules(await reportApi.list()), []);
  const loadPreview = useCallback(async (days: number) => setPreview(await reportApi.preview(days)), []);

  useEffect(() => { loadSchedules(); }, [loadSchedules]);
  useEffect(() => { loadPreview(windowDays); }, [windowDays, loadPreview]);

  const save = async () => {
    if (!editing) return;
    if (!editing.recipients?.trim()) { showToast("Add at least one recipient email", "error"); return; }
    setBusy(true);
    const result = editing.id ? await reportApi.update(editing as ReportSchedule) : await reportApi.create(editing);
    setBusy(false);
    if (result) { showToast(editing.id ? "Schedule updated" : "Schedule created", "success"); setEditing(null); loadSchedules(); }
    else showToast("Could not save the schedule", "error");
  };

  const remove = async (s: ReportSchedule) => {
    const ok = await confirmAction({
      title: "Delete report schedule?",
      message: `"${s.name}" will stop sending. Reports already delivered are unaffected.`,
      confirmLabel: "Delete schedule",
      danger: true,
    });
    if (!ok) return;
    if (await reportApi.remove(s.id)) { showToast("Schedule deleted", "success"); loadSchedules(); }
    else showToast("Could not delete the schedule", "error");
  };

  const runNow = async (s: ReportSchedule) => {
    setBusy(true);
    const r = await reportApi.runNow(s.id);
    setBusy(false);
    showToast(r.ok ? `Sent — ${r.status}` : `Failed — ${r.status ?? "check SMTP settings"}`, r.ok ? "success" : "error");
    loadSchedules();
  };

  const toggleEnabled = async (s: ReportSchedule) => {
    const updated = await reportApi.update({ ...s, enabled: !s.enabled });
    if (updated) loadSchedules();
  };

  const downloadCsv = () => {
    if (!preview?.csv) return;
    const blob = new Blob([preview.csv], { type: "text/csv" });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url; a.download = `vigil365-digest-${preview.generatedAt.slice(0, 10)}.csv`; a.click();
    URL.revokeObjectURL(url);
  };

  return (
    <div className="page">
      <p className="page-intro"><FileText size={15} style={{ verticalAlign: "-2px", marginRight: 6 }}/>
        <b>What this page does:</b> it emails a weekly/daily/monthly executive summary of your security posture
        (the preview below is exactly what recipients get). Create a schedule, add recipients, and Vigil365
        sends it automatically — using the SMTP settings under <b>Alerts → Rules &amp; Notifications</b>.
        Read-only; nothing is changed in your tenant.</p>

      {/* ── Live preview of the executive digest ─────────────────────────────── */}
      <Card title="Executive digest — preview" id="digest-preview" action={
        <div style={{ display: "flex", gap: 8, alignItems: "center" }}>
          <label style={{ fontSize: 12, color: "var(--color-muted)" }}>Window
            <select value={windowDays} onChange={e => setWindowDays(Number(e.target.value))} style={{ marginLeft: 6 }}>
              <option value={1}>1 day</option><option value={7}>7 days</option><option value={30}>30 days</option>
            </select>
          </label>
          {preview?.csv && <button className="btn-secondary" style={{ fontSize: 12 }} onClick={downloadCsv}><Download size={13} style={{ verticalAlign: "-2px", marginRight: 4 }}/>CSV</button>}
        </div>
      }>
        {preview === null ? <LoadingSkeleton /> : !preview.hasData ? (
          <EmptyState message="No data yet — trend metrics and alerts appear once the collector has run at least once."/>
        ) : (
          <div className="digest-preview-grid">
            <div>
              <div className="dm-section-hdr">Posture</div>
              {preview.metrics.length === 0 ? (
                <p style={{ fontSize: 13, color: "var(--color-muted)" }}>No posture snapshot captured yet.</p>
              ) : (
                <table className="digest-metric-table">
                  <tbody>
                    {preview.metrics.map(m => {
                      const worse = m.delta != null && Math.abs(m.delta) >= 0.05 && (m.higherIsWorse ? m.delta > 0 : m.delta < 0);
                      const better = m.delta != null && Math.abs(m.delta) >= 0.05 && !worse;
                      return (
                        <tr key={m.label}>
                          <td>{m.label}</td>
                          <td><b>{m.value}</b></td>
                          <td style={{ color: worse ? "var(--sev-high-text)" : better ? "var(--status-good-text)" : "var(--color-muted)" }}>
                            {m.delta == null || Math.abs(m.delta) < 0.05 ? "—" : `${m.delta > 0 ? "▲" : "▼"} ${Math.abs(m.delta).toFixed(1)}${m.deltaLabel ? " " + m.deltaLabel : ""}`}
                          </td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              )}
            </div>
            <div>
              <div className="dm-section-hdr">Top open alerts</div>
              {preview.topAlerts.length === 0 ? (
                <p style={{ fontSize: 13, color: "var(--status-good-text)" }}>No open alerts.</p>
              ) : (
                <ul className="digest-alert-list">
                  {preview.topAlerts.map((a, i) => (
                    <li key={i}>
                      <Badge label={a.severity.toUpperCase()} tone={a.severity === "critical" || a.severity === "high" ? "error" : a.severity === "medium" ? "warning" : "info"}/>
                      <span className="digest-alert-name">{a.policyName}</span>
                      <span className="digest-alert-cond">{a.condition}</span>
                    </li>
                  ))}
                </ul>
              )}
            </div>
          </div>
        )}
      </Card>

      {/* ── Schedules ────────────────────────────────────────────────────────── */}
      <Card title="Delivery schedules" id="report-schedules" action={
        canMutate && !editing && <button className="btn-apply" style={{ fontSize: 12 }} onClick={() => setEditing(blankSchedule())}><Plus size={13} style={{ verticalAlign: "-2px", marginRight: 4 }}/>New schedule</button>
      }>
        {editing && (
          <div className="report-editor">
            <div className="report-editor-grid">
              <label>Name<input value={editing.name ?? ""} onChange={e => setEditing({ ...editing, name: e.target.value })}/></label>
              <label>Cadence
                <select value={editing.cadence} onChange={e => setEditing({ ...editing, cadence: e.target.value as ReportSchedule["cadence"] })}>
                  <option value="daily">Daily</option><option value="weekly">Weekly</option><option value="monthly">Monthly</option>
                </select>
              </label>
              {editing.cadence === "weekly" && (
                <label>Day of week
                  <select value={editing.dayOfWeek} onChange={e => setEditing({ ...editing, dayOfWeek: Number(e.target.value) })}>
                    {DAYS.map((d, i) => <option key={i} value={i}>{d}</option>)}
                  </select>
                </label>
              )}
              {editing.cadence === "monthly" && (
                <label>Day of month<input type="number" min={1} max={28} value={editing.dayOfMonth} onChange={e => setEditing({ ...editing, dayOfMonth: Number(e.target.value) })}/></label>
              )}
              <label>Hour (UTC)<input type="number" min={0} max={23} value={editing.hourUtc} onChange={e => setEditing({ ...editing, hourUtc: Number(e.target.value) })}/></label>
            </div>
            <label style={{ display: "block", marginTop: 10 }}>Recipients (comma-separated)
              <input value={editing.recipients ?? ""} placeholder="ciso@contoso.com, soc@contoso.com" onChange={e => setEditing({ ...editing, recipients: e.target.value })}/>
            </label>
            <div style={{ display: "flex", gap: 16, marginTop: 10, alignItems: "center" }}>
              <label style={{ display: "flex", gap: 6, alignItems: "center", fontSize: 13 }}><input type="checkbox" checked={editing.includeCsv ?? true} onChange={e => setEditing({ ...editing, includeCsv: e.target.checked })}/>Attach CSV</label>
              <label style={{ display: "flex", gap: 6, alignItems: "center", fontSize: 13 }}><input type="checkbox" checked={editing.enabled ?? true} onChange={e => setEditing({ ...editing, enabled: e.target.checked })}/>Enabled</label>
              <div style={{ marginLeft: "auto", display: "flex", gap: 8 }}>
                <button className="btn-secondary" onClick={() => setEditing(null)}>Cancel</button>
                <button className="btn-apply" disabled={busy} onClick={save}>{editing.id ? "Save" : "Create"}</button>
              </div>
            </div>
          </div>
        )}

        {schedules === null ? <LoadingSkeleton /> : schedules.length === 0 && !editing ? (
          <EmptyState message={canMutate ? "No schedules yet — create one to email the executive digest on a recurring cadence." : "No report schedules have been configured."}/>
        ) : (
          <table className="data-table">
            <thead><tr><th scope="col">Report</th><th scope="col">Cadence</th><th scope="col">Recipients</th><th scope="col">Last run</th><th scope="col"></th></tr></thead>
            <tbody>
              {schedules?.map(s => (
                <tr key={s.id} style={{ opacity: s.enabled ? 1 : 0.55 }}>
                  <td><b>{s.name}</b>{!s.enabled && <span style={{ fontSize: 11, color: "var(--color-muted)", marginLeft: 6 }}>(disabled)</span>}</td>
                  <td style={{ whiteSpace: "nowrap" }}><Clock size={12} style={{ verticalAlign: "-2px", marginRight: 4, color: "var(--color-muted)" }}/>{cadenceLabel(s)}</td>
                  <td style={{ fontSize: 12, color: "var(--color-muted)" }}><Mail size={12} style={{ verticalAlign: "-2px", marginRight: 4 }}/>{s.recipients || "—"}</td>
                  <td style={{ fontSize: 12, color: "var(--color-muted)" }}>{s.lastRunAt ? `${fmtShort(s.lastRunAt)} · ${s.lastRunStatus ?? ""}` : "never"}</td>
                  <td style={{ whiteSpace: "nowrap", textAlign: "right" }}>
                    {canMutate && <>
                      <button className="icon-btn" title="Send now" disabled={busy} onClick={() => runNow(s)}><Play size={14}/></button>
                      <button className="icon-btn" title={s.enabled ? "Disable" : "Enable"} onClick={() => toggleEnabled(s)}><Clock size={14}/></button>
                      <button className="icon-btn" title="Edit" onClick={() => setEditing(s)}>Edit</button>
                      <button className="icon-btn btn-danger-icon" title="Delete" onClick={() => remove(s)}><Trash2 size={14}/></button>
                    </>}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </Card>
    </div>
  );
}
