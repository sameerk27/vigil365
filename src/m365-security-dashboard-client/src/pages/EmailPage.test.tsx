// @vitest-environment jsdom
import React from "react";
import { describe, it, expect, vi } from "vitest";
import { render } from "@testing-library/react";
import { EmailPage } from "./EmailPage";

const alert = (id: number, isResolved: boolean) => ({
  id, alertType: "MailFlowIssue", service: "ExchangeOnline", severity: "High", title: `Issue ${id}`,
  detectedAt: "2026-11-01T00:00:00Z", lastUpdatedAt: "2026-11-01T00:00:00Z", isResolved,
}) as never;

describe("EmailPage", () => {
  it("counts only open mail flow issues as active", () => {
    const { container } = render(<EmailPage alerts={[alert(1, false), alert(2, true), alert(3, true)]} emailProtection={null} onAlertClick={vi.fn()}/>);
    expect(container.textContent).toMatch(/Mail Flow Issues\s*1\s*Active delivery/);
  });
});
