// @vitest-environment jsdom
import React from "react";
import { describe, it, expect, beforeEach, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ClientAlertQueue } from "./ClientAlertQueue";
import { AuthContext, setEditionMode, getSelectedTenantId } from "../services/api";
import { registerToastHandler } from "../services/toast";
import { mockApi } from "../test/apiMock";
import type { QueueItem } from "../services/tenants";

const analyst = { email: "a@x.test", name: "A", role: "Analyst" as const, isAdmin: false, canMutate: true };

const item = (id: string, tenantId: string, tenantName: string, policyName: string): QueueItem => ({
  id, tenantId, tenantName, policyName, severity: "high", category: "identity", condition: "x >= 1", metricValue: 2,
  triggeredAt: "2026-10-02T08:00:00Z", status: "new", assignedTo: null, snoozedUntil: null,
});
const contoso = item("11111111-0000-0000-0000-000000000001", "aaaa", "Contoso Ltd", "MFA coverage drop");
const fabrikam = item("11111111-0000-0000-0000-000000000002", "bbbb", "Fabrikam Inc", "Risky sign-ins spike");
const northwind = item("11111111-0000-0000-0000-000000000003", "cccc", "Northwind", "Guest invites spike");

const reload = vi.fn();
beforeEach(() => {
  reload.mockReset();
  Object.defineProperty(window, "location", { configurable: true, value: { ...window.location, hash: "", reload } });
  setEditionMode("Msp");
  registerToastHandler(() => {});
});

function renderQueue() {
  return render(<AuthContext.Provider value={analyst}><ClientAlertQueue /></AuthContext.Provider>);
}

describe("ClientAlertQueue (U6)", () => {
  it("drops a client filter once that client has no open alerts left, instead of hiding everyone else's", async () => {
    let resolved = false;
    mockApi({
      "GET /api/tenants/alerts": () => ({ body: resolved ? [fabrikam, northwind] : [contoso, fabrikam, northwind] }),
      [`POST /api/triggered-alerts/${contoso.id}/resolve`]: () => { resolved = true; return { body: { ok: true } }; },
    });
    renderQueue();
    await userEvent.selectOptions(await screen.findByLabelText("Filter by client"), "Contoso Ltd");
    expect(screen.queryByText("Risky sign-ins spike")).toBeNull();

    await userEvent.click(screen.getByRole("button", { name: "Resolve" }));
    expect(await screen.findByText("Risky sign-ins spike")).toBeInTheDocument();
    expect(screen.getByText("Guest invites spike")).toBeInTheDocument();
    expect(screen.getByLabelText("Filter by client")).toHaveValue("");
  });

  it("acting on a row someone else already resolved says so and drops the stale row", async () => {
    let resolvedElsewhere = false;
    const toasts: string[] = [];
    registerToastHandler(t => toasts.push(t.message));
    mockApi({
      "GET /api/tenants/alerts": () => ({ body: resolvedElsewhere ? [contoso] : [contoso, fabrikam] }),
      [`POST /api/triggered-alerts/${fabrikam.id}/acknowledge`]: { status: 409, body: { error: "This alert has already been resolved by bob@msp.test." } },
    });
    renderQueue();
    await screen.findByText("Risky sign-ins spike");
    resolvedElsewhere = true; // Bob resolves it while this queue is on screen

    const row = screen.getByText("Risky sign-ins spike").closest("tr")!;
    await userEvent.click(row.querySelector("button.btn-ack")!);
    await waitFor(() => expect(toasts).toContain("Fabrikam Inc: This alert has already been resolved by bob@msp.test."));
    await waitFor(() => expect(screen.queryByText("Risky sign-ins spike")).toBeNull());
  });

  it("opens an alert in its own client, on the page that shows triggered alerts", async () => {
    mockApi({ "GET /api/tenants/alerts": { body: [contoso, fabrikam] } });
    renderQueue();
    await userEvent.click(await screen.findByRole("button", { name: "Risky sign-ins spike" }));
    expect(getSelectedTenantId()).toBe("bbbb");
    expect(window.location.hash).toBe(`#/alertcenter?alert=${fabrikam.id}`);
    await waitFor(() => expect(reload).toHaveBeenCalledTimes(1));
  });
});
