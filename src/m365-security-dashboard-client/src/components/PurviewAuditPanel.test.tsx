// @vitest-environment jsdom
import React from "react";
import { describe, it, expect, beforeEach } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import { PurviewAuditPanel } from "./PurviewAuditPanel";
import { setEditionMode } from "../services/api";
import { mockApi } from "../test/apiMock";

/** The Copilot agent and Power Platform rules depend on an optional permission: a quiet rule list must say why. */

const feed = (status: string, extra: Record<string, unknown> = {}) => ({
  feed: "powerPlatform", status, lastRunAt: null, lastSuccessAt: null, lastEventCount: 0, lastError: null, pending: false, ...extra,
});
const statusBody = (feeds: unknown[], extra: Record<string, unknown> = {}) =>
  ({ enabled: true, intervalMinutes: 60, highRiskConnectors: ["HTTP*", "Gmail"], feeds, ...extra });

beforeEach(() => setEditionMode("Single"));

describe("PurviewAuditPanel", () => {
  it("names the permission to grant when it is missing", async () => {
    mockApi({ "GET /api/purview-audit/status": { body: statusBody([feed("NeedsPermission"), { ...feed("NeedsPermission"), feed: "copilotStudio" }]) } });
    render(<PurviewAuditPanel/>);
    const banner = await screen.findByRole("alert");
    expect(banner).toHaveTextContent("need one more permission");
    expect(banner).toHaveTextContent("AuditLogsQuery.Read.All");
  });

  it("shows the last failure instead of an all-clear", async () => {
    mockApi({ "GET /api/purview-audit/status": { body: statusBody([feed("Failed", { lastError: "The audit search did not finish in time." }), feed("Ok")]) } });
    render(<PurviewAuditPanel/>);
    expect(await screen.findByRole("status")).toHaveTextContent("did not finish in time");
  });

  it("when healthy shows the cadence and the high-risk connectors, and no warning", async () => {
    mockApi({ "GET /api/purview-audit/status": { body: statusBody([feed("Ok", { lastSuccessAt: new Date().toISOString() })]) } });
    render(<PurviewAuditPanel/>);
    expect(await screen.findByText(/every 60 minutes/)).toBeInTheDocument();
    expect(screen.getByLabelText("High-risk connectors")).toHaveTextContent("HTTP*");
    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("says the rules are idle when the search is turned off", async () => {
    mockApi({ "GET /api/purview-audit/status": { body: statusBody([], { enabled: false }) } });
    render(<PurviewAuditPanel/>);
    expect(await screen.findByText(/rules are idle/)).toBeInTheDocument();
  });

  it("shows nothing when the status can't be read", async () => {
    const api = mockApi({ "GET /api/purview-audit/status": { status: 500, body: {} } });
    const { container } = render(<PurviewAuditPanel/>);
    await waitFor(() => expect(api.calls.length).toBe(1));
    expect(container).toBeEmptyDOMElement();
  });
});
