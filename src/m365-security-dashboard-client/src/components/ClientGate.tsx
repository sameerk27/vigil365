import React, { useEffect, useState } from "react";
import { Building2 } from "lucide-react";
import { isMspMode, getSelectedTenantId, setSelectedTenantId, setActiveClientName } from "../services/api";
import { loadMyTenants } from "../services/tenants";
import { EmptyState } from "./SharedComponents";
import { ClientsPage } from "../pages/ClientsPage";

type GateState = "loading" | "ready" | "pick" | "none";

/**
 * Decides, before the app shell renders, which client the UI is scoped to
 * (MSP_V12_PLAN.md U1, U5).
 *
 * The API fails closed when an MSP user can see several clients and none is
 * selected — every tenant-scoped call returns 400. Rather than let the shell and
 * every page render a wall of errors, this shows a focused "Choose a client"
 * screen (the Clients page: rollup + roster use only cross-client endpoints);
 * picking a card stores the choice and reloads into that client. A user with no
 * clients assigned gets a plain explanation instead.
 *
 * Single-organisation installs pass straight through, untouched.
 */
export function ClientGate({ children }: { children: React.ReactNode }) {
  const [state, setState] = useState<GateState>(() => (isMspMode() ? "loading" : "ready"));

  useEffect(() => {
    if (!isMspMode()) return;
    let cancelled = false;
    loadMyTenants().then(r => {
      if (cancelled) return;
      // If the list itself can't be read, don't block the app: pages show their errors.
      if (!r.ok) { setState("ready"); return; }

      const tenants = r.value.tenants;
      const stored = getSelectedTenantId();
      if (stored && !tenants.some(t => t.id === stored)) setSelectedTenantId(null); // access revoked / client removed

      const activeId = (stored && tenants.some(t => t.id === stored)) ? stored : r.value.current;
      const active = tenants.find(t => t.id === activeId) ?? null;

      if (tenants.length === 0) { setActiveClientName(null); setState("none"); return; }
      if (!active) { setActiveClientName(null); setState("pick"); return; }
      setActiveClientName(active.name);
      setState("ready");
    });
    return () => { cancelled = true; };
  }, []);

  if (state === "ready") return <>{children}</>;
  if (state === "loading") {
    return <div className="app-loading-shell"><div className="app-loading-text">Loading clients…</div></div>;
  }
  if (state === "none") {
    return (
      <div className="client-gate">
        <EmptyState icon={<Building2 size={28} />}
          message="No client tenants are assigned to you yet. Ask an Admin to assign you one in User Management." />
      </div>
    );
  }
  return (
    <div className="client-gate">
      <div className="client-gate-hdr">
        <h1 className="hdr-title">Choose a client</h1>
        <p className="hdr-sub">Vigil365 shows one client at a time. Pick one below to open its dashboard — you can switch any time from the header.</p>
      </div>
      <ClientsPage />
    </div>
  );
}
