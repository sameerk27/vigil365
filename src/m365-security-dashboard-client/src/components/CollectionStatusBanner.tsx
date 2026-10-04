import React, { useEffect, useState } from "react";
import { AlertTriangle, CheckCircle2, Database } from "lucide-react";
import { apiBase, apiFetch, isMspMode } from "../services/api";
import { relTime } from "../services/utils";

type HealthResponse = {
  status: "healthy" | "degraded" | "unhealthy";
  checks?: {
    database?: { ok: boolean };
    graph?: { configured: boolean };
    /** The newest run of ANY client; staleAfterMinutes is the window "fresh" was judged by. */
    collection?: { startedAt: string; status: string; fresh: boolean; staleAfterMinutes?: number } | null;
  };
};

type CollectionRun = {
  startedAt: string;
  status: "Started" | "Completed" | "Failed";
  sourceFailures: number;
  error?: string | null;
};

/**
 * Keeps data freshness visible at the point of triage. The health endpoint is
 * deliberately cheap: it only checks persisted collector state and never calls
 * Microsoft Graph.
 *
 * /health is anonymous and install-wide: its "collection" is the newest run of
 * any client, whatever its outcome. So whether THIS data is current comes from
 * the selected client's own runs (/api/collector/runs is tenant-scoped): a
 * failing collection, or another client's recent one, is never "current". Only
 * the staleness window comes from /health, and it is applied to this client's
 * own last run — a client the collector silently skips (credentials removed
 * after a success) writes no run, so its last one only ages.
 */
export function CollectionStatusBanner({ refreshKey }: { refreshKey: number }) {
  const [health, setHealth] = useState<HealthResponse | null>(null);
  // null while loading; "error" when the runs could not be read.
  const [runs, setRuns] = useState<CollectionRun[] | "error" | null>(null);

  useEffect(() => {
    let cancelled = false;
    const load = () => {
      apiFetch(`${apiBase}/health`)
        .then(r => r.json() as Promise<HealthResponse>)
        .then(data => { if (!cancelled) setHealth(data); })
        .catch(() => { if (!cancelled) setHealth({ status: "unhealthy" }); });
      apiFetch(`${apiBase}/api/collector/runs`)
        .then(r => { if (!r.ok) throw new Error(`Collection runs request failed (${r.status})`); return r.json() as Promise<CollectionRun[]>; })
        .then(data => { if (!cancelled) setRuns(data); })
        .catch(() => { if (!cancelled) setRuns("error"); });
    };

    load();
    const timer = window.setInterval(load, 60_000);
    return () => { cancelled = true; window.clearInterval(timer); };
  }, [refreshKey]);

  if (!health || (runs === null && health.status !== "unhealthy")) return null;

  const scope = isMspMode() ? " for this client" : "";
  // A run still in progress says nothing yet; judge by the newest finished one.
  const last = Array.isArray(runs) ? runs.find(r => r.status !== "Started") ?? null : null;
  const staleAfterMinutes = health.checks?.collection?.staleAfterMinutes;
  const stale = health.checks?.collection?.fresh === false
    || (last !== null && typeof staleAfterMinutes === "number"
      && Date.now() - new Date(last.startedAt).getTime() > staleAfterMinutes * 60_000);
  const [tone, message]: ["ok" | "degraded" | "error", string] =
    health.status === "unhealthy"
      ? ["error", "Collection status is unavailable because the database cannot be reached. Treat alert data as unavailable."]
    : !health.checks?.graph?.configured
      ? ["degraded", "Microsoft Graph is not configured, so Vigil365 cannot collect new alert data."]
    : runs === "error"
      ? ["degraded", `Collection status${scope} could not be read, so alert data may be out of date.`]
    : !last
      ? ["degraded", `No collection has completed yet${scope}. Alert data will appear after the first successful run.`]
    : last.status === "Failed"
      ? ["error", `The last collection${scope} failed (started ${relTime(last.startedAt)}), so alert data may be out of date.${last.error ? ` ${last.error.length > 200 ? `${last.error.slice(0, 200)}…` : last.error}` : ""}`]
    : stale
      ? ["degraded", `Alert data may be stale — the last collection${scope} started ${relTime(last.startedAt)}.`]
    : last.sourceFailures > 0
      ? ["degraded", `Alert data may be incomplete — the last collection${scope} (started ${relTime(last.startedAt)}) could not read ${last.sourceFailures} source${last.sourceFailures === 1 ? "" : "s"}.`]
    : ["ok", `Alert data is current — last collection${scope} started ${relTime(last.startedAt)}.`];

  return (
    <div className={`sys-status-banner collector-status-banner ${tone === "ok" ? "status-ok" : tone === "error" ? "status-error" : "status-degraded"}`} role="status">
      {tone === "ok" ? <CheckCircle2 size={16} aria-hidden="true"/> : health.status === "unhealthy" ? <Database size={16} aria-hidden="true"/> : <AlertTriangle size={16} aria-hidden="true"/>}
      <span>{message}</span>
    </div>
  );
}
