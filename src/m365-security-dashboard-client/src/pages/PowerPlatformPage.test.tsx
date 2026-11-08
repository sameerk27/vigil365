// @vitest-environment jsdom
import React from "react";
import { describe, it, expect, beforeEach, vi } from "vitest";
import { render, screen, fireEvent, within, waitFor } from "@testing-library/react";
import axe from "axe-core";
import { PowerPlatformPage, patternMatches } from "./PowerPlatformPage";
import { setEditionMode } from "../services/api";
import { mockApi, type MockRoute } from "../test/apiMock";
import type { AlertPolicy, TriggeredAlert } from "../services/types";

/**
 * The Power Platform page shows alerts and activity from the Purview audit log, read by
 * the server. It asks for no Microsoft sign-in of its own and calls no Microsoft API.
 */

const policy = (name: string, pattern: string, severity: string, enabled = true, category: AlertPolicy["category"] = "powerplatform"): AlertPolicy => ({
  id: name, name, enabled, category, kind: "activity", activityPattern: pattern, condition: `Activity "${pattern}" ≥ 1 in 60m`,
  metric: "", threshold: 1, severity: severity as AlertPolicy["severity"], notifyEmail: "", createdAt: "2026-11-01T00:00:00Z", triggerCount: 0,
});

const policies: AlertPolicy[] = [
  policy("Flow shared outside the organisation", "Flow shared (external:*", "high"),
  policy("Power Platform DLP policy deleted", "Power Platform DLP policy deleted*", "high"),
  policy("Agent shared", "Agent shared*", "medium", true, "copilot"),
  policy("App published", "App published*", "low", false),
  policy("Privileged role assignment", "Add member to role", "critical", true, "identity" as AlertPolicy["category"]),
];

const alert = (id: string, policyName: string, category: string, severity: string, status: TriggeredAlert["status"], triggeredAt: string): TriggeredAlert => ({
  id, policyId: policyName, policyName, category, severity, status, triggeredAt, condition: "x", metricValue: 1, threshold: 1,
});

const triggered: TriggeredAlert[] = [
  alert("a1", "Flow shared outside the organisation", "powerplatform", "high", "new", "2026-11-07T09:00:00Z"),
  alert("a2", "Agent shared", "copilot", "medium", "acknowledged", "2026-11-07T08:00:00Z"),
  alert("a3", "Power Platform DLP policy deleted", "powerplatform", "high", "resolved", "2026-11-06T08:00:00Z"),
  alert("a4", "Privileged role assignment", "identity", "critical", "new", "2026-11-07T07:00:00Z"),
];

const events = [
  { id: 1, activity: "Flow shared (external: alex@fabrikam.com)", category: "Power Automate", source: "purviewAudit", actorUpn: "j.smith@contoso.com", actorApp: null, targetName: "Sync invoices", result: "success", occurredAt: "2026-11-07T08:30:00Z" },
  { id: 2, activity: "Flow created", category: "Power Automate", source: "purviewAudit", actorUpn: "ravi@contoso.com", actorApp: null, targetName: "Daily report", result: "success", occurredAt: "2026-11-07T07:30:00Z" },
  { id: 3, activity: "App published", category: "Power Apps", source: "purviewAudit", actorUpn: "priya@contoso.com", actorApp: null, targetName: "Expense Tracker", result: "success", occurredAt: "2026-11-06T07:30:00Z" },
];

const page = (items = events, total = items.length) => ({ total, page: 1, pageSize: 200, items });

function setup(over: Record<string, MockRoute> = {}) {
  return mockApi({
    "GET /api/purview-audit/status": { body: { enabled: true, intervalMinutes: 60, highRiskConnectors: ["HTTP*"], feeds: [] } },
    "GET /api/audit-events": { body: page() },
    ...over,
  });
}

function show(props: Partial<React.ComponentProps<typeof PowerPlatformPage>> = {}) {
  const onOpenAlert = vi.fn();
  render(<PowerPlatformPage policies={policies} triggeredAlerts={triggered} onOpenAlert={onOpenAlert} {...props}/>);
  return { onOpenAlert };
}

beforeEach(() => setEditionMode("Single"));

describe("patternMatches", () => {
  it("reads * as anything and the rest literally, over the whole name", () => {
    expect(patternMatches("Flow shared (external:*", "Flow shared (external: alex@fabrikam.com)")).toBe(true);
    expect(patternMatches("Flow * (high-risk connector:*", "Flow edited (high-risk connector: HTTP)")).toBe(true);
    expect(patternMatches("Add member to role", "Add member to role")).toBe(true);
    expect(patternMatches("Add member to role", "Add member to role scope")).toBe(false);
    expect(patternMatches("App published*", "Flow created")).toBe(false);
  });
});

describe("PowerPlatformPage", () => {
  it("lists open Power Platform and agent alerts only, and opens one in the Alert Center", async () => {
    setup();
    const { onOpenAlert } = show();
    const table = (await screen.findAllByRole("table"))[0];
    const rows = within(table).getAllByRole("button", { name: /in the Alert Center$/ }); // the rows, not the sortable headers
    expect(rows.map(r => r.textContent)).toEqual([
      expect.stringContaining("Flow shared outside the organisation"),
      expect.stringContaining("Agent shared"),
    ]); // resolved and identity alerts are not here
    fireEvent.click(rows[0]);
    expect(onOpenAlert).toHaveBeenCalledWith("a1");
  });

  it("counts open alerts, the high ones, last week's events and the rules that are on", async () => {
    setup({ "GET /api/audit-events": { body: page(events, 42) } });
    show();
    await waitFor(() => expect(screen.getByText("Events in 7 days").parentElement).toHaveTextContent("42"));
    expect(screen.getByText("Open alerts").parentElement).toHaveTextContent("2");
    expect(screen.getByText("Open, high").parentElement).toHaveTextContent("1");
    expect(screen.getByText("Rules on").parentElement).toHaveTextContent("3"); // App published is off; identity isn't counted
  });

  it("shows every status when asked, including resolved alerts", async () => {
    setup();
    show();
    fireEvent.change(await screen.findByLabelText("Filter alerts by status"), { target: { value: "" } });
    expect(screen.getByRole("button", { name: /Open Power Platform DLP policy deleted/ })).toBeInTheDocument();
  });

  it("marks activity a rule alerts on, and can show only that", async () => {
    setup();
    show();
    expect(await screen.findByText("Would alert: Flow shared outside the organisation")).toBeInTheDocument();
    const activity = () => screen.getAllByRole("table")[1]; // alerts, activity, rules
    expect(within(activity()).getByText("Flow created")).toBeInTheDocument();

    fireEvent.click(screen.getByLabelText(/Only what a rule alerts on/));
    expect(within(activity()).queryByText("Flow created")).not.toBeInTheDocument();
    expect(within(activity()).queryByText("App published")).not.toBeInTheDocument(); // its rule is off
    expect(within(activity()).getByText("Flow shared (external: alex@fabrikam.com)")).toBeInTheDocument();
  });

  it("says a single event only counts towards a rule that needs several", async () => {
    setup({ "GET /api/audit-events": { body: page([{ ...events[1], activity: "Flow deleted" }]) } });
    show({ policies: [{ ...policy("Many flows deleted", "Flow deleted*", "high"), threshold: 5 }] });
    expect(await screen.findByText("Counts towards: Many flows deleted")).toBeInTheDocument();
    expect(screen.queryByText(/^Would alert/)).not.toBeInTheDocument();
    fireEvent.click(screen.getByLabelText(/Only what a rule alerts on/));
    expect(within(screen.getAllByRole("table")[1]).queryByText("Flow deleted")).not.toBeInTheDocument();
  });

  it("counts automatically resolved alerts as resolved", async () => {
    setup();
    show({ triggeredAlerts: [alert("a9", "Agent shared", "copilot", "medium", "auto_resolved", "2026-11-07T09:00:00Z")] });
    fireEvent.change(await screen.findByLabelText("Filter alerts by status"), { target: { value: "resolved" } });
    expect(screen.getByRole("button", { name: /Open Agent shared/ })).toBeInTheDocument();
  });

  it("keeps the rows when loading more fails", async () => {
    let calls = 0;
    setup({ "GET /api/audit-events": () => (calls++ === 0 ? { body: page([events[0]], 2) } : { status: 500, body: {} }) });
    show();
    fireEvent.click(await screen.findByRole("button", { name: /Load more/ }));
    expect(await screen.findByText("Couldn't load more activity. Try again.")).toBeInTheDocument();
    expect(screen.getByText("Flow shared (external: alex@fabrikam.com)")).toBeInTheDocument();
  });

  it("asks the server for every Purview product, or one when filtered", async () => {
    const api = setup();
    show();
    await screen.findByText("Flow created");
    const activityCalls = () => (fetch as ReturnType<typeof vi.fn>).mock.calls.map(c => String(c[0])).filter(u => u.includes("/api/audit-events") && u.includes("pageSize=200"));
    expect(activityCalls().at(-1)).toContain("source=purviewAudit");

    fireEvent.change(screen.getByLabelText("Filter activity by product"), { target: { value: "Power Apps" } });
    await waitFor(() => expect(activityCalls().at(-1)).toContain("source=Power+Apps"));
    expect(api.unmatched).toEqual([]);
  });

  it("loads the next page of activity when there is more", async () => {
    let calls = 0;
    setup({ "GET /api/audit-events": () => ({ body: { ...page(calls++ === 0 ? [events[0]] : [events[1]], 2), page: calls } }) });
    show();
    fireEvent.click(await screen.findByRole("button", { name: /Load more \(1 of 2\)/ }));
    expect(await screen.findByText("Flow created")).toBeInTheDocument();
  });

  it("says what to do when nothing has been logged", async () => {
    setup({ "GET /api/audit-events": { body: page([], 0) } });
    show({ triggeredAlerts: [] });
    expect(await screen.findByText(/Nothing logged in the last 7 days/)).toBeInTheDocument();
    expect(screen.getByText(/No Power Platform or agent alerts yet/)).toBeInTheDocument();
  });

  it("lists the Power Platform and agent rules with their state, not other rules", async () => {
    setup();
    show();
    const rules = (await screen.findAllByRole("table")).at(-1)!;
    expect(within(rules).getByText("App published")).toBeInTheDocument();
    expect(within(rules).queryByText("Privileged role assignment")).not.toBeInTheDocument();
    fireEvent.change(screen.getByLabelText("Filter rules by state"), { target: { value: "off" } });
    expect(within((await screen.findAllByRole("table")).at(-1)!).getAllByRole("row")).toHaveLength(2); // header + App published
  });

  it("makes no call to a Microsoft API from the browser", async () => {
    const api = setup();
    show();
    await screen.findByText("Flow created");
    expect(api.calls.every(c => c.path.startsWith("/api/"))).toBe(true);
  });

  it("has no detectable accessibility violations", async () => {
    setup();
    const { container } = render(<PowerPlatformPage policies={policies} triggeredAlerts={triggered} onOpenAlert={() => {}}/>);
    await screen.findByText("Flow created");
    const result = await axe.run(container, { rules: { "color-contrast": { enabled: false } } });
    expect(result.violations.map(v => v.id)).toEqual([]);
  });
});
