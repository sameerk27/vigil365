import React, { useState } from "react";
import { showToast } from "../services/toast";
import { tenantApi, type ClientTenant } from "../services/tenants";

/**
 * Per-user client-tenant assignment, shown on the User Management page in an
 * MSP install. Admins see every tenant and are not assignable; everyone else
 * sees exactly the tenants ticked here. Saves on each change so the row never
 * holds unsaved state.
 */
export function TenantAssignmentPicker({ email, role, tenants, assigned, onChanged }: {
  email: string;
  role: string;
  tenants: Pick<ClientTenant, "id" | "name" | "isActive">[];
  assigned: string[];
  onChanged: (email: string, tenantIds: string[]) => void;
}) {
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);

  if (role === "Admin") return <span className="al-date" title="Admins see every client">All clients</span>;

  const active = tenants.filter(t => t.isActive);
  const label = assigned.length === 0 ? "No clients" : assigned.length === active.length ? "All clients" : `${assigned.length} of ${active.length}`;

  const toggle = async (tenantId: string, on: boolean) => {
    const next = on ? [...assigned, tenantId] : assigned.filter(id => id !== tenantId);
    setBusy(true);
    const r = await tenantApi.setAssignments(email, next);
    setBusy(false);
    if (r.ok) onChanged(email, next);
    else showToast(r.error, "error");
  };

  return (
    <div className="assign-wrap"
      onBlur={e => { if (!e.currentTarget.contains(e.relatedTarget as Node)) setOpen(false); }}
      onKeyDown={e => { if (e.key === "Escape") setOpen(false); }}>
      <button className="btn-export" onClick={() => setOpen(o => !o)} aria-haspopup="listbox" aria-expanded={open}
        title="Which client tenants this user may see">
        {label}
      </button>
      {open && (
        <div className="assign-pop" role="listbox" aria-label={`Clients for ${email}`}>
          {active.length === 0 && <div className="assign-empty">No active clients.</div>}
          {active.map(t => (
            <label key={t.id} className="assign-item">
              <input type="checkbox" disabled={busy} checked={assigned.includes(t.id)}
                onChange={e => toggle(t.id, e.target.checked)} />
              {t.name}
            </label>
          ))}
        </div>
      )}
    </div>
  );
}
