import React, { useEffect, useState } from "react";
import { Building2 } from "lucide-react";
import { isMspMode, getSelectedTenantId, setSelectedTenantId, setActiveClientName, useAuth } from "../services/api";
import { loadMyTenants } from "../services/tenants";
import { EmptyState } from "./SharedComponents";
import { ClientsPage } from "../pages/ClientsPage";

type GateState = "loading" | "ready" | "pick" | "none";

function GateAccount({ email, onSignOut }: { email: string; onSignOut?: () => void }) {
  if (!onSignOut) return null;
  return (
    <div className="client-gate-account">
      <span>{email ? <>Signed in as <strong>{email}</strong></> : "Signed in"}</span>
      <button type="button" className="btn-export" onClick={onSignOut}>Sign out</button>
    </div>
  );
}

/**
 * Decides, before the app shell renders, which client the UI is scoped to
 * (MSP_V12_PLAN.md U1, U5).
 *
 * The API fails closed when an MSP user can see several clients and none is
 * selected — every tenant-scoped call returns 400. Rather than let the shell and
 * every page render a wall of errors, this shows a focused "Choose a client"
 * screen (the Clients page: rollup + roster use only cross-client endpoints);
 * picking a card stores the choice and reloads into that client. A user with no
 * clients assigned gets a plain explanation instead. These screens render without
 * the app shell (so without its user menu), so they show who is signed in and a
 * Sign out button themselves: the common way to land on "no clients" is signing in
 * with an account other than the install's Admin, and without this it is a dead end.
 *
 * An Admin with no active client is not stuck on that explanation — they get the
 * Clients page, where a client can be added or re-activated.
 *
 * Single-organisation installs pass straight through, untouched.
 */
export function ClientGate({ children, account, onSignOut }: {
  children: React.ReactNode;
  account?: { name?: string; username: string } | null;
  onSignOut?: () => void;
}) {
  const auth = useAuth();
  const email = auth.email || account?.username || "";
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
      // The server resolved a client on its own (e.g. the user's only one):
      // record it so per-client controls know a client is in scope (U13).
      if (active.id !== stored) setSelectedTenantId(active.id);
      setActiveClientName(active.name);
      setState("ready");
    });
    return () => { cancelled = true; };
  }, []);

  if (state === "ready") return <>{children}</>;
  if (state === "loading") {
    return <div className="app-loading-shell"><div className="app-loading-text">Loading clients…</div></div>;
  }
  if (state === "none" && !auth.isAdmin) {
    return (
      <main className="client-gate" id="main-content">
        <GateAccount email={email} onSignOut={onSignOut} />
        <EmptyState icon={<Building2 size={28} />}
          message="No client tenants are assigned to this account yet. Ask an Admin to assign you one in User Management. If you installed Vigil365 and expected to be the Admin, sign out and sign in with the account you entered as Admin email in Setup." />
      </main>
    );
  }
  return (
    <main className="client-gate" id="main-content">
      <GateAccount email={email} onSignOut={onSignOut} />
      <div className="client-gate-hdr">
        <h1 className="hdr-title">Choose a client</h1>
        <p className="hdr-sub">Vigil365 shows one client at a time. Pick one below to open its dashboard — you can switch any time from the header.</p>
      </div>
      <ClientsPage />
    </main>
  );
}
