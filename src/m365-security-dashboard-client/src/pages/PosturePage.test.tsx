// @vitest-environment jsdom
import React from "react";
import { describe, it, expect, beforeEach, afterEach, vi } from "vitest";
import { render, screen, fireEvent, within } from "@testing-library/react";
import { PosturePage } from "./PosturePage";
import { setEditionMode } from "../services/api";
import { mockApi } from "../test/apiMock";

const finding = (id: string, title: string, workload: string, status: string, extra: Record<string, unknown> = {}) => ({
  id, title, workload, status, detail: `${title}: detail`, licensedBy: status === "NotLicensed" ? null : "Microsoft 365 Business Premium",
  unlockedBy: status === "NotLicensed" ? ["Microsoft Entra ID P2", "Microsoft 365 E5"] : [], readFrom: "Microsoft Graph",
  whyItMatters: `Why ${title} matters.`, minimum: `Minimum for ${title}.`, fix: ["Open the admin centre.", "Turn it on."],
  portalUrl: "https://entra.microsoft.com/", feature: "Microsoft Entra ID P1 (Conditional Access)",
  featureSource: "https://learn.microsoft.com/en-us/entra/identity/conditional-access/overview",
  baselines: { scuba: ["MS.AAD.3.2v2"], cis: [], secureScore: [] }, ...extra,
});

const posture = (extra: Record<string, unknown> = {}) => ({
  configured: true, generatedAt: new Date().toISOString(), organizationName: "Contoso Ltd", error: null,
  licences: [{ partNumber: "SPB", name: "Microsoft 365 Business Premium", enabled: 25, consumed: 22 }],
  includedFeatures: ["Microsoft Entra ID P1 (Conditional Access)", "Microsoft Intune Plan 1"],
  summary: { total: 4, pass: 1, fail: 1, licensedNotConfigured: 1, licensedLowAdoption: 0, notLicensed: 1, unknown: 0 },
  scuba: { pass: 1, total: 3 }, secureScore: { current: 41.6, max: 80 },
  sources: [
    { name: "Microsoft Graph", state: "Connected", detail: "All Graph checks read" },
    { name: "Exchange Online PowerShell", state: "Not set up yet", detail: "Coming in the next phase." },
  ],
  findings: [
    finding("PST-AAD-002", "Legacy authentication is blocked", "Entra ID", "Pass"),
    finding("PST-INT-001", "Devices have a compliance policy", "Intune", "LicensedNotConfigured"),
    finding("PST-AAD-005", "High-risk users and sign-ins are blocked", "Entra ID", "NotLicensed"),
    finding("PST-AAD-001", "Conditional Access requires MFA for all users", "Entra ID", "Fail",
      { detail: "Enabled policy “MFA” is for all users, but it doesn't cover all cloud apps." }),
  ],
  upgrades: [{ unlockedBy: ["Microsoft Entra ID P2", "Microsoft 365 E5"], checks: ["PST-AAD-005"] }],
  ...extra,
});

const rows = () => within(screen.getByRole("table")).getAllByRole("row").slice(1);

beforeEach(() => setEditionMode("Single"));
afterEach(() => vi.restoreAllMocks());

describe("PosturePage", () => {
  it("lists what needs doing first: failures, then paid-for features not used, passes last", async () => {
    mockApi({ "GET /api/posture": { body: posture() } });
    render(<PosturePage/>);

    expect(await screen.findByText("Conditional Access requires MFA for all users")).toBeInTheDocument();
    const order = rows().map(r => r.textContent ?? "");
    expect(order[0]).toContain("Conditional Access requires MFA");
    expect(order[1]).toContain("Devices have a compliance policy");
    expect(order[3]).toContain("Legacy authentication is blocked");
    expect(rows()[0]).toHaveTextContent("it doesn't cover all cloud apps");
    expect(rows()[2]).toHaveTextContent("Needs Microsoft Entra ID P2");
  });

  it("a status tile filters the checks, and clicking it again clears the filter", async () => {
    mockApi({ "GET /api/posture": { body: posture() } });
    render(<PosturePage/>);
    const tile = await screen.findByRole("button", { name: /Paid for, not used/ });

    fireEvent.click(tile);
    expect(rows()).toHaveLength(1);
    expect(rows()[0]).toHaveTextContent("Devices have a compliance policy");

    fireEvent.click(tile);
    expect(rows()).toHaveLength(4);
  });

  it("search and the workload filter narrow the table", async () => {
    mockApi({ "GET /api/posture": { body: posture() } });
    render(<PosturePage/>);
    await screen.findByRole("table");

    fireEvent.change(screen.getByLabelText("Filter by workload"), { target: { value: "Intune" } });
    expect(rows()).toHaveLength(1);

    fireEvent.change(screen.getByLabelText("Filter by workload"), { target: { value: "" } });
    fireEvent.change(screen.getByLabelText("Search checks"), { target: { value: "legacy" } });
    expect(rows()).toHaveLength(1);
    expect(rows()[0]).toHaveTextContent("Legacy authentication is blocked");

    fireEvent.change(screen.getByLabelText("Search checks"), { target: { value: "nothing like this" } });
    expect(screen.getByText("No checks match these filters.")).toBeInTheDocument();
  });

  it("sorts by check name when that header is clicked, and flips on a second click", async () => {
    mockApi({ "GET /api/posture": { body: posture() } });
    render(<PosturePage/>);
    await screen.findByRole("table");

    fireEvent.click(screen.getByRole("button", { name: "Check" }));
    expect(screen.getByRole("columnheader", { name: "Check" })).toHaveAttribute("aria-sort", "ascending");
    expect(rows()[0]).toHaveTextContent("Conditional Access requires MFA");

    fireEvent.click(screen.getByRole("button", { name: "Check" }));
    expect(screen.getByRole("columnheader", { name: "Check" })).toHaveAttribute("aria-sort", "descending");
    expect(rows()[0]).toHaveTextContent("Legacy authentication is blocked");
  });

  it("a check opens its reason, minimum and fix steps", async () => {
    mockApi({ "GET /api/posture": { body: posture() } });
    render(<PosturePage/>);
    fireEvent.click(await screen.findByRole("button", { name: "Devices have a compliance policy" }));

    const dialog = screen.getByRole("dialog", { name: "Devices have a compliance policy" });
    expect(dialog).toHaveTextContent("Why Devices have a compliance policy matters.");
    expect(dialog).toHaveTextContent("Minimum for Devices have a compliance policy.");
    expect(within(dialog).getAllByRole("listitem").map(li => li.textContent)).toEqual(["Open the admin centre.", "Turn it on."]);
    expect(within(dialog).getByRole("link", { name: /Open in the admin centre/ })).toHaveAttribute("href", "https://entra.microsoft.com/");
  });

  it("shows which licence would unlock each check the tenant can't use", async () => {
    mockApi({ "GET /api/posture": { body: posture() } });
    render(<PosturePage/>);

    const card = (await screen.findByText("Options that need another licence")).closest(".card") as HTMLElement;
    expect(card).toHaveTextContent("Microsoft Entra ID P2");
    expect(card).toHaveTextContent("also in Microsoft 365 E5");
    expect(card).toHaveTextContent("High-risk users and sign-ins are blocked");
  });

  it("says why every check is Unknown when the licences couldn't be read", async () => {
    mockApi({ "GET /api/posture": { body: posture({ error: "The tenant's licences couldn't be read: Permission denied.", licences: [], includedFeatures: [] }) } });
    render(<PosturePage/>);

    const notice = await screen.findByRole("status");
    expect(notice).toHaveTextContent("Every check shows as Unknown");
    expect(notice).toHaveTextContent("Permission denied.");
  });

  it("reads again past the server's cache only when asked", async () => {
    mockApi({ "GET /api/posture": { body: posture() } });
    render(<PosturePage/>);
    fireEvent.click(await screen.findByRole("button", { name: /Read again/ }));

    await vi.waitFor(() => expect(vi.mocked(fetch).mock.calls).toHaveLength(2));
    const urls = vi.mocked(fetch).mock.calls.map(([input]) => String(input));
    expect(urls[0]).not.toContain("refresh=true");
    expect(urls[1]).toContain("/api/posture?refresh=true");
  });

  it("downloads the client report through the API, scoped like every other call", async () => {
    const api = mockApi({
      "GET /api/posture": { body: posture() },
      "GET /api/posture/report.html": { body: "<!doctype html><p>report</p>" },
    });
    URL.createObjectURL = vi.fn(() => "blob:report");
    URL.revokeObjectURL = vi.fn();
    const click = vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(() => {});
    render(<PosturePage/>);

    fireEvent.click(await screen.findByRole("button", { name: /Client report/ }));

    await vi.waitFor(() => expect(click).toHaveBeenCalled());
    expect(api.calls.some(c => c.path === "/api/posture/report.html")).toBe(true);
  });

  it("asks for Graph to be connected when it isn't", async () => {
    mockApi({ "GET /api/posture": { body: { configured: false } } });
    render(<PosturePage/>);
    expect(await screen.findByText("Connect Microsoft Graph in Setup to see licence posture.")).toBeInTheDocument();
  });

  it("says the request failed rather than showing an empty page", async () => {
    mockApi({ "GET /api/posture": { status: 500, body: { error: "boom" } } });
    render(<PosturePage/>);
    expect(await screen.findByText(/Could not load licence posture/)).toBeInTheDocument();
  });
});
