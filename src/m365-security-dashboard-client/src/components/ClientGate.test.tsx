// @vitest-environment jsdom
import React from "react";
import { describe, it, expect, beforeEach, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import { ClientGate } from "./ClientGate";
import { AuthContext, setEditionMode, setSelectedTenantId, getSelectedTenantId, getActiveClientName, setActiveClientName, clientFileName } from "../services/api";
import { resetMyTenantsCache } from "../services/tenants";
import { showToast, registerToastHandler } from "../services/toast";
import { mockApi } from "../test/apiMock";

const A = { id: "aaaa", name: "Contoso Ltd", isActive: true, configured: true, lastCollectionStatus: "Completed" };
const B = { id: "bbbb", name: "Fabrikam Inc", isActive: true, configured: true, lastCollectionStatus: "Completed" };
const admin = { email: "a@x.test", name: "A", role: "Admin" as const, isAdmin: true, canMutate: true };

function renderGate() {
  return render(
    <AuthContext.Provider value={admin}>
      <ClientGate><div>APP SHELL</div></ClientGate>
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
    renderGate();
    expect(await screen.findByText("Choose a client")).toBeInTheDocument();
    expect(getSelectedTenantId()).toBeNull();
  });

  it("no clients assigned: explains it instead of showing errors", async () => {
    setEditionMode("Msp");
    mockApi({ "/api/tenants/me": { body: { current: null, tenants: [] } } });
    renderGate();
    expect(await screen.findByText(/No client tenants are assigned to you yet/)).toBeInTheDocument();
    expect(screen.queryByText("APP SHELL")).toBeNull();
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
    await vi.advanceTimersByTimeAsync(30_000);
    expect(api.calls.filter(c => c.path === "/api/tenants/bbbb").length).toBe(pollsWhileOpen);
    vi.useRealTimers();
  });
});
