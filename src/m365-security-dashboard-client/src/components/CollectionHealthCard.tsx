import React, { useState, useEffect } from "react";
import { Database, AlertTriangle } from "lucide-react";
import { apiBase, apiFetch, crossNavigate } from "../services/api";
import { Card, Badge, EmptyState, StatBox, SectHdr } from "./SharedComponents";
import { relTime } from "../services/utils";

interface CollectionRunInfo {
  id: number;
  startedAt: string;
  completedAt: string | null;
  status: string; // Started | Completed | Failed
  alertsUpserted: number;
  sourceFailures: number;
  error?: string | null;
  sourceFailureDetails?: string | null;
}

export function CollectionHealthCard({ refreshKey }: { refreshKey: number }) {
  const [runs, setRuns] = useState<CollectionRunInfo[] | null>(null);
  const [err, setErr] = useState(false);

  useEffect(() => {
    let cancelled = false;
    setErr(false);
    apiFetch(`${apiBase}/api/collector/runs`)
      .then(r => r.ok ? r.json() : Promise.reject())
      .then((d: CollectionRunInfo[]) => { if (!cancelled) setRuns(d); })
      .catch(() => { if (!cancelled) setErr(true); });
    return () => { cancelled = true; };
  }, [refreshKey]);

  const finished = (runs ?? []).filter(r => r.status !== "Started");
  const last = finished[0];
  const failures: { source: string; error: string }[] = (() => {
    if (!last?.sourceFailureDetails) return [];
    try { return JSON.parse(last.sourceFailureDetails); } catch { return []; }
  })();
  const durationMs = last?.completedAt ? new Date(last.completedAt).getTime() - new Date(last.startedAt).getTime() : 0;
  const healthy = !!last && last.sourceFailures === 0 && last.status === "Completed";
  const tone: "good"|"warning"|"error" = !last ? "warning" : last.status === "Failed" ? "error" : last.sourceFailures > 0 ? "warning" : "good";

  return (
    <Card title="Collection Health"
      className="act-collection-card"
      badge={<Badge label={!last ? "No runs" : healthy ? "Healthy" : last.status === "Failed" ? "Failed" : `${last.sourceFailures} source issue${last.sourceFailures!==1?"s":""}`} tone={tone}/>}
      action={
        // The card summarises; the Collection Runs tab has the full history and
        // the untruncated error text. Without this the card is a dead end —
        // a 403 renders as '403 Forbidden: {"error":{"c…' with nowhere to go.
        <button type="button" className="card-link-btn"
          onClick={() => crossNavigate({ page: "alertcenter", tab: "runs" })}>
          View runs →
        </button>
      }>
      {err ? (
        <EmptyState message="Could not load collection status"/>
      ) : !runs ? (
        <EmptyState message="Loading collection status…"/>
      ) : !last ? (
        <EmptyState icon={<Database size={22}/>} message="No collection has run yet"/>
      ) : (
        <>
          <div className="stat-row3">
            <StatBox value={last.alertsUpserted} label="Alerts Collected"/>
            <StatBox value={`${(durationMs/1000).toFixed(1)}s`} label="Duration"/>
            <StatBox value={last.sourceFailures} label="Source Failures" color={last.sourceFailures>0?"var(--status-error-text)":undefined}/>
          </div>
          <div className="mini-row" style={{marginTop:10, justifyContent:"space-between"}}>
            <span className="mr-date">Last run {relTime(last.completedAt ?? last.startedAt)}</span>
            <Badge label={`${finished.length} recent run${finished.length!==1?"s":""}`} tone="neutral"/>
          </div>
          {failures.length > 0 ? (
            <div className="mini-list" style={{marginTop:8}}>
              <SectHdr>FAILING SOURCES</SectHdr>
              {failures.map((f,i)=>(
                <button key={i} type="button" className="mini-row failing-source-row" title={f.error}
                  onClick={() => crossNavigate({ page: "alertcenter", tab: "runs" })}>
                  <AlertTriangle size={11} color="var(--sev-high-icon)"/>
                  <span className="mr-user">{f.source}</span>
                  <span className="mr-date trunc" style={{maxWidth:140}}>{f.error}</span>
                </button>
              ))}
            </div>
          ) : (
            <div className="mini-row" style={{marginTop:8, color:"var(--status-good-text)"}}>
              <span style={{fontSize:12}}>All data sources collected without errors on the last run</span>
            </div>
          )}
        </>
      )}
    </Card>
  );
}
