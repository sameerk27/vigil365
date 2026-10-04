// @vitest-environment jsdom
import React from "react";
import { describe, it, expect, beforeEach } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ReportsPage } from "./ReportsPage";
import { AuthContext, setEditionMode, setSelectedTenantId, setActiveClientName } from "../services/api";
import { registerToastHandler } from "../services/toast";
import { mockApi } from "../test/apiMock";
import type { AppRole, ReportSchedule } from "../services/types";

const as = (role: AppRole) => ({ email: "u@x.test", name: "U", role, isAdmin: role === "Admin", canMutate: role !== "Viewer" });
const contoso = "0a000000-0000-0000-0000-00000000000a";

const schedule = (extra: Partial<ReportSchedule> = {}): ReportSchedule => ({
  id: "s1", tenantId: contoso, name: "Weekly executive digest", reportType: "exec-digest", cadence: "weekly", dayOfWeek: 1, dayOfMonth: 1,
  hourUtc: 7, recipients: "ciso@contoso.com", includeCsv: true, includePdf: true, enabled: true, createdAt: "2026-09-01T00:00:00Z", ...extra,
});
const preview = { subject: "Digest", htmlBody: "", csv: "a,b", generatedAt: "2026-10-02T08:00:00Z", hasData: true, metrics: [], topAlerts: [] };

let toasts: string[] = [];
beforeEach(() => {
  toasts = [];
  registerToastHandler(t => toasts.push(t.message));
  setEditionMode("Single");
  setActiveClientName(null);
});

const renderAs = (role: AppRole) =>
  render(<AuthContext.Provider value={as(role)}><ReportsPage/></AuthContext.Provider>);

describe("ReportsPage roles (ui-9, cli-7)", () => {
  it("an Analyst sees the schedules but none of the Admin-only controls", async () => {
    mockApi({ "GET /api/report-schedules": { body: [schedule()] }, "GET /api/reports/exec-digest/preview": { body: preview } });
    renderAs("Analyst");
    const row = await screen.findByRole("row", { name: /Weekly executive digest/ });
    expect(within(row).queryByRole("button")).toBeNull();
    expect(screen.queryByRole("button", { name: /New schedule/ })).toBeNull();
  });

  it("an Admin gets the schedule controls", async () => {
    mockApi({ "GET /api/report-schedules": { body: [schedule()] }, "GET /api/reports/exec-digest/preview": { body: preview } });
    renderAs("Admin");
    const row = await screen.findByRole("row", { name: /Weekly executive digest/ });
    expect(within(row).getByTitle("Send now")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /New schedule/ })).toBeInTheDocument();
  });

  it("a Viewer is told the page needs the Analyst role: no endless skeleton, no false 'no schedules'", async () => {
    const api = mockApi();
    const { container } = renderAs("Viewer");
    expect(screen.getByText("Analyst role needed")).toBeInTheDocument();
    expect(screen.queryByText(/No report schedules have been configured/)).toBeNull();
    expect(container.querySelector(".skeleton, [class*='skeleton']")).toBeNull();
    expect(api.calls).toHaveLength(0);
  });
});

describe("ReportsPage load failures are not empty states (cli-7)", () => {
  it("a failed schedules read is an error with Retry, not 'no schedules'", async () => {
    mockApi({ "GET /api/report-schedules": { status: 500, body: {} }, "GET /api/reports/exec-digest/preview": { body: preview } });
    renderAs("Admin");
    expect(await screen.findByText("Couldn't load report schedules")).toBeInTheDocument();
    expect(screen.queryByText(/No schedules yet/)).toBeNull();
  });

  it("a failed preview is an error with Retry, not a skeleton forever", async () => {
    let fail = true;
    mockApi({
      "GET /api/report-schedules": { body: [] },
      "GET /api/reports/exec-digest/preview": () => fail ? { status: 500, body: {} } : { body: { ...preview, hasData: false } },
    });
    renderAs("Analyst");
    expect(await screen.findByText("Couldn't build the digest preview")).toBeInTheDocument();
    fail = false;
    await userEvent.click(screen.getByRole("button", { name: /Retry/ }));
    expect(await screen.findByText(/No data yet/)).toBeInTheDocument();
  });
});

describe("ReportsPage in MSP mode (qa-b2 handoff)", () => {
  beforeEach(() => { setEditionMode("Msp"); setSelectedTenantId(contoso); setActiveClientName("Contoso Ltd"); });

  it("a create the server refuses shows its reason, not a generic failure", async () => {
    mockApi({
      "GET /api/report-schedules": { body: [] },
      "GET /api/reports/exec-digest/preview": { body: preview },
      "POST /api/report-schedules": { status: 400, body: { error: "Select a client first: in MSP mode each report schedule belongs to one client." } },
    });
    renderAs("Admin");
    await userEvent.click(await screen.findByRole("button", { name: /New schedule/ }));
    await userEvent.type(screen.getByPlaceholderText(/ciso@contoso.com/), "ciso@contoso.com");
    await userEvent.click(screen.getByRole("button", { name: "Create" }));
    await waitFor(() => expect(toasts.join()).toMatch(/Select a client first/));
  });

  it("a schedule with no client is marked as never sent, and cannot be sent now", async () => {
    mockApi({ "GET /api/report-schedules": { body: [schedule({ tenantId: null })] }, "GET /api/reports/exec-digest/preview": { body: preview } });
    renderAs("Admin");
    const row = await screen.findByRole("row", { name: /Weekly executive digest/ });
    expect(within(row).getByText(/Not assigned to a client, so it is never sent/)).toBeInTheDocument();
    expect(within(row).queryByTitle("Send now")).toBeNull();
    expect(within(row).getByTitle("Delete")).toBeInTheDocument();
  });

  it("'Send now' reports the server's refusal, not 'check SMTP settings'", async () => {
    mockApi({
      "GET /api/report-schedules": { body: [schedule()] },
      "GET /api/reports/exec-digest/preview": { body: preview },
      "POST /api/report-schedules/s1/run-now": { status: 403, body: {} },
    });
    renderAs("Admin");
    await userEvent.click(within(await screen.findByRole("row", { name: /Weekly executive digest/ })).getByTitle("Send now"));
    await waitFor(() => expect(toasts.length).toBeGreaterThan(0));
    expect(toasts.join()).toMatch(/Failed — request refused \(403\)/);
    expect(toasts.join()).not.toMatch(/SMTP/);
  });

  it("the digest CSV is named after the client (tq-7)", async () => {
    mockApi({ "GET /api/report-schedules": { body: [] }, "GET /api/reports/exec-digest/preview": { body: preview } });
    const names: string[] = [];
    const click = HTMLAnchorElement.prototype.click;
    HTMLAnchorElement.prototype.click = function (this: HTMLAnchorElement) { names.push(this.download); };
    URL.createObjectURL = () => "blob:x";
    URL.revokeObjectURL = () => {};
    try {
      renderAs("Analyst");
      await userEvent.click(await screen.findByRole("button", { name: /CSV/ }));
      expect(names).toEqual(["contoso-ltd-vigil365-digest-2026-10-02.csv"]);
    } finally { HTMLAnchorElement.prototype.click = click; }
  });
});
