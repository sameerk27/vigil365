// @vitest-environment jsdom
import React from "react";
import { describe, it, expect, beforeEach, vi } from "vitest";
import { render, screen, fireEvent, within, waitFor } from "@testing-library/react";
import { PowerPlatformPage } from "./PowerPlatformPage";
import { setEditionMode, acquireTokenPopupForTenant, acquireTokenSilentForTenant } from "../services/api";
import { clearPowerPlatformCache } from "../services/powerPlatform";
import { mockApi } from "../test/apiMock";

// The Microsoft popup can't run in tests: the token calls are replaced, and the calls the
// browser then makes to the Power Platform API and Graph go through the fetch mock.
vi.mock("../services/api", async (original) => ({
  ...(await original<typeof import("../services/api")>()),
  acquireTokenPopupForTenant: vi.fn(async () => "user-token"),
  acquireTokenSilentForTenant: vi.fn(async () => null),
}));

const popup = vi.mocked(acquireTokenPopupForTenant);
const silent = vi.mocked(acquireTokenSilentForTenant);

const TENANT = "11111111-1111-1111-1111-111111111111";
const settings = { configured: true, tenantId: TENANT, powerPlatformApi: "https://api.powerplatform.com", graphApi: "https://graph.microsoft.com", maxEnvironments: 50 };

const rows = [
  { name: "env-prod", type: "microsoft.powerplatform/environments", location: "europe", properties: { displayName: "Contoso (default)", environmentType: "Default", isManaged: false } },
  { name: "app-1", type: "microsoft.powerapps/canvasapps", properties: { displayName: "Expense approvals", environmentId: "env-prod", ownerId: "o-1", appUris: { big: "x".repeat(50) }, powerPlatformConnectors: [{ connectorId: "shared_sharepointonline", operations: [] }] } },
  { name: "agent-1", type: "microsoft.copilotstudio/agents", properties: { displayName: "HR helper", environmentId: "env-prod", authentication: "None" } },
];

const evaluated = (extra: Record<string, unknown> = {}) => ({
  readAt: "2026-10-23T09:00:00Z", appDetails: true, ownersResolved: true, notes: [],
  summary: { environments: 1, apps: 1, flows: 1, agents: 2, needALook: 3, consentBypassed: 1, highRiskConnector: 1, agentsWithoutSignIn: 1, sharedWithEveryone: 1, ownerGone: 1 },
  environments: [{ id: "env-prod", name: "Contoso (default)", type: "Default", region: "europe", isManaged: false, apps: 1, flows: 1, agents: 2 }],
  apps: [{ id: "app-1", name: "Expense approvals", environmentId: "env-prod", environmentName: "Contoso (default)", ownerId: "o-1", owner: "Priya Shah",
    created: null, modified: "2026-10-01T09:00:00Z", sharedUsers: 120, sharedGroups: 0, bypassConsent: true, quarantined: false,
    connectors: ["shared_sharepointonline"], flags: [{ label: "Consent prompt bypassed", severity: "high" }] }],
  flows: [{ id: "flow-1", name: "Send files to partner", environmentId: "env-prod", environmentName: "Contoso (default)", ownerId: "o-2", owner: null,
    created: null, modified: null, trigger: "sharepointonline", connectors: ["shared_sftpwithssh"],
    flags: [{ label: "High-risk connector: shared_sftpwithssh", severity: "high" }, { label: "Creator not in the directory", severity: "medium" }] }],
  agents: [
    { id: "agent-1", name: "HR helper", environmentId: "env-prod", environmentName: "Contoso (default)", ownerId: "o-1", owner: "Priya Shah", created: null,
      published: "2026-10-20T08:00:00Z", createdIn: "Copilot Studio", authentication: "None", channels: ["Teams"], sharedUsers: 0, sharedGroups: 0,
      sharedWithTenant: true, quarantined: false, connectors: [], flags: [{ label: "No sign-in required", severity: "high" }, { label: "Shared with the whole organisation", severity: "medium" }] },
    { id: "agent-2", name: "Draft bot", environmentId: "env-prod", environmentName: "Contoso (default)", ownerId: "o-1", owner: "Priya Shah", created: null,
      published: null, createdIn: "Copilot Studio", authentication: "Microsoft Entra", channels: [], sharedUsers: 2, sharedGroups: 0,
      sharedWithTenant: false, quarantined: false, connectors: [], flags: [] },
  ],
  ...extra,
});

/** The whole read: settings, Microsoft's inventory, app detail, and Vigil365's evaluation. */
const tenant = (over: Record<string, unknown> = {}) => mockApi({
  "GET /api/power-platform/settings": { body: settings },
  "POST /resourcequery/resources/query": { body: { totalRecords: 3, count: 3, data: rows, skipToken: "" } },
  "GET /powerapps/environments/env-prod/apps": { body: { value: [{ name: "app-1", properties: { displayName: "Expense approvals", bypassConsent: true, sharedUsersCount: 120, appUris: { x: 1 } } }] } },
  "POST /api/power-platform/inventory/evaluate": { body: evaluated() },
  ...over,
});

const tableOf = (title: string) => {
  const heading = screen.getByRole("heading", { name: title });
  return within(heading.closest(".card") as HTMLElement).getByRole("table");
};

beforeEach(() => {
  setEditionMode("Single");
  clearPowerPlatformCache();
  popup.mockReset(); popup.mockResolvedValue("user-token");
  silent.mockReset(); silent.mockResolvedValue(null);
});

describe("PowerPlatformPage: reading with the person's own sign-in", () => {
  it("reads with a Microsoft sign-in, sends the rows to Vigil365 slimmed, and keeps the token at Microsoft", async () => {
    const api = tenant();
    render(<PowerPlatformPage/>);

    fireEvent.click(await screen.findByRole("button", { name: "Read with Microsoft sign-in" }));

    expect(await screen.findByText("Expense approvals")).toBeInTheDocument();
    expect(popup).toHaveBeenCalledWith(TENANT, ["https://api.powerplatform.com/ResourceQuery.Resources.Read", "https://api.powerplatform.com/PowerApps.Apps.Read"]);

    const query = api.calls.find(c => c.path === "/resourcequery/resources/query")!;
    expect(query.headers.get("Authorization")).toBe("Bearer user-token");
    expect(query.body).toMatchObject({ TableName: "PowerPlatformResources", Clauses: [{ $type: "where", FieldName: "type", Operator: "in~" }] });

    const evaluate = api.calls.find(c => c.path === "/api/power-platform/inventory/evaluate")!.body as {
      resources: { properties: Record<string, unknown> }[]; appDetails: { environmentId: string; items: { properties: Record<string, unknown> }[] }[];
    };
    expect(evaluate.resources).toHaveLength(3);
    expect(evaluate.resources[1].properties.appUris).toBeUndefined(); // only the fields Vigil365 evaluates
    expect(evaluate.resources[1].properties.powerPlatformConnectors).toEqual([{ connectorId: "shared_sharepointonline", operations: [] }]);
    expect(evaluate.appDetails[0].environmentId).toBe("env-prod");
    expect(evaluate.appDetails[0].items[0].properties).toMatchObject({ bypassConsent: true, sharedUsersCount: 120 });
    expect(evaluate.appDetails[0].items[0].properties.appUris).toBeUndefined();
    // The person's token goes to Microsoft only, never to Vigil365's own API.
    expect(api.calls.filter(c => c.path.startsWith("/api/")).every(c => c.headers.get("Authorization") !== "Bearer user-token")).toBe(true);
  });

  it("reads straight away without a popup when the person already consented", async () => {
    silent.mockResolvedValue("user-token");
    tenant();
    render(<PowerPlatformPage/>);

    expect(await screen.findByText("Expense approvals")).toBeInTheDocument();
    expect(popup).not.toHaveBeenCalled();
  });

  it("follows the inventory's skip token to the next page", async () => {
    let page = 0;
    const api = tenant({
      "POST /resourcequery/resources/query": () => ({ body: ++page === 1 ? { data: rows.slice(0, 2), skipToken: "p2" } : { data: rows.slice(2), skipToken: "" } }),
    });
    render(<PowerPlatformPage/>);
    fireEvent.click(await screen.findByRole("button", { name: "Read with Microsoft sign-in" }));

    await screen.findByText("Expense approvals");
    const queries = api.calls.filter(c => c.path === "/resourcequery/resources/query");
    expect(queries).toHaveLength(2);
    expect((queries[1].body as { Options: { SkipToken: string } }).Options.SkipToken).toBe("p2");
    expect((api.calls.find(c => c.path === "/api/power-platform/inventory/evaluate")!.body as { resources: unknown[] }).resources).toHaveLength(3);
  });

  it("says which Microsoft roles can read the inventory when the account can't", async () => {
    tenant({ "POST /resourcequery/resources/query": { status: 403, body: { error: { message: "Forbidden" } } } });
    render(<PowerPlatformPage/>);
    fireEvent.click(await screen.findByRole("button", { name: "Read with Microsoft sign-in" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("Power Platform Administrator, Dynamics 365 Administrator or Global Reader");
  });

  it("an account that can read the inventory but not apps as admin still gets the inventory, with the reason", async () => {
    const api = tenant({ "GET /powerapps/environments/env-prod/apps": { status: 403, body: {} } });
    render(<PowerPlatformPage/>);
    fireEvent.click(await screen.findByRole("button", { name: "Read with Microsoft sign-in" }));

    await screen.findByText("Expense approvals");
    const body = api.calls.find(c => c.path === "/api/power-platform/inventory/evaluate")!.body as { appDetails: unknown[]; appDetailsProblem: string };
    expect(body.appDetails).toEqual([]);
    expect(body.appDetailsProblem).toContain("Power Platform Administrator");
  });

  it("a next page on another host isn't followed, and the app detail is sent as partial", async () => {
    const api = tenant({
      "GET /powerapps/environments/env-prod/apps": { body: { value: [{ name: "app-1", properties: { sharedUsersCount: 120 } }], nextLink: "https://elsewhere.example/apps?page=2" } },
    });
    render(<PowerPlatformPage/>);
    fireEvent.click(await screen.findByRole("button", { name: "Read with Microsoft sign-in" }));

    await screen.findByText("Expense approvals");
    expect(api.calls.some(c => c.path === "/apps")).toBe(false);
    const body = api.calls.find(c => c.path === "/api/power-platform/inventory/evaluate")!.body as { appDetails: { complete: boolean; items: unknown[] }[] };
    expect(body.appDetails[0]).toMatchObject({ complete: false });
    expect(body.appDetails[0].items).toHaveLength(1);
  });

  it("an unreadable app detail response costs that environment's detail, not the read", async () => {
    const api = tenant();
    const mocked = vi.mocked(fetch);
    const answer = mocked.getMockImplementation()!;
    mocked.mockImplementation(async (input, init) =>
      String(input).includes("/powerapps/") ? new Response("<html>gateway</html>", { status: 200 }) : answer(input, init));
    render(<PowerPlatformPage/>);
    fireEvent.click(await screen.findByRole("button", { name: "Read with Microsoft sign-in" }));

    await screen.findByText("Expense approvals");
    const body = api.calls.find(c => c.path === "/api/power-platform/inventory/evaluate")!.body as { appDetails: { complete: boolean; error: string }[] };
    expect(body.appDetails[0]).toMatchObject({ complete: false, error: "unreadable response" });
  });

  it("an inventory too large for Vigil365 says so", async () => {
    tenant({ "POST /api/power-platform/inventory/evaluate": { status: 413, body: {} } });
    render(<PowerPlatformPage/>);
    fireEvent.click(await screen.findByRole("button", { name: "Read with Microsoft sign-in" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("larger than Vigil365 can evaluate");
  });

  it("a tenant without the Power Platform API gets a register step first", async () => {
    popup.mockRejectedValueOnce(Object.assign(new Error("AADSTS650052: The app needs access to a service that your organization has not subscribed to"), { errorCode: "invalid_client" }));
    const api = tenant({ "POST /v1.0/servicePrincipals": { status: 201, body: { id: "sp" } } });
    render(<PowerPlatformPage/>);

    fireEvent.click(await screen.findByRole("button", { name: "Read with Microsoft sign-in" }));
    fireEvent.click(await screen.findByRole("button", { name: "Register the Power Platform API" }));

    expect(await screen.findByText("Power Platform API registered. Now read the inventory.")).toBeInTheDocument();
    expect(popup).toHaveBeenLastCalledWith(TENANT, ["https://graph.microsoft.com/Application.ReadWrite.All"]);
    expect(api.calls.find(c => c.method === "POST" && c.path === "/v1.0/servicePrincipals")!.body).toEqual({ appId: "8578e004-a5c6-46e7-913e-12f58912df43" });
  });

  it("closing the sign-in window is not an error, but says where admin approval comes from", async () => {
    popup.mockRejectedValueOnce(Object.assign(new Error("User cancelled the flow."), { errorCode: "user_cancelled" }));
    tenant();
    render(<PowerPlatformPage/>);

    fireEvent.click(await screen.findByRole("button", { name: "Read with Microsoft sign-in" }));

    await waitFor(() => expect(screen.getByRole("button", { name: "Read with Microsoft sign-in" })).toBeEnabled());
    expect(screen.queryByRole("alert")).toBeNull();
    expect(screen.getByText(/needs admin approval/)).toBeInTheDocument();
  });
});

describe("PowerPlatformPage: the inventory", () => {
  const readIt = async () => {
    silent.mockResolvedValue("user-token");
    render(<PowerPlatformPage/>);
    await screen.findByText("Expense approvals");
  };

  it("shows agents first, with sign-in and organisation-wide sharing flagged", async () => {
    tenant();
    await readIt();

    expect(screen.getByText("1 published · 1 without sign-in")).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText("Filter agents"), { target: { value: "nosignin" } });
    const agents = within(tableOf("Copilot Studio agents")).getAllByRole("row").slice(1);
    expect(agents).toHaveLength(1);
    expect(agents[0]).toHaveTextContent("HR helper");
    expect(agents[0]).toHaveTextContent("Everyone");
    expect(agents[0]).toHaveTextContent("No sign-in required");
  });

  it("names flows whose creator left, and shows connector ids without Microsoft's prefix", async () => {
    tenant();
    await readIt();

    fireEvent.change(screen.getByLabelText("Filter flows"), { target: { value: "owner" } });
    const flows = within(tableOf("Cloud flows")).getAllByRole("row").slice(1);
    expect(flows).toHaveLength(1);
    expect(flows[0]).toHaveTextContent("Unknown user");
    expect(flows[0]).toHaveTextContent("sftpwithssh");
  });

  it("explains what couldn't be read", async () => {
    tenant({ "POST /api/power-platform/inventory/evaluate": { body: evaluated({ notes: ["Finance: app sharing and consent detail couldn't be read (access refused)."] }) } });
    await readIt();

    expect(screen.getByRole("status")).toHaveTextContent("Finance: app sharing and consent detail couldn't be read (access refused).");
  });

  it("an install without Graph says so", async () => {
    mockApi({ "GET /api/power-platform/settings": { body: { configured: false } } });
    render(<PowerPlatformPage/>);
    expect(await screen.findByText("Connect Microsoft Graph in Setup to see Power Platform.")).toBeInTheDocument();
  });
});
