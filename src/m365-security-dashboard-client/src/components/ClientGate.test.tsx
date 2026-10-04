// @vitest-environment jsdom
import React from "react";
import { describe, it, expect, beforeEach, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ClientGate } from "./ClientGate";
import { AuthContext, apiFetch, setEditionMode, setSelectedTenantId, getSelectedTenantId, getActiveClientName, setActiveClientName, clientFileName } from "../services/api";
import { resetMyTenantsCache } from "../services/tenants";
import { showToast, showInstallToast, registerToastHandler } from "../services/toast";
import { mockApi } from "../test/apiMock";

const A = { id: "aaaa", name: "Contoso Ltd", isActive: true, configured: true, lastCollectionStatus: "Completed" };
const B = { id: "bbbb", name: "Fabrikam Inc", isActive: true, configured: true, lastCollectionStatus: "Completed" };
const admin = { email: "a@x.test", name: "A", role: "Admin" as const, isAdmin: true, canMutate: true };

// What TenantResolutionMiddleware does: any /api call naming a client the user
// may not select is refused with 403 before the endpoint runs.
function rejectUnpermittedTenant(permitted: string[]) {
  const inner = globalThis.fetch;
  vi.stubGlobal("fetch", vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const asked = new Headers(init?.headers).get("X-Vigil-Tenant");
    if (asked && !permitted.includes(asked)) {
      return Promise.resolve(new Response(JSON.stringify({ ok: false, message: "You do not have access to that tenant." }),
        { status: 403, headers: { "Content-Type": "application/json" } }));
    }
    return inner(input, init);
  }));
}

const viewer = { email: "v@x.test", name: "V", role: "Viewer" as const, isAdmin: false, canMutate: false };

function renderGate(opts: { as?: typeof admin | typeof viewer; onSignOut?: () => void; username?: string } = {}) {
  return render(
    <AuthContext.Provider value={opts.as ?? admin}>
      <ClientGate onSignOut={opts.onSignOut} account={opts.username ? { username: opts.username } : null}>
        <div>APP SHELL</div>
      </ClientGate>
    </AuthContext.Provider>
  );
}

beforeEach(() => { resetMyTenantsCache(); setActiveClientName(null); });

describe("ClientGate (U1, U5)", () => {
  it("single-organisation install: renders the app immediately, asks nothing", () => {
    setEditionMode("Single");
    const api = mockApi();
    renderGate();
    expect(screen.getByText("APP SHELL")).toBeInTheDocument();
    expect(api.calls).toHaveLength(0);
  });

  it("MSP with several clients and none chosen: shows Choose a client, never the app shell", async () => {
    setEditionMode("Msp");
    const api = mockApi({
      "/api/tenants/me": { body: { current: null, tenants: [A, B] } },
      "/api/tenants/rollup": { body: [] },
      "/api/tenants": { body: [] },
    });
    renderGate();
    expect(await screen.findByText("Choose a client")).toBeInTheDocument();
    expect(screen.queryByText("APP SHELL")).toBeNull();
    // Only cross-client endpoints are called — nothing that would 400 without a client.
    expect(api.calls.every(c => c.path.startsWith("/api/tenants"))).toBe(true);
  });

  it("MSP with a valid stored choice: renders the app and records the client's name", async () => {
    setEditionMode("Msp");
    setSelectedTenantId("bbbb");
    mockApi({ "/api/tenants/me": { body: { current: "bbbb", tenants: [A, B] } } });
    renderGate();
    expect(await screen.findByText("APP SHELL")).toBeInTheDocument();
    expect(getActiveClientName()).toBe("Fabrikam Inc");
  });

  it("MSP user with exactly one client (server auto-selects): straight into the app", async () => {
    setEditionMode("Msp");
    mockApi({ "/api/tenants/me": { body: { current: "aaaa", tenants: [A] } } });
    renderGate();
    expect(await screen.findByText("APP SHELL")).toBeInTheDocument();
    expect(getActiveClientName()).toBe("Contoso Ltd");
  });

  it("a stored choice the user can no longer see is dropped and they must choose again", async () => {
    setEditionMode("Msp");
    setSelectedTenantId("revoked");
    mockApi({
      "/api/tenants/me": { body: { current: null, tenants: [A, B] } },
      "/api/tenants/rollup": { body: [] },
      "/api/tenants": { body: [] },
    });
    rejectUnpermittedTenant([A.id, B.id]);
    renderGate();
    expect(await screen.findByText("Choose a client")).toBeInTheDocument();
    expect(getSelectedTenantId()).toBeNull();
  });

  it("identity calls never carry the selection; data calls do", async () => {
    setEditionMode("Msp");
    setSelectedTenantId("bbbb");
    const api = mockApi({ "/api/auth/me": { body: {} }, "/api/tenants/me": { body: {} }, "/api/dashboard/overview": { body: {} } });
    await apiFetch("/api/auth/me");
    await apiFetch("/api/tenants/me");
    await apiFetch("/api/dashboard/overview");
    expect(api.calls.map(c => c.headers.get("X-Vigil-Tenant"))).toEqual([null, null, "bbbb"]);
  });

  it("no clients assigned: explains it instead of showing errors", async () => {
    setEditionMode("Msp");
    mockApi({ "/api/tenants/me": { body: { current: null, tenants: [] } } });
    renderGate({ as: viewer });
    expect(await screen.findByText(/No client tenants are assigned to this account yet/)).toBeInTheDocument();
    expect(screen.queryByText("APP SHELL")).toBeNull();
  });

  // Landing here after signing in with an account other than the install's Admin
  // (a new database makes everyone else a Viewer) was a dead end: no hint which
  // account, and no way to switch — the app shell's user menu is not rendered.
  it("no clients assigned: names the signed-in account and offers Sign out", async () => {
    setEditionMode("Msp");
    mockApi({ "/api/tenants/me": { body: { current: null, tenants: [] } } });
    const onSignOut = vi.fn();
    renderGate({ as: viewer, onSignOut });
    expect(await screen.findByText("v@x.test")).toBeInTheDocument();
    expect(screen.getByText(/sign in with the account you entered as Admin email in Setup/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Sign out" }));
    expect(onSignOut).toHaveBeenCalledTimes(1);
  });

  it("falls back to the Microsoft account name when /api/auth/me gave no email", async () => {
    setEditionMode("Msp");
    mockApi({ "/api/tenants/me": { body: { current: null, tenants: [] } } });
    renderGate({ as: { ...viewer, email: "" }, onSignOut: () => {}, username: "samir@contoso.test" });
    expect(await screen.findByText("samir@contoso.test")).toBeInTheDocument();
  });

  it("an Admin with no active client reaches the Clients page, not a dead end", async () => {
    setEditionMode("Msp");
    mockApi({
      "/api/tenants/me": { body: { current: null, tenants: [] } },
      "/api/tenants/rollup": { body: [] },
      "/api/tenants": { body: [] },
      "/api/tenants/alerts": { body: [] },
    });
    renderGate({ as: admin, onSignOut: () => {} });
    expect(await screen.findByText(/No clients yet\. Add the first one below\./)).toBeInTheDocument();
    expect(screen.queryByText(/No client tenants are assigned/)).toBeNull();
    expect(screen.getByRole("button", { name: "Sign out" })).toBeInTheDocument();
  });

  it("Choose a client also shows who is signed in", async () => {
    setEditionMode("Msp");
    mockApi({
      "/api/tenants/me": { body: { current: null, tenants: [A, B] } },
      "/api/tenants/rollup": { body: [] },
      "/api/tenants": { body: [] },
      "/api/tenants/alerts": { body: [] },
    });
    renderGate({ as: admin, onSignOut: () => {} });
    expect(await screen.findByRole("heading", { name: "Choose a client" })).toBeInTheDocument();
    expect(screen.getByText("a@x.test")).toBeInTheDocument();
  });
});

describe("active client is named everywhere (U5)", () => {
  it("prefixes toasts and export filenames in MSP mode only", () => {
    const seen: string[] = [];
    const off = registerToastHandler(t => seen.push(t.message));
    setEditionMode("Msp");
    setActiveClientName("Contoso Ltd");
    showToast("Alert resolved");
    expect(seen.at(-1)).toBe("Contoso Ltd: Alert resolved");
    expect(clientFileName("alerts.csv")).toBe("contoso-ltd-alerts.csv");

    setEditionMode("Single");
    showToast("Alert resolved");
    expect(seen.at(-1)).toBe("Alert resolved");
    expect(clientFileName("alerts.csv")).toBe("alerts.csv");
    off();
  });

  it("install-wide actions name no client, even with one selected", () => {
    const seen: string[] = [];
    const off = registerToastHandler(t => seen.push(t.message));
    setEditionMode("Msp");
    setActiveClientName("Contoso Ltd");
    showInstallToast("alice@x.test is now Analyst");
    expect(seen.at(-1)).toBe("alice@x.test is now Analyst");
    off();
  });
});

describe("onboarding consent poll (U4)", () => {
  it("stops polling when the dialog is closed mid sign-in", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    setEditionMode("Msp");
    const row = { id: "bbbb", name: "Fabrikam Inc", microsoftTenantId: null, isActive: true, notes: null, createdAt: "2026-09-01T00:00:00Z",
      hasOwnCredentials: false, clientId: null, authMode: null, certificateThumbprint: null, brandName: null, brandAccentColor: null,
      consecutiveFailures: 0, nextCollectionAfter: null, credentialSource: "install", configured: true, consentGrantedAt: null,
      lastCollectionAt: null, lastCollectionStatus: null, lastError: null, openAlerts: 0 };
    const api = mockApi({
      "/api/tenants/rollup": { body: [] },
      "GET /api/tenants": { body: [row] },
      "GET /api/tenants/bbbb": { body: row },
      "GET /api/tenants/bbbb/consent-url": { body: { ok: true, url: "https://login.example/consent" } },
    });
    const popup = { closed: false, close: vi.fn() };
    vi.stubGlobal("open", vi.fn(() => popup));

    const { ClientsPage } = await import("../pages/ClientsPage");
    const { default: userEvent } = await import("@testing-library/user-event");
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    const view = render(<AuthContext.Provider value={admin}><ClientsPage /></AuthContext.Provider>);

    await user.click(await screen.findByRole("button", { name: /Connect/ }));
    await user.click(await screen.findByRole("button", { name: /Sign in as global admin & consent/ }));
    await vi.advanceTimersByTimeAsync(6000);
    const pollsWhileOpen = api.calls.filter(c => c.path === "/api/tenants/bbbb").length;
    expect(pollsWhileOpen).toBeGreaterThan(0);

    view.unmount(); // dialog (and page) gone mid sign-in
    expect(popup.close).toHaveBeenCalled(); // nobody is left approving consent nothing waits for
    await vi.advanceTimersByTimeAsync(30_000);
    expect(api.calls.filter(c => c.path === "/api/tenants/bbbb").length).toBe(pollsWhileOpen);
    vi.useRealTimers();
  });
});
