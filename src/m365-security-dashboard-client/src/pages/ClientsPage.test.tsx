// @vitest-environment jsdom
import React from "react";
import { describe, it, expect, beforeEach, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ClientsPage } from "./ClientsPage";
import { AuthContext, setEditionMode, setSelectedTenantId, getSelectedTenantId, setActiveClientName } from "../services/api";
import { registerToastHandler } from "../services/toast";
import { registerConfirmHandler } from "../services/confirm";
import { mockApi } from "../test/apiMock";
import type { ClientTenant } from "../services/tenants";

const admin = { email: "a@x.test", name: "A", role: "Admin" as const, isAdmin: true, canMutate: true };

const row = (id: string, name: string, extra: Partial<ClientTenant> = {}): ClientTenant => ({
  id, name, microsoftTenantId: null, isActive: true, notes: null, createdAt: "2026-09-01T00:00:00Z",
  hasOwnCredentials: false, clientId: null, authMode: null, certificateThumbprint: null, brandName: null, brandAccentColor: null,
  consecutiveFailures: 0, nextCollectionAfter: null, credentialSource: "install", configured: true, consentGrantedAt: null,
  lastCollectionAt: null, lastCollectionStatus: null, lastError: null, openAlerts: 0, ...extra,
});
const A = row("aaaa", "Contoso Ltd");
const B = row("bbbb", "Fabrikam Inc");

const reload = vi.fn();
let toasts: string[] = [];

beforeEach(() => {
  reload.mockReset();
  Object.defineProperty(window, "location", { configurable: true, value: { ...window.location, reload } });
  setEditionMode("Msp");
  setActiveClientName("Contoso Ltd"); // what ClientGate records for the selected client
  toasts = [];
  registerToastHandler(t => toasts.push(t.message));
  registerConfirmHandler(async () => true);
});

function renderPage() {
  return render(<AuthContext.Provider value={admin}><ClientsPage /></AuthContext.Provider>);
}
const rosterRow = async (name: string) => (await screen.findByText(name, { selector: "strong" })).closest("tr")!;

describe("Clients roster", () => {
  it("deactivating the selected client reloads, so nothing stays scoped to it; another client does not", async () => {
    setSelectedTenantId("aaaa");
    mockApi({
      "/api/tenants/rollup": { body: [] },
      "GET /api/tenants": { body: [A, B] },
      "DELETE /api/tenants/aaaa": { body: { ok: true } },
      "DELETE /api/tenants/bbbb": { body: { ok: true } },
    });
    renderPage();

    await userEvent.click(within(await rosterRow("Fabrikam Inc")).getByRole("button", { name: /Deactivate/ }));
    await waitFor(() => expect(toasts).toContain("Fabrikam Inc: Deactivated"));
    expect(reload).not.toHaveBeenCalled();
    expect(getSelectedTenantId()).toBe("aaaa");

    await userEvent.click(within(await rosterRow("Contoso Ltd")).getByRole("button", { name: /Deactivate/ }));
    await waitFor(() => expect(reload).toHaveBeenCalledTimes(1));
    expect(getSelectedTenantId()).toBeNull();
  });

  it("re-activating a client keeps its report branding", async () => {
    const inactive = row("cccc", "Northwind", { isActive: false, brandName: "Northwind Security", brandAccentColor: "#1d4ed8" });
    const api = mockApi({
      "/api/tenants/rollup": { body: [] },
      "GET /api/tenants": { body: [inactive] },
      "PUT /api/tenants/cccc": { body: { ok: true } },
    });
    renderPage();
    await userEvent.click(within(await rosterRow("Northwind")).getByRole("button", { name: /Re-activate/ }));
    await waitFor(() => expect(api.calls.some(c => c.method === "PUT")).toBe(true));
    expect(api.calls.find(c => c.method === "PUT")!.body)
      .toMatchObject({ isActive: true, brandName: "Northwind Security", brandAccentColor: "#1d4ed8" });
  });

  it("a roster action's toast names the client acted on, not the selected one", async () => {
    mockApi({
      "/api/tenants/rollup": { body: [] },
      "GET /api/tenants": { body: [A, B] },
      "POST /api/tenants/bbbb/test": { status: 400, body: { ok: false, message: "AADSTS700016: app not found" } },
    });
    renderPage();
    await userEvent.click(within(await rosterRow("Fabrikam Inc")).getByRole("button", { name: /Test/ }));
    await waitFor(() => expect(toasts).toContain("Fabrikam Inc: AADSTS700016: app not found"));
    expect(toasts.some(t => t.startsWith("Contoso Ltd:"))).toBe(false);
  });
});

describe("onboarding dialog", () => {
  it("Add client: if the new row cannot be read back, the next Save updates it instead of adding it again", async () => {
    const api = mockApi({
      "/api/tenants/rollup": { body: [] },
      "GET /api/tenants": { body: [] },
      "POST /api/tenants": { status: 201, body: { ok: true, id: "dddd" } },
      "GET /api/tenants/dddd": { status: 500, body: { message: "database unavailable" } },
      "PUT /api/tenants/dddd": { body: { ok: true } },
    });
    renderPage();
    await userEvent.click(await screen.findByRole("button", { name: /Add client/ }));
    const dialog = screen.getByRole("dialog");
    await userEvent.type(within(dialog).getByLabelText("Client display name"), "Tailspin");
    await userEvent.click(within(dialog).getByRole("button", { name: "Add client" }));

    await userEvent.click(await within(dialog).findByRole("button", { name: "Save" }));
    await waitFor(() => expect(api.calls.some(c => c.method === "PUT" && c.path === "/api/tenants/dddd")).toBe(true));
    expect(api.calls.filter(c => c.method === "POST" && c.path === "/api/tenants")).toHaveLength(1);
    expect(toasts).toContain("Tailspin: Client added");
  });

  it("re-consent waits for a new consent, not the one already on record", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const entraId = "0f000000-0000-0000-0000-00000000000f";
    const consented = row("bbbb", "Fabrikam Inc", { consentGrantedAt: "2026-09-01T00:00:00Z" });
    let reconsented = false;
    const api = mockApi({
      "/api/tenants/rollup": { body: [] },
      "GET /api/tenants": { body: [consented] },
      "GET /api/tenants/bbbb/consent-url": { body: { ok: true, url: "https://login.example/consent" } },
      "GET /api/tenants/bbbb": () => ({ body: reconsented ? { ...consented, consentGrantedAt: "2026-10-03T10:00:00Z", microsoftTenantId: entraId } : consented }),
      "POST /api/tenants/bbbb/test": { body: { ok: true, microsoftTenantId: entraId, displayName: "Fabrikam Inc" } },
    });
    const popup = { closed: false, close: vi.fn() };
    vi.stubGlobal("open", vi.fn(() => popup));
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    renderPage();

    await user.click(within(await rosterRow("Fabrikam Inc")).getByRole("button", { name: /Connect/ }));
    await user.click(await screen.findByRole("button", { name: /Sign in as global admin & consent/ }));
    await vi.advanceTimersByTimeAsync(6000);
    expect(api.calls.filter(c => c.path === "/api/tenants/bbbb").length).toBeGreaterThan(0); // polled...
    expect(popup.close).not.toHaveBeenCalled();                                               // ...but still waiting
    expect(api.calls.some(c => c.path === "/api/tenants/bbbb/test")).toBe(false);

    reconsented = true;
    await vi.advanceTimersByTimeAsync(3000);
    await waitFor(() => expect(screen.getByText("Connected to Fabrikam Inc.")).toBeInTheDocument());
    expect(popup.close).toHaveBeenCalled();
    // The Entra id consent recorded is shown, so a later Save does not wipe it.
    expect(screen.getByLabelText("Entra tenant id")).toHaveValue(entraId);
    vi.useRealTimers();
  });

  it("testing a client that has not consented yet says it is waiting for its Global Administrator", async () => {
    // MSP mode: no Entra id and no app of its own means nothing applies until consent.
    const pending = row("bbbb", "Fabrikam Inc", { configured: false, credentialSource: "none" });
    const noCreds = { status: 400, body: { ok: false, message: "No Graph credentials apply to this tenant." } };
    mockApi({
      "/api/tenants/rollup": { body: [] },
      "GET /api/tenants": { body: [A, pending] },
      "GET /api/tenants/bbbb": { body: { ...pending, lastError: "No Graph credentials apply to this tenant." } },
      "POST /api/tenants/bbbb/test": noCreds,
      "POST /api/tenants/aaaa/test": noCreds,
    });
    renderPage();

    await userEvent.click(within(await rosterRow("Fabrikam Inc")).getByRole("button", { name: /Test/ }));
    await waitFor(() => expect(toasts.at(-1)).toMatch(/^Fabrikam Inc: Not connected yet: waiting for the client's Global Administrator to consent/));

    await userEvent.click(within(await rosterRow("Fabrikam Inc")).getByRole("button", { name: /Connect/ }));
    await userEvent.click(screen.getByText(/Can't sign in here/));
    await userEvent.click(screen.getByRole("button", { name: /Test now/ }));
    expect(await screen.findByText(/waiting for the client's Global Administrator to consent/)).toBeInTheDocument();
    expect(screen.queryByText(/No Graph credentials apply/)).toBeNull();

    // A configured client that fails gets the server's own reason.
    await userEvent.keyboard("{Escape}");
    await userEvent.click(within(await rosterRow("Contoso Ltd")).getByRole("button", { name: /Test/ }));
    await waitFor(() => expect(toasts.at(-1)).toBe("Contoso Ltd: No Graph credentials apply to this tenant."));
  });

  it("is a proper modal: focus starts inside, Escape closes, a stray click outside does not end a sign-in", async () => {
    mockApi({
      "/api/tenants/rollup": { body: [] },
      "GET /api/tenants": { body: [A, B] },
      "GET /api/tenants/bbbb": { body: B },
      "GET /api/tenants/bbbb/consent-url": { body: { ok: true, url: "https://login.example/consent" } },
    });
    const popup = { closed: false, close: vi.fn() };
    vi.stubGlobal("open", vi.fn(() => popup));
    renderPage();

    await userEvent.click(within(await rosterRow("Fabrikam Inc")).getByRole("button", { name: /Connect/ }));
    expect(screen.getByLabelText("Client display name")).toHaveFocus();
    await userEvent.keyboard("{Escape}");
    expect(screen.queryByRole("dialog")).toBeNull();

    await userEvent.click(within(await rosterRow("Fabrikam Inc")).getByRole("button", { name: /Connect/ }));
    await userEvent.click(screen.getByRole("button", { name: /Sign in as global admin & consent/ }));
    await screen.findByRole("button", { name: /Waiting for sign-in/ });
    await userEvent.click(document.querySelector(".detail-modal-backdrop")!);
    expect(screen.getByRole("dialog")).toBeInTheDocument();
    await userEvent.keyboard("{Escape}");
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(popup.close).toHaveBeenCalled();
  });
});
