// @vitest-environment jsdom
import React from "react";
import { describe, it, expect, vi } from "vitest";
import { render } from "@testing-library/react";
import { IncidentsPage } from "./IncidentsPage";
import type { SecurityAlert } from "../services/types";

const alert = (id: number, isResolved: boolean) => ({
  id, alertType: "MfaStatus", service: "EntraId", severity: "High", title: `Alert ${id}`,
  detectedAt: "2026-11-01T00:00:00Z", lastUpdatedAt: "2026-11-01T00:00:00Z", isResolved,
}) as unknown as SecurityAlert;

describe("IncidentsPage", () => {
  it("counts only open alerts in the tile that says unresolved", () => {
    const { container } = render(
      <IncidentsPage alerts={[alert(1, false), alert(2, true), alert(3, true)]} alertsTotal={3}
        serviceHealth={null} defenderAlerts={null} securityIncidents={null} onAlertClick={vi.fn()}/>
    );
    expect(container.textContent).toMatch(/HIGH ALERTS\s*1\s*High severity, unresolved/);
  });
});
