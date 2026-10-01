import React, { useEffect, useState } from "react";
import { Building2 } from "lucide-react";
import { getSelectedTenantId, setSelectedTenantId } from "../services/api";
import { tenantApi, type MyTenants } from "../services/tenants";

/**
 * Header control that picks which client tenant every API call is scoped to.
 * Renders nothing for a single-tenant install (or a user assigned to only one
 * client): there is nothing to choose and the server picks it. Changing the
 * selection reloads the app so no page keeps another tenant's data in state.
 */
export function TenantSwitcher() {
  const [mine, setMine] = useState<MyTenants | null>(null);

  useEffect(() => {
    let cancelled = false;
    tenantApi.me().then(r => {
      if (cancelled || !r.ok) return;
      setMine(r.value);
      // A stale local selection (tenant removed, or access revoked) must not
      // keep sending a header the server will reject.
      const stored = getSelectedTenantId();
      if (stored && !r.value.tenants.some(t => t.id === stored)) {
        setSelectedTenantId(null);
        window.location.reload();
      }
    });
    return () => { cancelled = true; };
  }, []);

  if (!mine || mine.tenants.length < 2) return null;

  const current = getSelectedTenantId() ?? mine.current ?? "";
  return (
    <label className="tenant-switcher" title="Client tenant this view is scoped to">
      <Building2 size={14} aria-hidden="true" />
      <select
        className="filter-sel tenant-switcher-sel"
        aria-label="Client tenant"
        value={current}
        onChange={e => {
          const id = e.target.value || null;
          setSelectedTenantId(id);
          window.location.reload();
        }}
      >
        {!current && <option value="">Select a client…</option>}
        {mine.tenants.map(t => (
          <option key={t.id} value={t.id}>
            {t.name}{!t.configured ? " (not connected)" : ""}
          </option>
        ))}
      </select>
    </label>
  );
}
