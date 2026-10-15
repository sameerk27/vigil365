// @vitest-environment jsdom
import React from "react";
import { describe, it, expect, beforeEach } from "vitest";
import { render, screen } from "@testing-library/react";
import { CollectionStatusBanner } from "./CollectionStatusBanner";
import { setEditionMode, setSelectedTenantId } from "../services/api";
import { mockApi } from "../test/apiMock";

/** ui-5: /health is install-wide and ignores the run's outcome; the banner must not. */

const minutesAgo = (m: number) => new Date(Date.now() - m * 60_000).toISOString();
const health = (fresh = true) => ({
  status: fresh ? "healthy" : "degraded",
  checks: { database: { ok: true }, graph: { configured: true }, collection: { startedAt: minutesAgo(3), status: "Completed", fresh, staleAfterMinutes: 30 } },
});
const run = (status: "Started" | "Completed" | "Failed", extra: Record<string, unknown> = {}) =>
  ({ id: 1, startedAt: minutesAgo(3), completedAt: minutesAgo(2), status, alertsUpserted: 0, sourceFailures: 0, error: null, ...extra });

beforeEach(() => setEditionMode("Single"));

describe("CollectionStatusBanner (ui-5)", () => {
  it("a recent but failed collection is not 'current'", async () => {
    mockApi({ "GET /health": { body: health() }, "GET /api/collector/runs": { body: [run("Failed", { error: "AADSTS7000222: client secret expired" })] } });
    render(<CollectionStatusBanner refreshKey={0}/>);
    const banner = await screen.findByRole("status");
    expect(banner).toHaveTextContent(/The last collection failed .* may be out of date\. AADSTS7000222/);
    expect(banner).not.toHaveTextContent(/current/);
    expect(banner).toHaveClass("status-error");
  });

  it("MSP: another client's recent collection does not make the selected one current", async () => {
    setEditionMode("Msp");
    setSelectedTenantId("0a000000-0000-0000-0000-00000000000a");
    const api = mockApi({ "GET /health": { body: health() }, "GET /api/collector/runs": { body: [] } });
    render(<CollectionStatusBanner refreshKey={0}/>);
    expect(await screen.findByRole("status")).toHaveTextContent("No collection has completed yet for this client.");
    // The runs are read in the selected client.
    expect(api.calls.find(c => c.path === "/api/collector/runs")!.headers.get("X-Vigil-Tenant")).toBe("0a000000-0000-0000-0000-00000000000a");
  });

  it("judges by the newest finished run while one is in progress", async () => {
    mockApi({ "GET /health": { body: health() }, "GET /api/collector/runs": { body: [run("Started", { completedAt: null }), run("Failed")] } });
    render(<CollectionStatusBanner refreshKey={0}/>);
    expect(await screen.findByRole("status")).toHaveTextContent(/The last collection failed/);
  });

  it("a completed run that could not read some sources says the data may be incomplete", async () => {
    mockApi({ "GET /health": { body: health() }, "GET /api/collector/runs": { body: [run("Completed", { sourceFailures: 2 })] } });
    render(<CollectionStatusBanner refreshKey={0}/>);
    expect(await screen.findByRole("status")).toHaveTextContent(/may be incomplete .* could not read 2 sources/);
  });

  it("unreadable runs are not 'current' either", async () => {
    mockApi({ "GET /health": { body: health() }, "GET /api/collector/runs": { status: 500, body: {} } });
    render(<CollectionStatusBanner refreshKey={0}/>);
    expect(await screen.findByRole("status")).toHaveTextContent(/could not be read, so alert data may be out of date/);
  });

  it("stale install-wide collection is stale here too", async () => {
    mockApi({ "GET /health": { body: health(false) }, "GET /api/collector/runs": { body: [run("Completed")] } });
    render(<CollectionStatusBanner refreshKey={0}/>);
    expect(await screen.findByRole("status")).toHaveTextContent(/Alert data may be stale/);
  });

  it("MSP: a client whose own last run is older than the window is stale, though another client keeps /health fresh", async () => {
    // e.g. its credentials were cleared after a success: the collector skips it
    // and writes no run, so its last Completed run only ages.
    setEditionMode("Msp");
    setSelectedTenantId("0a000000-0000-0000-0000-00000000000a");
    mockApi({ "GET /health": { body: health() }, "GET /api/collector/runs": { body: [run("Completed", { startedAt: minutesAgo(3 * 24 * 60) })] } });
    render(<CollectionStatusBanner refreshKey={0}/>);
    const banner = await screen.findByRole("status");
    expect(banner).toHaveTextContent("Alert data may be stale — the last collection for this client started 3d ago.");
    expect(banner).not.toHaveTextContent(/current/);
  });

  it("a fresh, fully successful run is current", async () => {
    mockApi({ "GET /health": { body: health() }, "GET /api/collector/runs": { body: [run("Completed")] } });
    render(<CollectionStatusBanner refreshKey={0}/>);
    const banner = await screen.findByRole("status");
    expect(banner).toHaveTextContent("Alert data is current — last collection started 3m ago.");
    expect(banner).toHaveClass("status-ok");
  });
});
