import React, { useState, useCallback, useEffect } from "react";
import { CheckCircle, AlertTriangle, Bell, Users, ShieldCheck, ChevronRight, Check, X, HelpCircle } from "lucide-react";
import { apiBase, apiFetch, crossNavigate } from "../services/api";
import { showToast } from "../services/toast";
import { Card, Badge, CopyButton, EmptyState } from "../components/SharedComponents";

export function SetupPage() {
  const [status, setStatus] = useState<{ configured: boolean; tenantId: string; clientId: string; hasSecret: boolean; loginInstance?: string; baseUrl?: string } | null>(null);
  const [permissions, setPermissions] = useState<{ permission: string; features: string[]; status: "missing" | "granted" | "unknown" }[] | null>(null);
  const [tenantId, setTenantId] = useState("");
  const [clientId, setClientId] = useState("");
  const [clientSecret, setClientSecret] = useState("");
  const [loginInstance, setLoginInstance] = useState("");
  const [baseUrl, setBaseUrl] = useState("");
  const [showAdvanced, setShowAdvanced] = useState(false);
  const [saving, setSaving] = useState(false);
  const [result, setResult] = useState<{ ok: boolean; msg: string } | null>(null);
  const [errors, setErrors] = useState<{ tenantId?: string; clientId?: string; clientSecret?: string }>({});

  const loadStatus = useCallback(async () => {
    try {
      const r = await apiFetch(`${apiBase}/api/setup/graph`);
      if (r.ok) {
        const s = await r.json();
        setStatus(s);
        setTenantId(s.tenantId || "");
        setClientId(s.clientId || "");
        setLoginInstance(s.loginInstance || "");
        setBaseUrl(s.baseUrl || "");
        if (s.loginInstance || s.baseUrl) setShowAdvanced(true);
      }
      const p = await apiFetch(`${apiBase}/api/setup/permissions`);
      if (p.ok) {
        const pdata = await p.json();
        setPermissions(pdata.permissions);
      }
    } catch { /* ignore */ }
  }, []);

  useEffect(() => { loadStatus(); }, [loadStatus]);

  const validate = () => {
    const errs: typeof errors = {};
    if (!tenantId.trim()) errs.tenantId = "Tenant ID is required";
    else if (!/^([0-9a-fA-F-]{36}|[a-zA-Z0-9.-]+\.onmicrosoft\.com)$/.test(tenantId.trim())) errs.tenantId = "Must be a valid GUID or .onmicrosoft.com domain";
    
    if (!clientId.trim()) errs.clientId = "Client ID is required";
    else if (!/^[0-9a-fA-F-]{36}$/.test(clientId.trim())) errs.clientId = "Must be a valid GUID";

    if (!status?.hasSecret && !clientSecret.trim()) errs.clientSecret = "Client Secret is required";

    setErrors(errs);
    return Object.keys(errs).length === 0;
  };

  const save = async () => {
    if (!validate()) return;
    setSaving(true); setResult(null);
    try {
      const r = await apiFetch(`${apiBase}/api/setup/graph`, {
        method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ 
          tenantId: tenantId.trim(), 
          clientId: clientId.trim(), 
          clientSecret: clientSecret.trim(),
          loginInstance: loginInstance.trim(),
          baseUrl: baseUrl.trim()
        }),
      });
      const d = await r.json().catch(() => ({}));
      if (!r.ok) { setResult({ ok: false, msg: d.error ?? "Could not save credentials" }); }
      else if (d.testOk) {
        setResult({ ok: true, msg: "Saved and connected to Microsoft Graph successfully." });
        showToast("Graph configured");
        setClientSecret("");
        await loadStatus();
      }
      else {
        setResult({ ok: false, msg: `Saved, but the connection test failed: ${d.testError ?? "unknown error"}` });
        await loadStatus();
      }
    } finally { setSaving(false); }
  };

  const field = (label: string, value: string, set: (v: string) => void, placeholder: string, type = "text", copyable = false, error?: string) => (
    <div className="policy-field">
      <span className="policy-label" data-inline-style="inline-c82cd24e1a">
        {label}
        {copyable && value.trim() && <CopyButton value={value.trim()} label={label} size={12}/>}
      </span>
      <input className={`policy-input ${error ? 'err-input' : ''}`} type={type} value={value} placeholder={placeholder} onChange={e => { set(e.target.value); setErrors(prev => ({ ...prev, [label.includes('tenant') ? 'tenantId' : label.includes('client)') ? 'clientId' : 'clientSecret']: undefined })); }} />
      {error && <div className="field-err" style={{ color: "var(--status-error-text)", fontSize: 12, marginTop: 4 }}>{error}</div>}
    </div>
  );

  return (
    <div className="page">
      <div style={{ display: "flex", marginBottom: 16 }}>
        <button className="btn-export" style={{ padding: "4px 12px" }} onClick={() => crossNavigate({ page: "overview" })}>← Back to Dashboard</button>
      </div>
      <Card title="Microsoft Graph Setup"
        badge={<Badge label={status?.configured ? "Configured" : "Not configured"} tone={status?.configured ? "good" : "warning"}/>}>
        <div data-inline-style="inline-ddbe82fa76">
          Enter the credentials from your Entra app registration. Vigil365 uses these to read your
          tenant's security data via Microsoft Graph (app-only, read-only). The client secret is stored
          encrypted at rest and is never shown again after saving.
        </div>
        <div data-inline-style="inline-33a4af2463">
          {field("Directory (tenant) ID", tenantId, setTenantId, "00000000-0000-0000-0000-000000000000", "text", true, errors.tenantId)}
          {field("Application (client) ID", clientId, setClientId, "00000000-0000-0000-0000-000000000000", "text", true, errors.clientId)}
          {field(status?.hasSecret ? "Client secret (leave blank to keep current)" : "Client secret", clientSecret, setClientSecret, status?.hasSecret ? "••••••••" : "Paste secret value", "password", false, errors.clientSecret)}
          
          <div style={{ marginTop: 16 }}>
            <button type="button" onClick={() => setShowAdvanced(a => !a)} style={{ background: "none", border: "none", color: "var(--color-primary)", cursor: "pointer", fontSize: 13, display: "flex", alignItems: "center", gap: 4 }}>
              {showAdvanced ? <ChevronRight size={14} style={{ transform: "rotate(90deg)" }}/> : <ChevronRight size={14}/>}
              Sovereign Cloud & Advanced Settings
            </button>
            {showAdvanced && (
              <div style={{ marginTop: 12, paddingLeft: 12, borderLeft: "2px solid var(--border-color)", display: "flex", flexDirection: "column", gap: 12 }}>
                {field("Login Instance (Authority)", loginInstance, setLoginInstance, "https://login.microsoftonline.com/", "text")}
                {field("Graph Base URL", baseUrl, setBaseUrl, "https://graph.microsoft.com/", "text")}
              </div>
            )}
          </div>

          <div style={{ marginTop: 24 }}>
            <button className="btn-apply" disabled={saving} onClick={save}>{saving ? "Saving & testing…" : "Save & Test Connection"}</button>
          </div>
          {result && (
            <div style={{ display:"flex", alignItems:"center", gap:8, fontSize:13, padding:"10px 14px", borderRadius:8,
              background: result.ok ? "rgba(34,197,94,0.1)" : "rgba(220,38,38,0.1)",
              color: result.ok ? "var(--color-good, #16a34a)" : "var(--color-error, #dc2626)" }}>
              {result.ok ? <CheckCircle size={15}/> : <AlertTriangle size={15}/>}
              {result.msg}
            </div>
          )}
        </div>
      </Card>

      {/* Once Graph is connected the wizard used to dead-end. Point the user at
          the remaining onboarding steps rather than leaving them to find them.
          The SMTP form itself lives with notification settings — not duplicated
          here — so there is one place to configure delivery. */}
      {status?.configured && (
        <Card title="Required Graph Permissions" badge={<Badge label={permissions?.some(p => p.status === "missing") ? "Action Required" : "Live Check"} tone={permissions?.some(p => p.status === "missing") ? "error" : "good"}/>}>
          <div data-inline-style="inline-ddbe82fa76">
            Vigil365 requires the following Application permissions in Entra ID. The live status is evaluated based on the success of the most recent collection run.
          </div>
          {!permissions || permissions.length === 0 ? (
            <EmptyState icon={<ShieldCheck size={28}/>} message="Run a collection to evaluate Graph permissions."/>
          ) : (
            <div className="tbl-wrap" style={{marginTop: 16}}>
              <table className="data-tbl">
                <thead><tr><th scope="col" style={{width:100}}>Status</th><th scope="col">Permission</th><th scope="col">Used For</th></tr></thead>
                <tbody>
                  {permissions.map((p, i) => (
                    <tr key={i}>
                      <td>
                        {p.status === "granted" ? <Badge label="Granted" tone="good" icon={<Check size={12}/>}/> :
                         p.status === "missing" ? <Badge label="Missing" tone="error" icon={<X size={12}/>}/> :
                         <Badge label="Unknown" tone="neutral" icon={<HelpCircle size={12}/>}/>}
                      </td>
                      <td><div className="al-title">{p.permission}</div></td>
                      <td className="al-desc">{p.features.join(", ")}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </Card>
      )}

      {status?.configured && (
        <Card title="Next steps">
          <div className="setup-next-grid">
            {[
              { icon: <ShieldCheck size={16}/>, label: "Run your first collection",
                hint: "Populate the dashboard with your tenant's security data.", page: "overview" },
              { icon: <Bell size={16}/>, label: "Set up notifications",
                hint: "Add Teams, email (SMTP), or a webhook so alerts reach you.", page: "alertcenter" },
              { icon: <Users size={16}/>, label: "Invite your team",
                hint: "Add analysts and viewers with role-based access.", page: "users" },
            ].map(s => (
              <button key={s.page} type="button" className="setup-next-item"
                onClick={() => crossNavigate({ page: s.page })}>
                <span className="setup-next-icon">{s.icon}</span>
                <span className="setup-next-body">
                  <span className="setup-next-label">{s.label}</span>
                  <span className="setup-next-hint">{s.hint}</span>
                </span>
                <ChevronRight size={15} className="setup-next-chevron"/>
              </button>
            ))}
          </div>
        </Card>
      )}
    </div>
  );
}
