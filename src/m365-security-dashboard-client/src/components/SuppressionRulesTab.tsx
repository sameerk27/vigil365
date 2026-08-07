import React, { useCallback, useEffect, useState } from "react";
import { BellOff, Plus, Trash2, AlertTriangle } from "lucide-react";
import { AlertPolicy, SuppressionRule } from "../services/types";
import { suppressionApi, useAuth } from "../services/api";
import { Card, Badge, EmptyState } from "./SharedComponents";
import { showToast } from "../services/toast";
import { confirmAction } from "../services/confirm";
import { fmtDate, relTime } from "../services/utils";

/**
 * Standing suppression rules — the answer to alert fatigue from a known-noisy
 * source. Distinct from snooze: snooze silences one raised alert for a while,
 * a suppression stops a whole class of alerts being raised at all.
 *
 * Because a bad rule hides real alerts, the UI shows what each rule has actually
 * swallowed (hit count + last hit) and requires a reason up front.
 */
export function SuppressionRulesTab({ policies }: { policies: AlertPolicy[] }) {
  const auth = useAuth();
  const [rules, setRules] = useState<SuppressionRule[] | null>(null);
  const [adding, setAdding] = useState(false);
  const [policyId, setPolicyId] = useState("");
  const [entityPattern, setEntityPattern] = useState("");
  const [reason, setReason] = useState("");
  const [expiresAt, setExpiresAt] = useState("");
  const [saving, setSaving] = useState(false);

  const load = useCallback(() => {
    suppressionApi.list().then(setRules).catch(() => setRules([]));
  }, []);
  useEffect(() => { load(); }, [load]);

  const resetForm = () => {
    setPolicyId(""); setEntityPattern(""); setReason(""); setExpiresAt(""); setAdding(false);
  };

  const save = async () => {
    setSaving(true);
    const res = await suppressionApi.create({
      policyId: policyId || null,
      entityPattern: entityPattern.trim() || null,
      reason: reason.trim(),
      expiresAt: expiresAt ? new Date(expiresAt).toISOString() : null,
    });
    setSaving(false);
    if (res.ok) { showToast("Suppression rule created"); resetForm(); load(); }
    else showToast(res.error ?? "Could not create the rule", "error");
  };

  const toggle = async (r: SuppressionRule) => {
    if (await suppressionApi.update(r.id, { enabled: !r.enabled })) {
      showToast(r.enabled ? "Rule disabled" : "Rule enabled");
      load();
    } else showToast("Could not update the rule", "error");
  };

  const remove = async (r: SuppressionRule) => {
    const ok = await confirmAction({
      title: "Delete suppression rule?",
      message: `Alerts matching "${r.reason}" will start being raised again. Alerts already suppressed are not restored.`,
      confirmLabel: "Delete rule",
      danger: true,
    });
    if (!ok) return;
    if (await suppressionApi.remove(r.id)) { showToast("Suppression rule deleted"); load(); }
    else showToast("Could not delete the rule", "error");
  };

  const scopeOf = (r: SuppressionRule) => {
    const parts: string[] = [];
    parts.push(r.policyName ? `Policy: ${r.policyName}` : "All policies");
    if (r.entityPattern) parts.push(`Entity: ${r.entityPattern}`);
    return parts.join(" · ");
  };

  return (
    <Card title="Suppression Rules"
      badge={rules ? <Badge label={`${rules.filter(r => r.enabled && !r.expired).length} active`} tone={rules.some(r => r.enabled && !r.expired) ? "warning" : "neutral"}/> : undefined}
      action={auth.isAdmin && !adding ? (
        <button className="btn-apply" onClick={() => setAdding(true)}><Plus size={13}/> Add rule</button>
      ) : undefined}>

      <p className="supp-intro">
        Suppression stops matching alerts being raised at all — use it for known-noisy sources
        such as service accounts. To silence a single alert temporarily, snooze it from the queue instead.
      </p>

      {adding && (
        <div className="supp-form">
          <label className="supp-field">
            <span>Policy</span>
            <select className="filter-sel" value={policyId} onChange={e => setPolicyId(e.target.value)}>
              <option value="">All policies</option>
              {policies.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}
            </select>
          </label>
          <label className="supp-field">
            <span>Entity pattern</span>
            <input className="search-input" value={entityPattern} placeholder="svc-* or *@contractors.example.com"
              onChange={e => setEntityPattern(e.target.value)}/>
          </label>
          <label className="supp-field supp-field-wide">
            <span>Reason (required)</span>
            <input className="search-input" value={reason} placeholder="Nightly backup account trips this policy by design"
              onChange={e => setReason(e.target.value)}/>
          </label>
          <label className="supp-field">
            <span>Expires (optional)</span>
            <input className="search-input" type="date" value={expiresAt} onChange={e => setExpiresAt(e.target.value)}/>
          </label>
          <div className="supp-form-actions">
            <button className="btn-cancel" onClick={resetForm}>Cancel</button>
            <button className="btn-apply" disabled={saving || !reason.trim()} onClick={save}>
              {saving ? "Saving…" : "Create rule"}
            </button>
          </div>
          {!policyId && !entityPattern.trim() && (
            <div className="supp-warn"><AlertTriangle size={13}/> Scope the rule to a policy or an entity pattern — otherwise it would suppress every alert.</div>
          )}
        </div>
      )}

      {!rules ? (
        <EmptyState message="Loading suppression rules…"/>
      ) : rules.length === 0 ? (
        <EmptyState icon={<BellOff size={24}/>} message="No suppression rules — every alert that fires is shown."/>
      ) : (
        <div className="table-wrap">
          <table>
            <thead><tr>
              <th scope="col">Status</th><th scope="col">Scope</th><th scope="col">Reason</th>
              <th scope="col">Suppressed</th><th scope="col">Expires</th><th scope="col">Created</th>
              {auth.isAdmin && <th scope="col"></th>}
            </tr></thead>
            <tbody>
              {rules.map(r => (
                <tr key={r.id}>
                  <td>
                    {r.expired ? <Badge label="Expired" tone="neutral"/>
                      : r.enabled ? <Badge label="Active" tone="warning"/>
                      : <Badge label="Disabled" tone="neutral"/>}
                  </td>
                  <td className="supp-scope">{scopeOf(r)}</td>
                  <td className="supp-reason">{r.reason}</td>
                  <td title={r.lastSuppressedAt ? `Last: ${fmtDate(r.lastSuppressedAt)}` : "Never matched"}>
                    {r.suppressedCount > 0
                      ? <>{r.suppressedCount} <span className="supp-dim">({relTime(r.lastSuppressedAt!)})</span></>
                      : <span className="supp-dim">none yet</span>}
                  </td>
                  <td>{r.expiresAt ? fmtDate(r.expiresAt) : <span className="supp-dim">never</span>}</td>
                  <td className="supp-dim">{r.createdBy ?? "—"}</td>
                  {auth.isAdmin && (
                    <td>
                      <div className="supp-actions">
                        <button className="btn-export" onClick={() => toggle(r)}>{r.enabled ? "Disable" : "Enable"}</button>
                        <button className="btn-danger-icon" onClick={() => remove(r)} aria-label="Delete rule"><Trash2 size={14}/></button>
                      </div>
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
