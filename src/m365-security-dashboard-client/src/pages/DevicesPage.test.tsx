// @vitest-environment jsdom
import React from "react";
import { describe, it, expect, vi } from "vitest";
import { render } from "@testing-library/react";
import { DevicesPage } from "./DevicesPage";

const alert = (id: number, deviceName: string, isResolved: boolean) => ({
  id, alertType: "NonCompliantDevice", service: "Intune", severity: "High", title: `Non-compliant device: ${deviceName}`, deviceName,
  detectedAt: "2026-11-01T00:00:00Z", lastUpdatedAt: "2026-11-01T00:00:00Z", isResolved,
}) as never;
const devices = (totalDevices: number) => ({ nonCompliant: 0, notCheckedIn: 0, totalDevices, compliancePct: 100, nonCompliantDevices: [], osBuckets: [] }) as never;

describe("DevicesPage", () => {
  it("leaves a resolved device off the non-compliant list", () => {
    const { container } = render(<DevicesPage devices={devices(2)} alerts={[alert(1, "PC-OPEN", false), alert(2, "PC-FIXED", true)]} mdeVulnerabilities={null} onAlertClick={vi.fn()}/>);
    expect(container.textContent).toContain("PC-OPEN");
    expect(container.textContent).not.toContain("PC-FIXED");
  });

  it("does not call an empty tenant compliant", () => {
    const { container } = render(<DevicesPage devices={devices(0)} alerts={[]} mdeVulnerabilities={null} onAlertClick={vi.fn()}/>);
    expect(container.textContent).toContain("No managed devices found");
    expect(container.textContent).not.toContain("All devices are compliant");
  });
});
