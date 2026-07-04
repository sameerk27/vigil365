import React, { useState, useCallback, useEffect } from "react";
import { CheckCircle, AlertTriangle } from "lucide-react";
import { apiBase, apiFetch } from "../services/api";
import { showToast } from "../services/toast";
import { Card, Badge, CopyButton } from "../components/SharedComponents";

export function SetupPage() {
  const [status, setStatus] = useState<{ configured: boolean; tenantId: string; clientId: string; hasSecret: boolean } | null>(null);
  const [tenantId, setTenantId] = useState("");
  const [clientId, setClientId] = useState("");
  const [clientSecret, setClientSecret] = useState("");
  const [saving, setSaving] = useState(false);
  const [result, setResult] = useState<{ ok: boolean; msg: string } | null>(null);

  const loadStatus = useCallback(async () => {
    try {
      const r = await apiFetch(`${apiBase}/api/setup/graph`);
      if (r.ok) {
        const s = await r.json();
        setStatus(s);
        setTenantId(s.tenantId || "");
        setClientId(s.clientId || "");
      }
    } catch { /* ignore */ }
  }, []);

  useEffect(() => { loadStatus(); }, [loadStatus]);

  const save = async () => {
    if (!tenantId.trim() || !clientId.trim()) { showToast("Tenant ID and Client ID are required"); return; }
    setSaving(true); setResult(null);
    try {
      const r = await apiFetch(`${apiBase}/api/setup/graph`, {
        method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ tenantId: tenantId.trim(), clientId: clientId.trim(), clientSecret: clientSecret.trim() }),
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

  const field = (label: string, value: string, set: (v: string) => void, placeholder: string, type = "text", copyable = false) => (
    <div className="policy-field">
      <span className="policy-label" style={{ display: "flex", alignItems: "center", gap: 4 }}>
        {label}
        {copyable && value.trim() && <CopyButton value={value.trim()} label={label} size={12}/>}
      </span>
      <input className="policy-input" type={type} value={value} placeholder={placeholder} onChange={e => set(e.target.value)} />
    </div>
  );

  return (
    <div className="page">
      <Card title="Microsoft Graph Setup"
        badge={<Badge label={status?.configured ? "Configured" : "Not configured"} tone={status?.configured ? "good" : "warning"}/>}>
        <div style={{ fontSize:12, color:"var(--color-muted)", padding:"0 0 16px", lineHeight:1.6 }}>
          Enter the credentials from your Entra app registration. Vigil365 uses these to read your
          tenant's security data via Microsoft Graph (app-only, read-only). The client secret is stored
          encrypted at rest and is never shown again after saving.
        </div>
        <div style={{ maxWidth:520, display:"flex", flexDirection:"column", gap:12 }}>
          {field("Directory (tenant) ID", tenantId, setTenantId, "00000000-0000-0000-0000-000000000000", "text", true)}
          {field("Application (client) ID", clientId, setClientId, "00000000-0000-0000-0000-000000000000", "text", true)}
          {field(status?.hasSecret ? "Client secret (leave blank to keep current)" : "Client secret", clientSecret, setClientSecret, status?.hasSecret ? "••••••••" : "Paste secret value", "password")}
          <div>
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
    </div>
  );
}
