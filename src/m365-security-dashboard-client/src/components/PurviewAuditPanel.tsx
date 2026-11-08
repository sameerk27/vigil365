import React, { useEffect, useState } from "react";
import { ShieldAlert, Clock } from "lucide-react";
import { purviewApi, type PurviewAuditStatus } from "../services/purview";
import { relTime, fmtDate } from "../services/utils";

const latest = (dates: (string | null)[]) => {
  const known = dates.filter((d): d is string => !!d).sort();
  return known.length > 0 ? known[known.length - 1] : null;
};

/**
 * The Copilot agent and Power Platform rules run on the Purview audit search, whose
 * permission (AuditLogsQuery.Read.All) is optional. Say whether the search runs and,
 * when it can't, what to grant, so a quiet rule list is never read as "nothing happened".
 */
export function PurviewAuditPanel() {
  const [status, setStatus] = useState<PurviewAuditStatus | null>(null);

  useEffect(() => {
    let cancelled = false;
    purviewApi.status().then(s => { if (!cancelled) setStatus(s); });
    return () => { cancelled = true; };
  }, []);

  if (!status) return null;

  if (!status.enabled) {
    return (
      <div className="purview-meta">
        Copilot agent and Power Platform rules are idle: the Purview audit search is turned off
        (Graph:PurviewAuditIntervalMinutes is 0).
      </div>
    );
  }

  const needsPermission = status.feeds.find(f => f.status === "NeedsPermission");
  const failed = status.feeds.find(f => f.status === "Failed");
  const lastResults = latest(status.feeds.map(f => f.lastSuccessAt));

  return (
    <div className="purview-panel">
      {needsPermission ? (
        <div className="purview-banner" role="alert">
          <ShieldAlert size={16} aria-hidden="true"/>
          <div>
            <strong>Copilot agent and Power Platform rules need one more permission</strong>
            {/* The server's sentence says what to check if the permission is already granted, with Microsoft's own reason when it gave one. */}
            <p>{needsPermission.lastError ?? "Grant AuditLogsQuery.Read.All (read-only) with admin consent."}</p>
            <p>Until then these rules stay idle. In the MSP edition, each client grants it through its consent link.</p>
          </div>
        </div>
      ) : failed ? (
        <div className="purview-banner" role="status">
          <ShieldAlert size={16} aria-hidden="true"/>
          <div>
            <strong>The last Purview audit search didn't complete</strong>
            <p>{failed.lastError ?? "It will be submitted again on the next run."}</p>
          </div>
        </div>
      ) : null}
      <div className="purview-meta">
        <Clock size={13} aria-hidden="true"/>
        <span>
          Purview audit search every {status.intervalMinutes} minutes ·{" "}
          {lastResults
            ? <span title={fmtDate(lastResults)}>last results {relTime(lastResults) || fmtDate(lastResults)}</span>
            : "no results yet"}
        </span>
        {status.highRiskConnectors.length > 0 && (
          <span className="connector-chips" aria-label="High-risk connectors">
            High-risk connectors:
            {status.highRiskConnectors.map(c => <span key={c} className="connector-chip">{c}</span>)}
          </span>
        )}
      </div>
    </div>
  );
}
