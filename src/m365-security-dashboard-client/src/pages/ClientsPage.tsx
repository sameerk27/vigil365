import React, { useCallback, useEffect, useMemo, useState } from "react";
import { Building2, Plus, Plug, Link as LinkIcon, PlayCircle, Power, Trash2, ShieldCheck, AlertTriangle, Info } from "lucide-react";
import { useAuth, getSelectedTenantId, setSelectedTenantId } from "../services/api";
import { showToast } from "../services/toast";
import { confirmAction } from "../services/confirm";
import { Card, Badge, EmptyState, LoadingSkeleton, StatBox, CopyButton } from "../components/SharedComponents";
import { fmtDate, relTime } from "../services/utils";
import { tenantApi, setupApi, collectionTone, type ClientTenant, type TenantRollupRow } from "../services/tenants";
import type { Tone } from "../services/types";

/**
 * Clients: the MSP roster and cross-tenant rollup, plus onboarding.
 *
 * Rollup cards sort worst-posture-first — this is the morning triage view.
 * Every number is real: open triggered alerts by severity and unresolved
 * critical/high security alerts per client, from the server's cross-tenant
 * read. The roster is where an Admin adds a client, stores its credentials,
 * hands the admin-consent link to the client's Global Administrator, and
 * proves the connection. Non-admins see the clients assigned to them.
 */
export function ClientsPage() {
  const { isAdmin } = useAuth();
  const [rollup, setRollup] = useState<TenantRollupRow[] | null>(null);
  const [tenants, setTenants] = useState<ClientTenant[] | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [onboarding, setOnboarding] = useState<ClientTenant | "new" | null>(null);

  const load = useCallback(async () => {
    const [r, t] = await Promise.all([tenantApi.rollup(), isAdmin ? tenantApi.list() : Promise.resolve(null)]);
    setRollup(r.ok ? r.value : []);
    if (t) setTenants(t.ok ? t.value : []);
  }, [isAdmin]);

  useEffect(() => { load(); }, [load]);

  const sorted = useMemo(() => rollup ? [...rollup].sort(byWorstFirst) : null, [rollup]);
  const totals = useMemo(() => sorted?.reduce((acc, r) => ({
    critical: acc.critical + r.open.critical, high: acc.high + r.open.high,
    total: acc.total + r.open.total, notConnected: acc.notConnected + (r.configured ? 0 : 1),
  }), { critical: 0, high: 0, total: 0, notConnected: 0 }), [sorted]);

  const focus = (id: string) => {
    if (getSelectedTenantId() === id) return;
    setSelectedTenantId(id);
    window.location.reload();
  };

  const test = async (t: ClientTenant) => {
    setBusy(t.id);
    const r = await tenantApi.test(t.id);
    setBusy(null);
    if (r.ok) showToast(`Connected to ${r.value.displayName ?? r.value.microsoftTenantId ?? t.name}`);
    else showToast(r.error, "error");
    await load();
  };

  const deactivate = async (t: ClientTenant) => {
    const ok = await confirmAction({
      title: `Deactivate ${t.name}?`,
      message: "Collection stops and the client disappears from the switcher. Its data is kept and it can be re-activated later.",
      confirmLabel: "Deactivate", danger: true,
    });
    if (!ok) return;
    setBusy(t.id);
    const r = await tenantApi.remove(t.id, false);
    setBusy(null);
    if (r.ok) { showToast(`${t.name} deactivated`); if (getSelectedTenantId() === t.id) setSelectedTenantId(null); }
    else showToast(r.error, "error");
    await load();
  };

  const purge = async (t: ClientTenant) => {
    const ok = await confirmAction({
      title: `Permanently delete ${t.name} and all its data?`,
      message: "Every alert, run, snapshot, note and audit event belonging to this client is deleted. This is the offboarding action and cannot be undone.",
      confirmLabel: "Delete everything", danger: true,
    });
    if (!ok) return;
    setBusy(t.id);
    const r = await tenantApi.remove(t.id, true);
    setBusy(null);
    if (r.ok) { showToast(`${t.name} removed`); if (getSelectedTenantId() === t.id) setSelectedTenantId(null); }
    else showToast(r.error, "error");
    await load();
  };

  const reactivate = async (t: ClientTenant) => {
    setBusy(t.id);
    const r = await tenantApi.update(t.id, { name: t.name, microsoftTenantId: t.microsoftTenantId, notes: t.notes, isActive: true });
    setBusy(null);
    if (r.ok) showToast(`${t.name} re-activated`); else showToast(r.error, "error");
    await load();
  };

  return (
    <div className="page">
      <div className="page-intro">
        <Building2 size={16} aria-hidden="true" />
        <span>
          Every client tenant Vigil365 watches, worst posture first. Pick a client to scope the rest of the dashboard to it.
          {isAdmin && " To onboard a client: add it, then sign in as its Global Administrator to consent — Vigil365 connects and tests in one go."}
        </span>
      </div>

      {sorted === null ? <LoadingSkeleton type="kpi" /> : sorted.length === 0 ? (
        <EmptyState icon={<Building2 size={28} />} message={isAdmin ? "No clients yet. Add the first one below." : "No clients have been assigned to you yet. Ask an Admin."} />
      ) : (
        <>
          <div className="kpi-row">
            <StatBox value={sorted.length} label="Clients" />
            <StatBox value={totals!.critical} label="Open critical" color={totals!.critical ? "var(--error)" : undefined} />
            <StatBox value={totals!.high} label="Open high" color={totals!.high ? "var(--warning)" : undefined} />
            <StatBox value={totals!.total} label="Open alerts, all clients" />
            <StatBox value={totals!.notConnected} label="Not connected" color={totals!.notConnected ? "var(--warning)" : undefined} />
          </div>
          <div className="rollup-grid">
            {sorted.map(r => <RollupCard key={r.id} row={r} selected={getSelectedTenantId() === r.id} onFocus={() => focus(r.id)} />)}
          </div>
        </>
      )}

      {isAdmin && (
        <Card title="Client roster"
          badge={<Badge label={tenants ? `${tenants.filter(t => t.isActive).length} active` : "—"} tone="neutral" />}
          action={<button className="btn-apply" onClick={() => setOnboarding("new")}><Plus size={13} /> Add client</button>}>
          {tenants === null ? <LoadingSkeleton type="table" /> : tenants.length === 0 ? <EmptyState message="No clients yet." /> : (
            <div className="tbl-wrap">
              <table className="data-tbl">
                <thead>
                  <tr>
                    <th scope="col">Client</th><th scope="col">Entra tenant</th><th scope="col">Credentials</th>
                    <th scope="col">Consent</th><th scope="col">Last collection</th><th scope="col">Actions</th>
                  </tr>
                </thead>
                <tbody>
                  {tenants.map(t => (
                    <tr key={t.id} className={t.isActive ? "" : "row-inactive"}>
                      <td>
                        <strong>{t.name}</strong>
                        {!t.isActive && <Badge label="Inactive" tone="neutral" />}
                        {t.notes && <div className="al-date">{t.notes}</div>}
                      </td>
                      <td className="mono al-date">{t.microsoftTenantId ?? "—"}</td>
                      <td>
                        <Badge label={t.credentialSource === "tenant" ? (t.authMode === "certificate" ? "Own app · cert" : "Own app · secret") : t.credentialSource === "install" ? "Install-wide" : "None"}
                          tone={t.credentialSource === "none" ? "warning" : "good"} />
                      </td>
                      <td className="al-date" title={t.consentGrantedAt ? fmtDate(t.consentGrantedAt) : "Not verified"}>
                        {t.consentGrantedAt ? <><ShieldCheck size={13} /> {relTime(t.consentGrantedAt) || fmtDate(t.consentGrantedAt)}</> : "Not verified"}
                      </td>
                      <td>
                        <Badge label={t.lastCollectionStatus ?? (t.configured ? "Pending" : "Not connected")} tone={collectionTone(t)} />
                        {t.lastError && <div className="al-date client-error" title={t.lastError}><AlertTriangle size={12} /> {truncate(t.lastError, 80)}</div>}
                        {t.nextCollectionAfter && new Date(t.nextCollectionAfter) > new Date() && (
                          <div className="al-date" title={`${t.consecutiveFailures} consecutive failure(s)`}>Backing off until {fmtDate(t.nextCollectionAfter)}</div>
                        )}
                      </td>
                      <td className="client-actions">
                        <button className="btn-export" disabled={busy === t.id} onClick={() => setOnboarding(t)} title="Credentials and consent"><Plug size={13} /> Connect</button>
                        <button className="btn-export" disabled={busy === t.id || !t.isActive} onClick={() => test(t)} title="Call Graph as this client"><PlayCircle size={13} /> Test</button>
                        {t.isActive
                          ? <button className="btn-export" disabled={busy === t.id} onClick={() => deactivate(t)} title="Stop collecting, keep data"><Power size={13} /> Deactivate</button>
                          : <button className="btn-export" disabled={busy === t.id} onClick={() => reactivate(t)}><Power size={13} /> Re-activate</button>}
                        <button className="btn-danger" disabled={busy === t.id} onClick={() => purge(t)} title="Delete the client and every row it owns"><Trash2 size={13} /></button>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </Card>
      )}

      {onboarding && (
        <OnboardingDialog tenant={onboarding === "new" ? null : onboarding}
          onClose={() => setOnboarding(null)}
          onChanged={async () => { await load(); }} />
      )}
    </div>
  );
}

// ─── Rollup ───────────────────────────────────────────────────────────────────

function byWorstFirst(a: TenantRollupRow, b: TenantRollupRow): number {
  const rank = (r: TenantRollupRow) => r.health === "error" ? 3 : r.health === "warning" ? 2 : r.health === "neutral" ? 1 : 0;
  return (rank(b) - rank(a))
    || (b.open.critical - a.open.critical)
    || (b.open.high - a.open.high)
    || (b.open.total - a.open.total)
    || a.name.localeCompare(b.name);
}

function RollupCard({ row, selected, onFocus }: { row: TenantRollupRow; selected: boolean; onFocus: () => void }) {
  const tone: Tone = row.health;
  return (
    <button className={`rollup-card tone-${tone}${selected ? " rollup-selected" : ""}`} onClick={onFocus}
      title={selected ? "Currently selected" : `Scope the dashboard to ${row.name}`}>
      <div className="rollup-head">
        <span className="rollup-name">{row.name}</span>
        <Badge label={!row.configured ? "Not connected" : row.lastCollectionStatus ?? "Pending"} tone={tone} />
      </div>
      <div className="rollup-sev">
        <span className="sev sev-critical" title="Open critical">{row.open.critical}</span>
        <span className="sev sev-high" title="Open high">{row.open.high}</span>
        <span className="sev sev-medium" title="Open medium">{row.open.medium}</span>
        <span className="sev sev-low" title="Open low">{row.open.low}</span>
      </div>
      <div className="rollup-foot al-date">
        {row.unresolvedAlerts.critical + row.unresolvedAlerts.high > 0
          ? `${row.unresolvedAlerts.critical} critical · ${row.unresolvedAlerts.high} high security alerts unresolved`
          : "No unresolved critical/high security alerts"}
        <br />
        {row.lastCollectionAt ? `Collected ${relTime(row.lastCollectionAt) || fmtDate(row.lastCollectionAt)}` : "Never collected"}
        {row.lastError && <span className="client-error"> · {truncate(row.lastError, 60)}</span>}
      </div>
    </button>
  );
}

// ─── Onboarding dialog ────────────────────────────────────────────────────────

function OnboardingDialog({ tenant, onClose, onChanged }: { tenant: ClientTenant | null; onClose: () => void; onChanged: () => Promise<void> }) {
  const [saved, setSaved] = useState<ClientTenant | null>(tenant);
  const [name, setName] = useState(tenant?.name ?? "");
  const [entra, setEntra] = useState(tenant?.microsoftTenantId ?? "");
  const [notes, setNotes] = useState(tenant?.notes ?? "");
  const [clientId, setClientId] = useState(tenant?.clientId ?? "");
  const [secret, setSecret] = useState("");
  const [useCert, setUseCert] = useState(tenant?.authMode === "certificate");
  const [thumbprint, setThumbprint] = useState(tenant?.certificateThumbprint ?? "");
  const [certPath, setCertPath] = useState("");
  const [certPassword, setCertPassword] = useState("");
  const [brandName, setBrandName] = useState(tenant?.brandName ?? "");
  const [brandColor, setBrandColor] = useState(tenant?.brandAccentColor ?? "");
  const [sovereign, setSovereign] = useState(false);
  const [loginInstance, setLoginInstance] = useState("");
  const [baseUrl, setBaseUrl] = useState("");
  const [redirect, setRedirect] = useState(() => `${window.location.origin}/consented`);
  const [consentUrl, setConsentUrl] = useState<string | null>(null);
  const [testResult, setTestResult] = useState<{ ok: boolean; text: string } | null>(null);
  const [busy, setBusy] = useState<string | null>(null);

  const step1 = async () => {
    if (!name.trim()) { showToast("Name is required", "error"); return; }
    setBusy("save");
    const body = { name: name.trim(), microsoftTenantId: entra.trim() || null, notes: notes.trim() || null, brandName: brandName.trim() || null, brandAccentColor: brandColor.trim() || null };
    const r = saved ? await tenantApi.update(saved.id, body) : await tenantApi.create(body);
    setBusy(null);
    if (!r.ok) { showToast(r.error, "error"); return; }
    const id = saved ? saved.id : (r.value as { id: string }).id;
    const list = await tenantApi.list();
    const fresh = list.ok ? list.value.find(t => t.id === id) ?? null : null;
    setSaved(fresh);
    showToast(saved ? "Client updated" : "Client added");
    await onChanged();
  };

  const step2 = async () => {
    if (!saved) return;
    if (!clientId.trim()) { showToast("Client ID is required", "error"); return; }
    const certGiven = useCert && (thumbprint.trim() || certPath.trim());
    if (!saved.hasOwnCredentials && !secret && !certGiven) { showToast("A client secret or a certificate is required the first time", "error"); return; }
    setBusy("creds");
    const r = await tenantApi.setCredentials(saved.id, {
      clientId: clientId.trim(), clientSecret: !useCert && secret ? secret : undefined,
      loginInstance: sovereign ? loginInstance.trim() || null : null, baseUrl: sovereign ? baseUrl.trim() || null : null,
      certificateThumbprint: useCert ? thumbprint.trim() || null : null,
      certificatePath: useCert ? certPath.trim() || null : null,
      certificatePassword: useCert && certPassword ? certPassword : null,
    });
    setBusy(null);
    if (!r.ok) { showToast(r.error, "error"); return; }
    setSecret("");
    showToast("Credentials stored");
    const list = await tenantApi.list();
    if (list.ok) setSaved(list.value.find(t => t.id === saved.id) ?? saved);
    await onChanged();
  };

  // Register the shared multi-tenant MSP app (Azure CLI, server-side) so per-client
  // credentials become optional. Fills nothing in the form — it configures the
  // install-wide app the consent flow uses.
  const registerMspApp = async () => {
    setBusy("mspapp");
    const r = await setupApi.registerMspApp({ redirectUri: redirect.trim() || undefined });
    setBusy(null);
    if (r.ok) showToast(`MSP app registered (client ${r.value.clientId.slice(0, 8)}…). Clients can now consent without their own credentials.`);
    else showToast(r.error, "error");
  };

  // The one-go flow: open Microsoft admin consent in a popup, let the client's
  // Global Administrator sign in and approve, then poll until the server records
  // consent (the /consented landing page does that) and auto-run the test.
  const connect = async () => {
    if (!saved) return;
    setBusy("consent");
    setTestResult(null);
    const r = await tenantApi.consentUrl(saved.id); // server default → its own /consented
    if (!r.ok) { setBusy(null); showToast(r.error, "error"); return; }

    const popup = window.open(r.value.url, "vigil365-consent", "width=620,height=800");
    if (!popup) {
      // Popup blocked — fall back to a copyable link the admin can open manually.
      setBusy(null); setConsentUrl(r.value.url);
      showToast("Allow popups to sign in here, or copy the consent link below.", "error");
      return;
    }

    const deadline = Date.now() + 5 * 60 * 1000;
    const poll = window.setInterval(async () => {
      const g = await tenantApi.get(saved.id);
      const granted = g.ok && !!g.value.consentGrantedAt;
      const timedOut = Date.now() > deadline;
      if (!granted && !popup.closed && !timedOut) return;

      window.clearInterval(poll);
      try { if (!popup.closed) popup.close(); } catch { /* cross-origin close race */ }
      if (granted) {
        if (g.ok) setSaved(g.value);
        await runTest();                 // verify + record the Entra tenant id
      } else {
        setBusy(null);
        if (timedOut) showToast("Timed out waiting for consent. Try again, or use the copy-link option.", "error");
        // popup closed without consenting: leave the step ready to retry, no error noise
      }
    }, 2500);
  };

  const runTest = async () => {
    if (!saved) return;
    setBusy("test");
    const r = await tenantApi.test(saved.id);
    setBusy(null);
    setTestResult(r.ok
      ? { ok: true, text: `Connected to ${r.value.displayName ?? r.value.microsoftTenantId ?? saved.name}.` }
      : { ok: false, text: r.error });
    if (r.ok) { const g = await tenantApi.get(saved.id); if (g.ok) setSaved(g.value); }
    await onChanged();
  };

  return (
    <div className="detail-modal-backdrop" onClick={onClose}>
      <div className="detail-modal onboarding" role="dialog" aria-modal="true" aria-label={saved ? `Connect ${saved.name}` : "Add client"} onClick={e => e.stopPropagation()}>
        <div className="detail-modal-hdr">
          <div>
            <h2>{saved ? saved.name : "Add client"}</h2>
            <div className="al-date">{saved ? "Connect: sign in as the client's Global Administrator" : "Step 1 of 3 — the client"}</div>
          </div>
          <button className="modal-close" onClick={onClose} aria-label="Close">×</button>
        </div>
        <div className="detail-modal-body onboarding-body">

          <section className="ob-step">
            <h3><span className="ob-num">1</span> Client</h3>
            <div className="ob-fields">
              <input className="form-input" placeholder="Display name (e.g. Contoso Ltd)" value={name} onChange={e => setName(e.target.value)} />
              <input className="form-input mono" placeholder="Entra tenant id (optional — filled in by the test)" value={entra} onChange={e => setEntra(e.target.value)} />
              <input className="form-input" placeholder="Notes (optional)" value={notes} onChange={e => setNotes(e.target.value)} />
              <div className="ob-actions">
                <input className="form-input" placeholder="Report brand name (optional, e.g. Contoso Security)" value={brandName} onChange={e => setBrandName(e.target.value)} title="Shown instead of Vigil365 on this client's digest emails and PDFs" />
                <input className="form-input ob-color" placeholder="#1d4ed8" value={brandColor} onChange={e => setBrandColor(e.target.value)} title="Accent colour on this client's reports (hex)" />
              </div>
              <button className="btn-apply" disabled={busy === "save"} onClick={step1}>{busy === "save" ? "Saving…" : saved ? "Save" : "Add client"}</button>
            </div>
          </section>

          <section className={`ob-step${saved ? "" : " ob-disabled"}`}>
            <h3><span className="ob-num">2</span> Credentials <span className="ob-optional">(optional)</span></h3>
            <p className="al-date">
              Most clients need nothing here: they consent to your <strong>shared multi-tenant MSP app</strong> in step 3.
              Fill this in only to give one client its own app registration.
              {saved?.credentialSource === "install" && " This client currently uses the shared MSP app; storing its own overrides that."}
            </p>
            <div className="ob-actions ob-mspapp">
              <button className="btn-export" disabled={busy === "mspapp"} onClick={registerMspApp}
                title="Create the shared multi-tenant app registration in your own tenant via Azure CLI, and store it install-wide">
                <ShieldCheck size={13} /> {busy === "mspapp" ? "Registering…" : "Register the shared MSP app"}
              </button>
              <span className="al-date">Runs Azure CLI on the server as your signed-in az session — needed once.</span>
            </div>
            <div className="ob-fields">
              <input className="form-input mono" placeholder="Application (client) ID" value={clientId} onChange={e => setClientId(e.target.value)} disabled={!saved} />
              <label className="ob-check"><input type="checkbox" checked={useCert} onChange={e => setUseCert(e.target.checked)} disabled={!saved} /> Certificate instead of a secret (recommended — nothing long-lived to rotate)</label>
              {!useCert && (
                <input className="form-input" type="password" autoComplete="off"
                  placeholder={saved?.hasOwnCredentials ? "Client secret (leave blank to keep the stored one)" : "Client secret"}
                  value={secret} onChange={e => setSecret(e.target.value)} disabled={!saved} />
              )}
              {useCert && (
                <>
                  <input className="form-input mono" placeholder="Certificate thumbprint (in the server's certificate store)" value={thumbprint} onChange={e => setThumbprint(e.target.value)} disabled={!saved} />
                  <input className="form-input" placeholder="…or PFX path on the server (e.g. /certs/contoso.pfx)" value={certPath} onChange={e => setCertPath(e.target.value)} disabled={!saved} />
                  <input className="form-input" type="password" autoComplete="off" placeholder="PFX password (if any)" value={certPassword} onChange={e => setCertPassword(e.target.value)} disabled={!saved} />
                </>
              )}
              <label className="ob-check"><input type="checkbox" checked={sovereign} onChange={e => setSovereign(e.target.checked)} disabled={!saved} /> Sovereign cloud (GCC High / DoD / China)</label>
              {sovereign && (
                <>
                  <input className="form-input" placeholder="Login authority, e.g. https://login.microsoftonline.us" value={loginInstance} onChange={e => setLoginInstance(e.target.value)} />
                  <input className="form-input" placeholder="Graph base URL, e.g. https://graph.microsoft.us" value={baseUrl} onChange={e => setBaseUrl(e.target.value)} />
                </>
              )}
              <div className="ob-actions">
                <button className="btn-apply" disabled={!saved || busy === "creds"} onClick={step2}>{busy === "creds" ? "Storing…" : "Store credentials"}</button>
                {saved?.hasOwnCredentials && (
                  <button className="btn-export" disabled={busy !== null} onClick={async () => {
                    const r = await tenantApi.clearCredentials(saved.id);
                    if (r.ok) { showToast("Credentials cleared"); const l = await tenantApi.list(); if (l.ok) setSaved(l.value.find(t => t.id === saved.id) ?? saved); await onChanged(); }
                    else showToast(r.error, "error");
                  }}>Clear</button>
                )}
              </div>
            </div>
          </section>

          <section className={`ob-step${saved ? "" : " ob-disabled"}`}>
            <h3><span className="ob-num">3</span> Sign in &amp; consent</h3>
            <p className="al-date">
              Opens Microsoft admin consent in a popup. The client's Global Administrator signs in and approves once —
              that provisions the app in their tenant. Vigil365 then finishes and tests the connection automatically.
            </p>
            <div className="ob-actions">
              <button className="btn-run" disabled={!saved || busy === "consent" || busy === "test"} onClick={connect}>
                <ShieldCheck size={13} /> {busy === "consent" ? "Waiting for sign-in…" : busy === "test" ? "Verifying…" : "Sign in as global admin & consent"}
              </button>
              {testResult && (
                <span className={`ob-result ${testResult.ok ? "ob-ok" : "ob-fail"}`}>
                  {testResult.ok ? <ShieldCheck size={14} /> : <AlertTriangle size={14} />} {testResult.text}
                </span>
              )}
            </div>
            {saved?.consentGrantedAt && !testResult && (
              <p className="al-date"><Info size={12} /> Last connected {relTime(saved.consentGrantedAt) || fmtDate(saved.consentGrantedAt)}.</p>
            )}
            <details className="ob-fallback">
              <summary>Can't sign in here? Send the client a link instead</summary>
              <div className="ob-fields">
                <input className="form-input" placeholder="Redirect URI (leave blank to use this app's /consented page)" value={redirect} onChange={e => setRedirect(e.target.value)} disabled={!saved} />
                <div className="ob-actions">
                  <button className="btn-export" disabled={!saved} onClick={async () => {
                    if (!saved) return;
                    const r = await tenantApi.consentUrl(saved.id, redirect.trim() || undefined);
                    if (r.ok) setConsentUrl(r.value.url); else showToast(r.error, "error");
                  }}><LinkIcon size={13} /> Generate consent link</button>
                  <button className="btn-export" disabled={!saved || busy === "test"} onClick={runTest}><PlayCircle size={13} /> {busy === "test" ? "Testing…" : "Test now"}</button>
                </div>
                {consentUrl && (
                  <div className="ob-consent">
                    <code className="ob-url">{consentUrl}</code>
                    <CopyButton value={consentUrl} label="Copy link" />
                  </div>
                )}
              </div>
            </details>
          </section>
        </div>
      </div>
    </div>
  );
}

function truncate(s: string, n: number) { return s.length <= n ? s : s.slice(0, n - 1) + "…"; }
