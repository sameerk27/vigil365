import React, { useCallback, useEffect, useMemo, useState } from "react";
import { AlertTriangle, Database, RefreshCw } from "lucide-react";
import { apiBase, apiFetch } from "../services/api";
import { Badge, Card, CopyButton, EmptyState } from "./SharedComponents";
import { PermissionsReference } from "./PermissionsReference";
import { fmtDate, relTime } from "../services/utils";

type CollectionRun = {
  id: number;
  startedAt: string;
  completedAt: string | null;
  status: "Started" | "Completed" | "Failed";
  alertsUpserted: number;
  sourceFailures: number;
  error?: string | null;
  sourceFailureDetails?: string | null;
};

const statusTone = (status: CollectionRun["status"]) =>
  status === "Completed" ? "good" as const : status === "Failed" ? "error" as const : "warning" as const;

function failureSummary(run: CollectionRun) {
  const errors = [run.error];
  if (run.sourceFailureDetails) {
    try {
      for (const item of JSON.parse(run.sourceFailureDetails) as { source?: string; error?: string }[])
        errors.push(`${item.source ?? "Source"}: ${item.error ?? "Unknown error"}`);
    } catch { errors.push(run.sourceFailureDetails); }
  }
  return errors.filter((value): value is string => !!value?.trim()).join("\n");
}

export function CollectionRunHistory() {
  const [runs, setRuns] = useState<CollectionRun[] | null>(null);
  const [loadError, setLoadError] = useState(false);
  const [status, setStatus] = useState<"all" | CollectionRun["status"]>("all");

  const reload = useCallback(async () => {
    setLoadError(false);
    try {
      const response = await apiFetch(`${apiBase}/api/collector/runs`);
      if (!response.ok) throw new Error("Unable to load collection runs");
      setRuns(await response.json());
    } catch { setLoadError(true); }
  }, []);

  useEffect(() => { reload(); }, [reload]);
  const filtered = useMemo(() => (runs ?? []).filter(run => status === "all" || run.status === status), [runs, status]);

  return (
    <>
    <Card title="Collection Runs" badge={<Badge label={`${filtered.length} shown`} tone="neutral"/>}
      action={<div data-inline-style="inline-f8df590e45">
        <select className="filter-sel" value={status} onChange={e => setStatus(e.target.value as typeof status)} aria-label="Filter collection runs by status">
          <option value="all">All statuses</option>
          <option value="Completed">Completed</option>
          <option value="Failed">Failed</option>
          <option value="Started">In progress</option>
        </select>
        <button className="btn-export" onClick={reload} aria-label="Refresh collection runs"><RefreshCw size={13}/> Refresh</button>
      </div>}>
      {loadError ? <EmptyState icon={<AlertTriangle size={24}/>} message="Could not load collection-run history"/>
        : !runs ? <EmptyState message="Loading collection-run history…"/>
        : filtered.length === 0 ? <EmptyState icon={<Database size={24}/>} message="No collection runs match this filter"/>
        : <div className="table-wrap collection-run-table"><table>
          <thead><tr><th scope="col">Started</th><th scope="col">Status</th><th scope="col">Duration</th><th scope="col">Alerts</th><th scope="col">Sources</th><th scope="col">Failure details</th></tr></thead>
          <tbody>{filtered.map(run => {
            const duration = run.completedAt ? Math.max(0, new Date(run.completedAt).getTime() - new Date(run.startedAt).getTime()) : null;
            const errors = failureSummary(run);
            return <tr key={run.id}>
              <td title={fmtDate(run.startedAt)}>{relTime(run.startedAt)}</td>
              <td><Badge label={run.status === "Started" ? "In progress" : run.status} tone={statusTone(run.status)}/></td>
              <td>{duration === null ? "Running" : `${(duration / 1000).toFixed(1)}s`}</td>
              <td>{run.alertsUpserted}</td>
              <td>{run.sourceFailures === 0 ? "All sources" : `${run.sourceFailures} failed`}</td>
              <td>{errors ? <details className="collection-failure-details"><summary>{run.sourceFailures > 0 ? "View errors" : "Run error"}</summary><pre>{errors}</pre><CopyButton value={errors} label="Collection failure details" size={12}/></details> : "—"}</td>
            </tr>;
          })}</tbody>
        </table></div>}
    </Card>
    <PermissionsReference refreshKey={runs?.length ?? 0}/>
    </>
  );
}
