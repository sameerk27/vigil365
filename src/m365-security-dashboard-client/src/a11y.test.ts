// @vitest-environment jsdom
import React from "react";
import { describe, it, expect, beforeEach } from "vitest";
import { render, screen, within, act } from "@testing-library/react";
import axe from "axe-core";
import { AlertCenterPage } from "./pages/AlertCenterPage";
import { ConfirmDialog } from "./components/ConfirmDialog";
import { InlineError } from "./components/SharedComponents";
import { AuthContext, setEditionMode } from "./services/api";
import { confirmAction } from "./services/confirm";
import { registerToastHandler } from "./services/toast";
import { mockApi } from "./test/apiMock";
import type { AppRole, TriggeredAlert } from "./services/types";

/**
 * Accessibility of the real components, rendered — not markup copied from
 * them, which stayed green whatever the components did. Colour contrast needs
 * the stylesheet and a real browser, so it is left to e2e/signed-in/a11y.spec.ts,
 * which runs axe over whole rendered pages.
 */
async function violations(root: Element): Promise<string> {
  const results = await axe.run(root, {
    rules: { "color-contrast": { enabled: false }, region: { enabled: false } },
  });
  return results.violations.map(v => `${v.id}: ${v.help} (${v.nodes.map(n => n.target.join(" ")).join(", ")})`).join("\n");
}

const as = (role: AppRole) => ({ email: "u@x.test", name: "U", role, isAdmin: role === "Admin", canMutate: role !== "Viewer" });
const alert = (id: string, policyName: string): TriggeredAlert => ({
  id, policyId: "p1", policyName, severity: "high", category: "identity", condition: "x >= 1", metricValue: 2, threshold: 1,
  triggeredAt: "2026-10-02T08:00:00Z", status: "new",
});

beforeEach(() => {
  setEditionMode("Single");
  registerToastHandler(() => {});
  mockApi();
});

describe("accessibility of rendered components", () => {
  it("the axe harness flags a real violation, so a clean result means something", async () => {
    const { container } = render(React.createElement("button", { type: "button" }, React.createElement("svg", { "aria-hidden": "true" })));
    expect(await violations(container)).toMatch(/^button-name/);
  });

  it.each(["Viewer", "Analyst"] as const)("the Alert Center queue, as rendered for the %s role, has no violations", async (role) => {
    const { container } = render(React.createElement(AuthContext.Provider, { value: as(role) },
      React.createElement(AlertCenterPage, { policies: [], onChanged: async () => {}, triggeredAlerts: [alert("a1", "Privileged role assigned"), alert("a2", "MFA coverage drop")] })));
    expect(await violations(container)).toBe("");
  });

  it("each queue alert opens from a real button named after it, and headers announce their sort", () => {
    render(React.createElement(AuthContext.Provider, { value: as("Analyst") },
      React.createElement(AlertCenterPage, { policies: [], onChanged: async () => {}, triggeredAlerts: [alert("a1", "Privileged role assigned")] })));
    const open = screen.getByRole("button", { name: "Open triggered alert Privileged role assigned" });
    expect(open.tagName).toBe("BUTTON");
    // The row holds the Acknowledge/Resolve controls, so it is not itself a button.
    expect(open.closest("tr")).not.toHaveAttribute("role");
    const table = open.closest("table")!;
    expect(within(table).getByRole("columnheader", { name: /Severity/ })).toHaveAttribute("aria-sort", "ascending");
    expect(table.querySelector("caption")).toHaveTextContent(/Active alerts/);
  });

  it("the alert detail opens as a named dialog with no violations", async () => {
    render(React.createElement(AuthContext.Provider, { value: as("Analyst") },
      React.createElement(AlertCenterPage, { policies: [], onChanged: async () => {}, triggeredAlerts: [alert("a1", "Privileged role assigned")] })));
    act(() => screen.getByRole("button", { name: "Open triggered alert Privileged role assigned" }).click());
    const dialog = await screen.findByRole("dialog", { name: "Privileged role assigned" });
    expect(within(dialog).getAllByRole("button", { name: "Close" }).length).toBeGreaterThan(0);
    expect(await violations(dialog)).toBe("");
  });

  it("the confirm dialog is a labelled, described alertdialog that takes focus", async () => {
    const { container } = render(React.createElement(ConfirmDialog));
    act(() => { void confirmAction({ title: "Delete alert policy?", message: "This policy will stop evaluating.", confirmLabel: "Delete policy", danger: true }); });
    const dialog = await screen.findByRole("alertdialog", { name: "Delete alert policy?" });
    expect(dialog).toHaveAccessibleDescription("This policy will stop evaluating.");
    expect(dialog).toContainElement(document.activeElement as HTMLElement);
    expect(await violations(container)).toBe("");
  });

  it("an inline error states its problem and offers a named Retry", async () => {
    const { container } = render(React.createElement(InlineError, { title: "Couldn't load report schedules", message: "Request failed (500)", onRetry: () => {} }));
    expect(screen.getByRole("button", { name: "Retry" })).toBeInTheDocument();
    expect(await violations(container)).toBe("");
  });
});
