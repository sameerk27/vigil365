import React, { useEffect, useState } from "react";
import { Clock, CheckCircle2, Timer, Users } from "lucide-react";
import { apiBase, apiFetch } from "../services/api";
import { Card, Badge, StatBox, EmptyState, SectHdr } from "./SharedComponents";

type AssigneeLoad = { assignee: string; open: number; acknowledged: number; resolved: number };
type Metrics = {
  total: number; open: number; resolved: number; autoResolved: number;
  resolutionRatePct: number; mttaMinutes: number | null; mttrMinutes: number | null;
  acknowledged: number; byAssignee: AssigneeLoad[];
};
type Response = { windowDays: number; metrics: Metrics };

const WINDOWS = [7, 30, 90] as const;

/** Human-friendly duration from minutes: "42m", "3.2h", "1.5d". */
function fmtDuration(min: number | null): string {
  if (min === null) return "—";
  if (min < 60) return `${Math.round(min)}m`;
  if (min < 60 * 24) return `${(min / 60).toFixed(1)}h`;
  return `${(min / 1440).toFixed(1)}d`;
}

export function AlertMetricsTab() {
  const [days, setDays] = useState<number>(30);
  const [data, setData] = useState<Response | null>(null);
  const [err, setErr] = useState(false);

  useEffect(() => {
    let cancelled = false;
    setErr(false); setData(null);
    apiFetch(`${apiBase}/api/triggered-alerts/metrics?days=${days}`)
      .then(r => r.ok ? r.json() : Promise.reject())
      .then((d: Response) => { if (!cancelled) setData(d); })
      .catch(() => { if (!cancelled) setErr(true); });
    return () => { cancelled = true; };
  }, [days]);

  const m = data?.metrics;

  return (
    <Card title="Alert Operations Metrics"
      badge={m ? <Badge label={`${m.total} alert${m.total !== 1 ? "s" : ""} in window`} tone="neutral"/> : undefined}
      action={
        <div className="pill-group" role="tablist" aria-label="Metrics window">
          {WINDOWS.map(w => (
            <button key={w} className={`pill-btn ${days === w ? "active" : ""}`} onClick={() => setDays(w)}>
              {w}d
            </button>
          ))}
        </div>
      }>
      {err ? (
        <EmptyState message="Could not load metrics"/>
      ) : !m ? (
        <EmptyState message="Loading metrics…"/>
      ) : m.total === 0 ? (
        <EmptyState icon={<Clock size={24}/>} message={`No alerts triggered in the last ${data!.windowDays} days`}/>
      ) : (
        <>
          <div className="metrics-kpi-row">
            <div className="metrics-kpi">
              <div className="metrics-kpi-icon"><Timer size={18}/></div>
              <div><div className="metrics-kpi-val">{fmtDuration(m.mttaMinutes)}</div>
                <div className="metrics-kpi-lbl">Mean time to acknowledge</div></div>
            </div>
            <div className="metrics-kpi">
              <div className="metrics-kpi-icon"><CheckCircle2 size={18}/></div>
              <div><div className="metrics-kpi-val">{fmtDuration(m.mttrMinutes)}</div>
                <div className="metrics-kpi-lbl">Mean time to resolve</div></div>
            </div>
            <div className="metrics-kpi">
              <div className="metrics-kpi-icon"><CheckCircle2 size={18}/></div>
              <div><div className="metrics-kpi-val">{m.resolutionRatePct}%</div>
                <div className="metrics-kpi-lbl">Resolution rate</div></div>
            </div>
          </div>

          <div className="stat-row4" style={{ marginTop: 12 }}>
            <StatBox value={m.open} label="Open" color={m.open > 0 ? "var(--status-warn-text)" : undefined}/>
            <StatBox value={m.acknowledged} label="Acknowledged"/>
            <StatBox value={m.resolved} label="Resolved (manual)"/>
            <StatBox value={m.autoResolved} label="Auto-resolved"/>
          </div>

          <div style={{ marginTop: 16 }}>
            <SectHdr>ANALYST WORKLOAD</SectHdr>
            {m.byAssignee.length === 0 ? (
              <div className="metrics-empty-assignee">
                <Users size={14}/> No alerts are assigned. Assign alerts from the queue to track per-analyst workload.
              </div>
            ) : (
              <div className="table-wrap">
                <table>
                  <thead><tr>
                    <th scope="col">Assignee</th><th scope="col">Open</th>
                    <th scope="col">Acknowledged</th><th scope="col">Resolved</th>
                  </tr></thead>
                  <tbody>
                    {m.byAssignee.map(a => (
                      <tr key={a.assignee}>
                        <td>{a.assignee}</td>
                        <td>{a.open}</td>
                        <td>{a.acknowledged}</td>
                        <td>{a.resolved}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </div>
        </>
      )}
    </Card>
  );
}
