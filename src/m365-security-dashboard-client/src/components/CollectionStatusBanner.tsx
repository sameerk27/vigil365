import React, { useEffect, useState } from "react";
import { AlertTriangle, CheckCircle2, Database } from "lucide-react";
import { apiBase, apiFetch } from "../services/api";
import { relTime } from "../services/utils";

type HealthResponse = {
  status: "healthy" | "degraded" | "unhealthy";
  checks?: {
    database?: { ok: boolean };
    graph?: { configured: boolean };
    collection?: { startedAt: string; status: string; fresh: boolean } | null;
  };
};

/**
 * Keeps data freshness visible at the point of triage. The health endpoint is
 * deliberately cheap: it only checks persisted collector state and never calls
 * Microsoft Graph.
 */
export function CollectionStatusBanner({ refreshKey }: { refreshKey: number }) {
  const [health, setHealth] = useState<HealthResponse | null>(null);

  useEffect(() => {
    let cancelled = false;
    const load = () => apiFetch(`${apiBase}/health`)
      .then(r => r.json() as Promise<HealthResponse>)
      .then(data => { if (!cancelled) setHealth(data); })
      .catch(() => { if (!cancelled) setHealth({ status: "unhealthy" }); });

    load();
    const timer = window.setInterval(load, 60_000);
    return () => { cancelled = true; window.clearInterval(timer); };
  }, [refreshKey]);

  if (!health) return null;

  const collection = health.checks?.collection;
  const graphConfigured = health.checks?.graph?.configured;
  const isHealthy = health.status === "healthy";
  const message = health.status === "unhealthy"
    ? "Collection status is unavailable because the database cannot be reached. Treat alert data as unavailable."
    : !graphConfigured
      ? "Microsoft Graph is not configured, so Vigil365 cannot collect new alert data."
      : collection?.fresh === false
        ? `Alert data may be stale — the last collection started ${relTime(collection.startedAt)}.`
        : collection
          ? `Alert data is current — last collection started ${relTime(collection.startedAt)}.`
          : "No collection has completed yet. Alert data will appear after the first successful run.";

  return (
    <div className={`sys-status-banner collector-status-banner ${isHealthy ? "status-ok" : health.status === "unhealthy" ? "status-error" : "status-degraded"}`} role="status">
      {isHealthy ? <CheckCircle2 size={16} aria-hidden="true"/> : health.status === "unhealthy" ? <Database size={16} aria-hidden="true"/> : <AlertTriangle size={16} aria-hidden="true"/>}
      <span>{message}</span>
    </div>
  );
}
