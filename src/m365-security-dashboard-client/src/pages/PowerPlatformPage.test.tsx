// @vitest-environment jsdom
import React from "react";
import { describe, it, expect, beforeEach } from "vitest";
import { render, screen, fireEvent, within } from "@testing-library/react";
import { PowerPlatformPage } from "./PowerPlatformPage";
import { setEditionMode } from "../services/api";
import { mockApi } from "../test/apiMock";

const setup = {
  tenantId: "11111111-1111-1111-1111-111111111111", appId: "22222222-2222-2222-2222-222222222222",
  role: "Power Platform reader", roleId: "c886ad2e-27f7-4874-8381-5849b8d8a090", script: "grant-power-platform-reader.ps1",
  command: ".\\grant-power-platform-reader.ps1 -TenantId 11111111-1111-1111-1111-111111111111 -AppId 22222222-2222-2222-2222-222222222222",
};

const inventory = (extra: Record<string, unknown> = {}) => ({
  configured: true, status: "Ready", reason: null, readAt: "2026-10-21T09:00:00Z", setup,
  summary: { environments: 2, environmentsRead: 2, environmentsSkipped: 0, apps: 2, flows: 2, consentBypassed: 1, highRiskConnector: 1, widelyShared: 1, customConnector: 0 },
  environments: [
    { id: "env-prod", name: "Contoso (default)", type: "Default", state: "Ready", region: "europe", hasDataverse: true, created: null, apps: 2, flows: 2, problem: null },
    { id: "env-trial", name: "Sam's trial", type: "Trial", state: "Ready", region: "unitedstates", hasDataverse: false, created: null, apps: 0, flows: 0, problem: null },
  ],
  apps: [
    { id: "app-1", name: "Expense approvals", environmentId: "env-prod", environmentName: "Contoso (default)", owner: "Priya Shah", created: null, modified: "2026-10-01T09:00:00Z",
      sharedUsers: 120, sharedGroups: 0, bypassConsent: true, connectors: ["SharePoint"],
      flags: [{ label: "Consent prompt bypassed", severity: "high" }, { label: "Shared with 120 people", severity: "medium" }] },
    { id: "app-2", name: "Room booking", environmentId: "env-prod", environmentName: "Contoso (default)", owner: "Lena Novak", created: null, modified: null,
      sharedUsers: 3, sharedGroups: 0, bypassConsent: false, connectors: [], flags: [] },
  ],
  flows: [
    { id: "f-1", name: "Sync invoices to partner API", environmentId: "env-prod", environmentName: "Contoso (default)", state: "On", created: null, modified: null,
      connectors: ["HTTP with Microsoft Entra ID"], flags: [{ label: "High-risk connector: HTTP with Microsoft Entra ID", severity: "high" }] },
    { id: "f-2", name: "Old reminder", environmentId: "env-prod", environmentName: "Contoso (default)", state: "Suspended", created: null, modified: null,
      connectors: [], flags: [{ label: "Suspended", severity: "low" }] },
  ],
  ...extra,
});

const tableOf = (title: string) => {
  const heading = screen.getByRole("heading", { name: title });
  return within(heading.closest(".card") as HTMLElement).getByRole("table");
};

beforeEach(() => setEditionMode("Single"));

describe("PowerPlatformPage", () => {
  it("a tenant without the role gets the turn-on steps with its own command", async () => {
    mockApi({ "GET /api/power-platform/inventory": { body: { configured: true, status: "NotEnabled", reason: "RoleMissing", setup } } });
    render(<PowerPlatformPage/>);

    expect(await screen.findByText("Power Platform inventory is off for this tenant")).toBeInTheDocument();
    expect(screen.getByText(setup.command)).toBeInTheDocument();
    expect(screen.getByText(/has no Power Platform role in this tenant yet/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Copy command" })).toBeInTheDocument();
  });

  it("'Check again' asks the API to read again instead of using its cache", async () => {
    const calls: string[] = [];
    mockApi({
      "GET /api/power-platform/inventory": ({ url }) => {
        calls.push(url);
        return { body: calls.length === 1 ? { configured: true, status: "NotEnabled", reason: "ApiNotInTenant", setup } : inventory() };
      },
    });
    render(<PowerPlatformPage/>);
    expect(await screen.findByText(/hasn't registered the Power Platform API yet/)).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Check again" }));

    expect(await screen.findByText("Expense approvals")).toBeInTheDocument();
    expect(calls[1]).toContain("refresh=true");
  });

  it("shows the counts, and the apps and flows that need a look", async () => {
    mockApi({ "GET /api/power-platform/inventory": { body: inventory() } });
    render(<PowerPlatformPage/>);

    expect(await screen.findByText("1 bypass consent · 1 use a high-risk connector")).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText("Filter apps"), { target: { value: "flagged" } });
    const apps = within(tableOf("Apps")).getAllByRole("row").slice(1);
    expect(apps).toHaveLength(1);
    expect(apps[0]).toHaveTextContent("Consent prompt bypassed");

    fireEvent.change(screen.getByLabelText("Filter flows"), { target: { value: "flagged" } });
    const flows = within(tableOf("Cloud flows")).getAllByRole("row").slice(1);
    expect(flows).toHaveLength(1); // a suspended flow alone is context, not a finding
    expect(flows[0]).toHaveTextContent("Sync invoices to partner API");
  });

  it("explains a partial read instead of hiding it", async () => {
    mockApi({ "GET /api/power-platform/inventory": { body: inventory({
      status: "Partial",
      summary: { environments: 60, environmentsRead: 49, environmentsSkipped: 10, apps: 2, flows: 2, consentBypassed: 1, highRiskConnector: 1, widelyShared: 1, customConnector: 0 },
      environments: [{ id: "env-x", name: "Finance", type: "Production", state: "Ready", region: "europe", hasDataverse: true, created: null, apps: 0, flows: 0, problem: "apps couldn't be read (access refused)" }],
    }) } });
    render(<PowerPlatformPage/>);

    const notice = await screen.findByRole("status");
    expect(notice).toHaveTextContent("Part of the inventory couldn't be read");
    expect(notice).toHaveTextContent("10 environments weren't read");
    expect(notice).toHaveTextContent("Finance: apps couldn't be read (access refused).");
  });

  it("shows Microsoft's message when the environment list fails", async () => {
    mockApi({ "GET /api/power-platform/inventory": { body: { configured: true, status: "Error", reason: "Power Platform API said: api-version is not supported", setup } } });
    render(<PowerPlatformPage/>);
    expect(await screen.findByText(/could not be read: Power Platform API said: api-version is not supported/)).toBeInTheDocument();
  });
});
