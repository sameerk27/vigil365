import React, { useCallback, useEffect, useMemo, useState } from "react";
import { AlertTriangle } from "lucide-react";
import { useAuth, setSelectedTenantId } from "../services/api";
import { showToast } from "../services/toast";
import { Card, Badge, EmptyState, LoadingSkeleton, InlineError } from "./SharedComponents";
import { fmtDate, relTime } from "../services/utils";
import { queueApi, type QueueItem } from "../services/tenants";
import type { Tone } from "../services/types";

const sevTone = (s: string): Tone => {
  const v = s.toLowerCase();
  return v === "critical" || v === "high" ? "error" : v === "medium" ? "warning" : "neutral";
};

/**
 * Open alerts across every client the user may see, most severe first
 * (MSP_V12_PLAN.md U6). The MSP's triage list: no switching client by client.
 * Acknowledge/resolve act in the alert's own client (decision Q5: in place);
 * the message names that client so nobody acts on the wrong one. Opening an
 * alert switches the dashboard to its client.
 */
export function ClientAlertQueue() {
  const { canMutate } = useAuth();
  const [items, setItems] = useState<QueueItem[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [client, setClient] = useState<string>("");
  const [busy, setBusy] = useState<string | null>(null);

  const load = useCallback(async () => {
    const r = await queueApi.list();
    if (r.ok) {
      setItems(r.value); setError(null);
      // Handling a client's last open alert drops it from the filter options; a
      // filter left on it would hide every other client's alerts, with no way back.
      setClient(c => r.value.some(i => i.tenantName === c) ? c : "");
    } else { setItems([]); setError(r.error); }
  }, []);
  useEffect(() => { load(); }, [load]);

  const clients = useMemo(() => [...new Set((items ?? []).map(i => i.tenantName))].sort(), [items]);
  const shown = useMemo(() => (items ?? []).filter(i => !client || i.tenantName === client), [items, client]);

  const act = async (item: QueueItem, action: "acknowledge" | "resolve") => {
    setBusy(item.id);
    const r = await queueApi.act(item, action);
    setBusy(null);
    // Toast names the alert's client explicitly — it may not be the selected one.
    if (r.ok) showToast(`${item.policyName} ${action === "resolve" ? "resolved" : "acknowledged"}`, "success", undefined, { client: item.tenantName });
    else showToast(r.error, "error", undefined, { client: item.tenantName });
    await load();
  };

  // A triggered (policy) alert opens on Rules & Notifications, the route
  // notification links use; the Alert Queue lists collected M365 alerts only.
  const open = (item: QueueItem) => {
    setSelectedTenantId(item.tenantId);
    window.location.hash = `#/alertcenter?alert=${item.id}`;
    window.location.reload();
  };

  return (
    <Card title="Open alerts across clients"
      badge={<Badge label={items ? `${shown.length}${client ? ` of ${items.length}` : ""}` : "—"} tone={shown.some(i => sevTone(i.severity) === "error") ? "error" : "neutral"} />}
      action={clients.length > 1 ? (
        <select className="filter-sel" aria-label="Filter by client" value={client} onChange={e => setClient(e.target.value)}>
          <option value="">All clients</option>
          {clients.map(c => <option key={c} value={c}>{c}</option>)}
        </select>
      ) : undefined}>
      {items === null ? <LoadingSkeleton type="table" />
        : error ? <InlineError title="Couldn't load the cross-client queue" message={error} onRetry={load} />
        : shown.length === 0 ? <EmptyState icon={<AlertTriangle size={24} />} message="No open alerts across your clients." />
        : (
          <div className="tbl-wrap">
            <table className="data-tbl queue-tbl">
              <thead>
                <tr><th scope="col">Client</th><th scope="col">Severity</th><th scope="col">Alert</th><th scope="col">Triggered</th><th scope="col">Status</th>{canMutate && <th scope="col">Actions</th>}</tr>
              </thead>
              <tbody>
                {shown.map(i => (
                  <tr key={i.id}>
                    <td><strong>{i.tenantName}</strong></td>
                    <td><Badge label={i.severity} tone={sevTone(i.severity)} /></td>
                    <td>
                      <button className="card-link-btn" onClick={() => open(i)} title={`Open in ${i.tenantName}`}>{i.policyName}</button>
                      <div className="al-date">{i.condition}</div>
                    </td>
                    <td className="al-date" title={fmtDate(i.triggeredAt)}>{relTime(i.triggeredAt) || fmtDate(i.triggeredAt)}</td>
                    <td><Badge label={i.status} tone={i.status === "new" ? "warning" : "neutral"} /></td>
                    {canMutate && (
                      <td className="client-actions">
                        {i.status === "new" && <button className="btn-ack" disabled={busy === i.id} onClick={() => act(i, "acknowledge")}>Acknowledge</button>}
                        <button className="btn-resolve" disabled={busy === i.id} onClick={() => act(i, "resolve")}>Resolve</button>
                      </td>
                    )}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
    </Card>
  );
}
