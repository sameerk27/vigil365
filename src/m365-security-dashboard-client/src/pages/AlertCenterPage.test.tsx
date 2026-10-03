// @vitest-environment jsdom
import React from "react";
import { describe, it, expect, beforeEach, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { AlertCenterPage } from "./AlertCenterPage";
import { AuthContext, setEditionMode, setSelectedTenantId, getSelectedTenantId, setActiveClientName } from "../services/api";
import { registerToastHandler } from "../services/toast";
import { mockApi } from "../test/apiMock";
import type { AlertPolicy, AppRole, TriggeredAlert } from "../services/types";

const as = (role: AppRole) => ({ email: "u@x.test", name: "U", role, isAdmin: role === "Admin", canMutate: role !== "Viewer" });
const contoso = "0a000000-0000-0000-0000-00000000000a";
const fabrikam = "0b000000-0000-0000-0000-00000000000b";

const policy = (extra: Partial<AlertPolicy> = {}): AlertPolicy => ({
  id: "p1", name: "Risky sign-ins spike", enabled: true, tenantId: null, category: "identity", condition: "riskySignIns >= 5",
  kind: "metric", metric: "riskySignIns", threshold: 5, severity: "High", notifyEmail: "", createdAt: "2026-09-01T00:00:00Z", triggerCount: 0,
  ...extra,
});
const alert = (extra: Partial<TriggeredAlert> = {}): TriggeredAlert => ({
  id: "11111111-0000-0000-0000-000000000001", policyId: "p1", policyName: "Risky sign-ins spike", severity: "high", category: "identity",
  condition: "riskySignIns >= 5", metricValue: 9, threshold: 5, triggeredAt: "2026-10-02T08:00:00Z", status: "new", ...extra,
});

const savedSettings = {
  teamsEnabled: true, teamsWebhookUrl: "https://contoso.webhook.office.com/real", emailEnabled: true, smtpHost: "smtp.office365.com",
  smtpPort: 587, smtpUseSsl: true, fromAddress: "vigil@contoso.com", defaultRecipient: "soc@contoso.com",
  webhookEnabled: false, minSeverity: "medium", digestFrequency: "weekly",
};
const routing = (extra: Record<string, unknown> = {}) => ({
  exists: true, notifyMsp: true, notifyClient: true, recipientEmail: "it@contoso.com", teamsWebhookUrl: null, hasTeamsWebhookUrl: false,
  hasWebhookUrl: false, minSeverity: null, lastDigestAt: null, ...extra,
});

let toasts: { message: string; type?: string }[] = [];
beforeEach(() => {
  toasts = [];
  registerToastHandler(t => toasts.push(t));
  setEditionMode("Single");
  setActiveClientName(null);
});

function msp(selected: string | null = contoso) {
  setEditionMode("Msp");
  setSelectedTenantId(selected);
  setActiveClientName(selected ? "Contoso Ltd" : null);
}

function renderPage(role: AppRole, props: Partial<React.ComponentProps<typeof AlertCenterPage>> = {}) {
  const onChanged = vi.fn(async () => {});
  render(
    <AuthContext.Provider value={as(role)}>
      <AlertCenterPage policies={[]} triggeredAlerts={[]} onChanged={onChanged} {...props}/>
    </AuthContext.Provider>,
  );
  return { onChanged };
}

const openTab = (name: string) => userEvent.click(screen.getByRole("tab", { name }));

describe("Notification settings (ui-3, cli-2)", () => {
  it("a failed load shows an error with Retry, never an all-off form that Save would write back", async () => {
    let fail = true;
    const api = mockApi({
      "GET /api/notification-settings": () => fail ? { status: 503, body: {} } : { body: savedSettings },
      "PUT /api/notification-settings": { body: { ok: true } },
      "GET /api/notification-log": { body: [] },
      "GET /api/notification-health": { body: { threshold: 3, channels: [], anyFailing: false } },
    });
    renderPage("Admin");
    await openTab("Notifications");

    expect(await screen.findByText("Couldn't load notification settings")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Save settings" })).toBeNull();
    expect(screen.queryByLabelText("Incoming Webhook URL")).toBeNull();

    fail = false;
    await userEvent.click(screen.getByRole("button", { name: /Retry/ }));
    expect(await screen.findByLabelText("Incoming Webhook URL")).toHaveValue(savedSettings.teamsWebhookUrl);

    await userEvent.click(screen.getByRole("button", { name: "Save settings" }));
    await waitFor(() => expect(api.calls.some(c => c.method === "PUT")).toBe(true));
    const put = api.calls.find(c => c.method === "PUT" && c.path === "/api/notification-settings")!;
    // The real settings go back, including the digest frequency the form shows.
    expect(put.body).toMatchObject({ teamsWebhookUrl: savedSettings.teamsWebhookUrl, smtpHost: "smtp.office365.com", digestFrequency: "weekly" });
  });

  it.each(["Analyst", "Viewer"] as const)("an %s is told the channels are managed by an Admin, with nothing to save", async (role) => {
    const api = mockApi({ "GET /api/notification-log": { body: [] }, "GET /api/notification-health": { body: null } });
    renderPage(role);
    await openTab("Notifications");

    expect(await screen.findByText("Managed by an Admin")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Save settings" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Send test" })).toBeNull();
    expect(api.calls.some(c => c.path === "/api/notification-settings")).toBe(false);
    // The history is Analyst-readable; a Viewer is not shown a 403 as "nothing sent".
    if (role === "Analyst") expect(await screen.findByText(/No notifications sent yet/)).toBeInTheDocument();
    else expect(screen.queryByText(/No notifications sent yet/)).toBeNull();
  });
});

describe("Client routing card (ui-8, MSP)", () => {
  it("an Admin can remove the client's saved webhook: Save sends \"\" (clear), not null (keep)", async () => {
    msp();
    const api = mockApi({
      "GET /api/notification-routing": { body: routing({ hasWebhookUrl: true }) },
      "PUT /api/notification-routing": { body: { ok: true } },
      "GET /api/notification-settings": { body: savedSettings },
      "GET /api/notification-log": { body: [] },
    });
    renderPage("Admin");
    await openTab("Notifications");

    await userEvent.click(await screen.findByRole("button", { name: "Remove webhook" }));
    await userEvent.click(screen.getByRole("button", { name: "Save routing" }));
    await waitFor(() => expect(api.calls.some(c => c.method === "PUT" && c.path === "/api/notification-routing")).toBe(true));
    expect(api.calls.find(c => c.method === "PUT" && c.path === "/api/notification-routing")!.body).toMatchObject({ webhookUrl: "" });
  });

  it("leaving the webhook field empty keeps the saved one (null)", async () => {
    msp();
    const api = mockApi({
      "GET /api/notification-routing": { body: routing({ hasWebhookUrl: true }) },
      "PUT /api/notification-routing": { body: { ok: true } },
      "GET /api/notification-settings": { body: savedSettings },
    });
    renderPage("Admin");
    await openTab("Notifications");
    await userEvent.click(await screen.findByRole("button", { name: "Save routing" }));
    await waitFor(() => expect(api.calls.some(c => c.method === "PUT")).toBe(true));
    expect(api.calls.find(c => c.method === "PUT")!.body).toMatchObject({ webhookUrl: null });
  });

  it("a client-only routing the server refuses shows the server's reason", async () => {
    msp();
    mockApi({
      "GET /api/notification-routing": { body: routing({ notifyMsp: false, recipientEmail: null }) },
      "PUT /api/notification-routing": { status: 400, body: { ok: false, message: "Alerts must go somewhere: with the MSP left out, give the client an email address, a Teams webhook or a webhook." } },
      "GET /api/notification-settings": { body: savedSettings },
    });
    renderPage("Admin");
    await openTab("Notifications");
    await userEvent.click(await screen.findByRole("button", { name: "Save routing" }));
    await waitFor(() => expect(toasts.map(t => t.message).join()).toMatch(/give the client an email address/));
  });

  it("a non-Admin sees that a client Teams webhook is set, without its URL", async () => {
    msp();
    mockApi({ "GET /api/notification-routing": { body: routing({ hasTeamsWebhookUrl: true }) }, "GET /api/notification-log": { body: [] } });
    renderPage("Analyst");
    await openTab("Notifications");
    const teams = await screen.findByLabelText("Client Teams webhook (optional)");
    expect(teams).toBeDisabled();
    expect(teams).toHaveAttribute("placeholder", "Set (only an Admin can see it)");
  });
});

describe("Per-client threshold override (ui-11, cli-12)", () => {
  it("typing 25 saves once, on blur, with 25 — not 2 on the first keystroke", async () => {
    msp();
    const puts: unknown[] = [];
    mockApi({
      "GET /api/alert-policies/tenant-overrides": { body: [] },
      "PUT /api/alert-policies/p1/tenant-override": ({ body }) => { puts.push(body); return { body: { ok: true } }; },
    });
    renderPage("Admin", { policies: [policy()] });
    await openTab("Policies");

    const input = await screen.findByLabelText("Risky sign-ins spike threshold for this client");
    await userEvent.type(input, "25");
    expect(input).toHaveValue(25);
    expect(input).toBeEnabled();
    expect(puts).toHaveLength(0);

    await userEvent.tab();
    await waitFor(() => expect(puts).toHaveLength(1));
    expect(puts[0]).toMatchObject({ threshold: 25 });
  });

  it("Enter commits too, and a refused save puts the saved value back", async () => {
    msp();
    mockApi({
      "GET /api/alert-policies/tenant-overrides": { body: [{ policyId: "p1", enabled: null, threshold: 7, notifyEmail: null }] },
      "PUT /api/alert-policies/p1/tenant-override": { status: 500, body: {} },
    });
    renderPage("Admin", { policies: [policy()] });
    await openTab("Policies");

    const input = await screen.findByLabelText("Risky sign-ins spike threshold for this client");
    await waitFor(() => expect(input).toHaveValue(7));
    await userEvent.clear(input);
    await userEvent.type(input, "30{Enter}");
    await waitFor(() => expect(toasts.map(t => t.message).join()).toMatch(/Could not save the client override/));
    expect(input).toHaveValue(7);
  });
});

describe("Policy controls follow the server's roles (ui-10, cli-8)", () => {
  it("a Viewer gets no New Policy, templates or toggle: the status is a read-only badge", async () => {
    mockApi();
    renderPage("Viewer", { policies: [policy()] });
    await openTab("Policies");
    const row = screen.getByRole("row", { name: /Risky sign-ins spike/ });
    expect(within(row).queryByRole("button")).toBeNull();
    expect(within(row).getByText("Enabled")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /New Policy/ })).toBeNull();
    expect(screen.queryByRole("button", { name: /Import/ })).toBeNull();

    await openTab("Templates");
    expect(screen.queryByRole("button", { name: /Use Template|Re-apply/ })).toBeNull();
  });

  it("MSP: an Analyst cannot change an install-wide policy, but can change the client's own", async () => {
    msp();
    mockApi({ "GET /api/alert-policies/tenant-overrides": { body: [] } });
    renderPage("Analyst", { policies: [policy(), policy({ id: "p2", name: "Contoso mailbox rule", tenantId: contoso })] });
    await openTab("Policies");

    const installWide = screen.getByRole("row", { name: /Risky sign-ins spike/ });
    expect(within(installWide).queryByRole("button")).toBeNull();
    const own = screen.getByRole("row", { name: /Contoso mailbox rule/ });
    expect(within(own).getByRole("button", { name: "Enabled" })).toBeInTheDocument();
    expect(within(own).getByRole("button", { name: "Edit" })).toBeInTheDocument();
  });

  it("MSP: an Analyst's new policy is created for the selected client (?scope=tenant)", async () => {
    msp();
    const posts: string[] = [];
    mockApi({
      "GET /api/alert-policies/tenant-overrides": { body: [] },
      "POST /api/alert-policies": ({ url }) => { posts.push(url); return { body: policy({ id: "p9", tenantId: contoso }) }; },
    });
    const { onChanged } = renderPage("Analyst");
    await openTab("Policies");
    await userEvent.click(screen.getByRole("button", { name: /New Policy/ }));
    const clientOnly = screen.getByRole("checkbox", { name: /This client only/ });
    expect(clientOnly).toBeChecked();
    expect(clientOnly).toBeDisabled();
    await userEvent.type(screen.getByLabelText("Policy Name"), "Contoso guest invites");
    await userEvent.click(screen.getByRole("button", { name: "Save Policy" }));
    await waitFor(() => expect(onChanged).toHaveBeenCalled());
    expect(posts).toHaveLength(1);
    expect(new URL(posts[0], "http://localhost").searchParams.get("scope")).toBe("tenant");
  });

  it("a refused toggle says so, with the server's reason, instead of doing nothing", async () => {
    mockApi({ "PUT /api/alert-policies/p1": { status: 403, body: { error: "Only an Admin can change install-wide policies: they apply to every client. Make a policy for this client instead." } } });
    const { onChanged } = renderPage("Analyst", { policies: [policy()] });
    await openTab("Policies");
    await userEvent.click(screen.getByRole("button", { name: "Enabled" }));
    await waitFor(() => expect(toasts.map(t => t.message).join()).toMatch(/Only an Admin can change install-wide policies/));
    expect(onChanged).not.toHaveBeenCalled();
  });
});

describe("Suppressions and baseline do not read a failure as 'nothing here' (ui-10, cli-13)", () => {
  it("a failed rules read is an error with Retry, not 'every alert is shown'", async () => {
    mockApi({ "GET /api/suppression-rules": { status: 500, body: {} } });
    renderPage("Analyst");
    await openTab("Suppressions");
    expect(await screen.findByText("Couldn't load suppression rules")).toBeInTheDocument();
    expect(screen.queryByText(/every alert that fires is shown/)).toBeNull();
  });

  it("a Viewer is told the rules need the Analyst role, and nothing is requested", async () => {
    const api = mockApi();
    renderPage("Viewer");
    await openTab("Suppressions");
    expect(screen.getByText("Analyst role needed")).toBeInTheDocument();
    expect(screen.queryByText(/every alert that fires is shown/)).toBeNull();
    await openTab("Baseline");
    expect(screen.queryByText(/No baseline yet/)).toBeNull();
    expect(api.calls.some(c => c.path === "/api/suppression-rules" || c.path === "/api/baseline")).toBe(false);
  });

  it("a failed baseline read is an error, not 'No baseline yet — run a collection first'", async () => {
    mockApi({ "GET /api/baseline": { status: 500, body: {} } });
    renderPage("Analyst");
    await openTab("Baseline");
    expect(await screen.findByText("Couldn't load the baseline")).toBeInTheDocument();
    expect(screen.queryByText(/No baseline yet/)).toBeNull();
  });
});

describe("Alerts queue", () => {
  it("filters hiding every alert are not reported as 'No alerts triggered yet'", async () => {
    mockApi();
    renderPage("Analyst", { triggeredAlerts: [alert({ status: "acknowledged" })] });
    expect(screen.getByText(/No alerts match these filters — 1 alert is hidden by them/)).toBeInTheDocument();
    expect(screen.queryByText(/No alerts triggered yet/)).toBeNull();
  });
});

describe("Acting on an alert resolved meanwhile (409)", () => {
  const resolvedBy = { status: 409, body: { error: "This alert has already been resolved by bob@x.test." } };
  const a1 = alert({ id: "11111111-0000-0000-0000-000000000001", policyName: "Risky sign-ins spike" });
  const a2 = alert({ id: "11111111-0000-0000-0000-000000000002", policyName: "Mass download" });

  it("bulk: alerts resolved by someone else are reported as resolved, not 'still open'", async () => {
    mockApi({
      [`POST /api/triggered-alerts/${a1.id}/acknowledge`]: { body: { ...a1, status: "acknowledged" } },
      [`POST /api/triggered-alerts/${a2.id}/acknowledge`]: resolvedBy,
    });
    const { onChanged } = renderPage("Analyst", { triggeredAlerts: [a1, a2] });
    await userEvent.click(screen.getByLabelText("Select all alerts on this page"));
    const bar = document.querySelector(".bulk-bar") as HTMLElement;
    await userEvent.click(within(bar).getByRole("button", { name: "Acknowledge" }));

    await waitFor(() => expect(toasts).toHaveLength(1));
    expect(toasts[0].message).toBe("Acknowledged 1 alert. 1 was already resolved.");
    expect(toasts[0].message).not.toMatch(/still open|Failed/);
    expect(toasts[0].type).toBe("info");
    expect(onChanged).toHaveBeenCalled();
    expect(screen.queryByText("2 selected")).toBeNull(); // nothing left to retry
  });

  it("single: Resolve on an alert resolved meanwhile says so and refreshes the list", async () => {
    mockApi({ [`POST /api/triggered-alerts/${a1.id}/resolve`]: resolvedBy });
    const { onChanged } = renderPage("Analyst", { triggeredAlerts: [a1] });
    await userEvent.click(screen.getByRole("button", { name: "Resolve" }));

    await waitFor(() => expect(toasts.map(t => t.message)).toContain("This alert has already been resolved by bob@x.test."));
    expect(onChanged).toHaveBeenCalled();
  });

  it("single: a failed Acknowledge is reported, not silently ignored", async () => {
    mockApi({ [`POST /api/triggered-alerts/${a1.id}/acknowledge`]: { status: 500, body: {} } });
    const { onChanged } = renderPage("Analyst", { triggeredAlerts: [a1] });
    await userEvent.click(screen.getByRole("button", { name: "Acknowledge" }));

    await waitFor(() => expect(toasts).toEqual([{ message: "Could not acknowledge the alert", type: "error", action: undefined }]));
    expect(onChanged).toHaveBeenCalled();
  });
});

describe("Notification permalinks in MSP mode (cli-5)", () => {
  it("a link to another client's alert switches to that client and reopens the link there", async () => {
    msp(contoso);
    const reload = vi.fn();
    Object.defineProperty(window, "location", { configurable: true, value: { ...window.location, hash: "", reload } });
    const id = "22222222-0000-0000-0000-000000000002";
    mockApi({
      "GET /api/tenants/alerts": { body: [{ id, tenantId: fabrikam, tenantName: "Fabrikam Inc", policyName: "Guest invites spike", severity: "high", category: "identity", condition: "x", metricValue: 2, triggeredAt: "2026-10-02T08:00:00Z", status: "new", assignedTo: null, snoozedUntil: null }] },
      "GET /api/alert-policies/tenant-overrides": { body: [] },
    });
    renderPage("Analyst", { triggeredAlerts: [alert()], deepLinkAlertId: id });

    await waitFor(() => expect(reload).toHaveBeenCalled());
    expect(getSelectedTenantId()).toBe(fabrikam);
    expect(window.location.hash).toBe(`#/alertcenter?alert=${id}`);
    expect(toasts.map(t => t.message).join()).not.toMatch(/no longer in the active queue/);
  });

  it("a link to an alert open in no client says it is gone, without naming the selected client", async () => {
    msp(contoso);
    mockApi({ "GET /api/tenants/alerts": { body: [] }, "GET /api/alert-policies/tenant-overrides": { body: [] } });
    renderPage("Analyst", { triggeredAlerts: [alert()], deepLinkAlertId: "33333333-0000-0000-0000-000000000003" });
    await waitFor(() => expect(toasts.map(t => t.message).join()).toMatch(/no longer in the active queue/));
    expect(toasts[0].message.startsWith("Contoso Ltd:")).toBe(false);
    expect(getSelectedTenantId()).toBe(contoso);
  });
});
