import React, { useCallback, useEffect, useState } from "react";
import { KeyRound } from "lucide-react";
import { apiTokenApi, isMspMode } from "../services/api";
import { showInstallToast } from "../services/toast";
import { confirmAction } from "../services/confirm";
import { Card, Badge, EmptyState, LoadingSkeleton, CopyButton, InlineError } from "./SharedComponents";
import { fmtDate, relTime } from "../services/utils";
import { tenantApi, type ClientTenant } from "../services/tenants";
import type { ApiTokenInfo } from "../services/types";

/** yyyy-mm-dd of the user's local day: what a date input shows and expects. */
const localDay = (d: Date) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;

const SCOPES = [
  { value: "alerts:read", label: "Read alerts (/api/siem/alerts)" },
  { value: "health:read", label: "Read health (/api/siem/health)" },
];

/**
 * Machine API tokens for SIEM ingestion (Admin). The backend has supported
 * these since the M4 release but no screen existed, so they could only be made
 * through the API. In MSP mode a token can be restricted to one client — such
 * a token can never read another client's data (MSP_V12_PLAN.md U7).
 */
export function ApiTokensCard() {
  const [tokens, setTokens] = useState<ApiTokenInfo[] | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [clients, setClients] = useState<ClientTenant[]>([]);
  const [showNew, setShowNew] = useState(false);
  const [name, setName] = useState("SIEM integration");
  const [scopes, setScopes] = useState<string[]>(["alerts:read", "health:read"]);
  const [expires, setExpires] = useState("");
  const [client, setClient] = useState("");
  const [created, setCreated] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const msp = isMspMode();

  const load = useCallback(async () => {
    // A failed read must never read as "No API tokens yet": an Admin looking for
    // a leaked token to revoke would be told there is none.
    setLoadError(null);
    try { setTokens(await apiTokenApi.list()); }
    catch (e) { setLoadError(e instanceof Error ? e.message : "Request failed"); }
    if (msp) { const r = await tenantApi.list(); setClients(r.ok ? r.value.filter(t => t.isActive) : []); }
  }, [msp]);
  useEffect(() => { load(); }, [load]);

  const clientName = (id?: string | null) => id ? (clients.find(c => c.id === id)?.name ?? "one client") : "All clients";

  const create = async () => {
    if (!name.trim() || scopes.length === 0) { showInstallToast("Give the token a name and at least one scope", "error"); return; }
    // The token lasts to the end of the chosen day where the admin is, not UTC
    // midnight. A day already over would issue a token that is born expired.
    const expiresAt = expires ? new Date(`${expires}T23:59:59`) : null;
    if (expiresAt && expiresAt <= new Date()) { showInstallToast("Pick an expiry date from today onwards", "error"); return; }
    setBusy(true);
    const r = await apiTokenApi.create({
      name: name.trim(), scopes: scopes.join(","),
      expiresAt: expiresAt ? expiresAt.toISOString() : null,
      tenantId: msp && client ? client : null,
    });
    setBusy(false);
    if (!r) { showInstallToast("Could not create the token", "error"); return; }
    setCreated(r.token);
    setShowNew(false);
    await load();
  };

  const revoke = async (t: ApiTokenInfo) => {
    const ok = await confirmAction({
      title: `Revoke "${t.name}"?`,
      message: "Anything using this token stops getting data immediately. This cannot be undone — create a new token instead.",
      confirmLabel: "Revoke token", danger: true,
    });
    if (!ok) return;
    if (await apiTokenApi.revoke(t.id)) { showInstallToast(`Revoked ${t.name}`); await load(); }
    else showInstallToast("Could not revoke the token", "error");
  };

  return (
    <Card title="API tokens (SIEM)"
      badge={<Badge label={tokens ? `${tokens.filter(t => !t.revokedAt).length} active` : "—"} tone="neutral" />}
      action={<button className="btn-apply" onClick={() => { setShowNew(s => !s); setCreated(null); }}><KeyRound size={13} /> New token</button>}>
      {created && (
        <div className="token-created" role="status">
          <strong>Copy this token now — it is shown only once.</strong>
          <div className="ob-consent"><code className="ob-url">{created}</code><CopyButton value={created} label="Copy token" /></div>
        </div>
      )}
      {showNew && (
        <div className="ob-fields token-form">
          <input className="form-input" aria-label="Token name" value={name} onChange={e => setName(e.target.value)} />
          {SCOPES.map(s => (
            <label key={s.value} className="ob-check">
              <input type="checkbox" checked={scopes.includes(s.value)}
                onChange={e => setScopes(v => e.target.checked ? [...v, s.value] : v.filter(x => x !== s.value))} /> {s.label}
            </label>
          ))}
          <label className="ob-check">Expires (optional) <input className="form-input" type="date" aria-label="Expiry date" min={localDay(new Date())} value={expires} onChange={e => setExpires(e.target.value)} /></label>
          {msp && (
            <label className="ob-check">Client
              <select className="filter-sel" aria-label="Restrict to client" value={client} onChange={e => setClient(e.target.value)}>
                <option value="">All clients (caller must choose one per request)</option>
                {clients.map(c => <option key={c.id} value={c.id}>Only {c.name}</option>)}
              </select>
            </label>
          )}
          <div className="ob-actions"><button className="btn-apply" disabled={busy} onClick={create}>{busy ? "Creating…" : "Create token"}</button></div>
        </div>
      )}
      {loadError ? (
        <InlineError title="Couldn't load API tokens" onRetry={load}
          message={`${loadError}. Tokens may still exist and be in use — this does not mean there are none.`} />
      ) : tokens === null ? <LoadingSkeleton type="table" /> : tokens.length === 0 ? <EmptyState message="No API tokens yet." /> : (
        <div className="tbl-wrap">
          <table className="data-tbl">
            <thead>
              <tr><th scope="col">Name</th><th scope="col">Token</th><th scope="col">Scopes</th>{msp && <th scope="col">Client</th>}<th scope="col">Last used</th><th scope="col">Expires</th><th scope="col">Status</th><th scope="col">Actions</th></tr>
            </thead>
            <tbody>
              {tokens.map(t => (
                <tr key={t.id} className={t.revokedAt ? "row-inactive" : ""}>
                  <td>{t.name}</td>
                  <td className="mono al-date">{t.prefix}…</td>
                  <td className="al-date">{t.scopes}</td>
                  {msp && <td className="al-date">{clientName(t.tenantId)}</td>}
                  <td className="al-date">{t.lastUsedAt ? (relTime(t.lastUsedAt) || fmtDate(t.lastUsedAt)) : "Never"}</td>
                  <td className="al-date">{t.expiresAt ? fmtDate(t.expiresAt) : "Never"}</td>
                  <td><Badge label={t.revokedAt ? "Revoked" : t.expiresAt && new Date(t.expiresAt) < new Date() ? "Expired" : "Active"}
                    tone={t.revokedAt ? "neutral" : t.expiresAt && new Date(t.expiresAt) < new Date() ? "warning" : "good"} /></td>
                  <td>{!t.revokedAt && <button className="btn-danger" onClick={() => revoke(t)}>Revoke</button>}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Card>
  );
}
