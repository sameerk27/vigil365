// @vitest-environment jsdom
import React from "react";
import { describe, it, expect, beforeEach } from "vitest";
import { render, screen, fireEvent, within } from "@testing-library/react";
import { CopilotPage } from "./CopilotPage";
import { setEditionMode } from "../services/api";
import { mockApi } from "../test/apiMock";

const usage = (extra: Record<string, unknown> = {}) => ({
  configured: true, reportRefreshDate: "2026-10-17", licensedUsers: 3, active: 1, inactive: 1, neverUsed: 1,
  seats: 60, assignedSeats: 48, unusedSeats: 12, skus: [{ partNumber: "Microsoft_365_Copilot", purchased: 60, assigned: 48 }],
  namesConcealed: false, activeDays: 30,
  apps: [{ name: "Teams", active: 1 }, { name: "Word", active: 1 }, { name: "Excel", active: 0 }],
  users: [
    { name: "Priya Shah", upn: "priya@contoso.com", lastActivity: "2026-10-16", apps: ["Teams", "Word"], status: "Active" },
    { name: "Lena Novak", upn: "lena@contoso.com", lastActivity: "2026-09-10", apps: [], status: "Inactive" },
    { name: "Marco Rossi", upn: "marco@contoso.com", lastActivity: null, apps: [], status: "Never used" },
  ],
  ...extra,
});

beforeEach(() => setEditionMode("Single"));

describe("CopilotPage", () => {
  it("shows the seat numbers and recommends reassigning idle licences", async () => {
    mockApi({ "GET /api/dashboard/copilot": { body: usage() } });
    render(<CopilotPage/>);

    expect(await screen.findByText("of 60 seats bought")).toBeInTheDocument();
    expect(screen.getByText("2 licensed users haven't used Copilot in 30 days")).toBeInTheDocument();
    expect(screen.getByText(/14 paid seats are doing nothing/)).toBeInTheDocument();
    expect(screen.queryByText(/User names are hidden/)).toBeNull();
  });

  it("'Show inactive users' filters the table to inactive users", async () => {
    mockApi({ "GET /api/dashboard/copilot": { body: usage() } });
    render(<CopilotPage/>);
    fireEvent.click(await screen.findByRole("button", { name: "Show inactive users" }));

    const rows = within(screen.getByRole("table")).getAllByRole("row").slice(1);
    expect(rows).toHaveLength(1);
    expect(rows[0]).toHaveTextContent("Lena Novak");
  });

  it("explains hidden names instead of showing codes without context", async () => {
    mockApi({ "GET /api/dashboard/copilot": { body: usage({ namesConcealed: true }) } });
    render(<CopilotPage/>);
    expect(await screen.findByRole("status")).toHaveTextContent("User names are hidden in this tenant's usage reports");
  });

  it("a tenant with no Copilot licences gets an explanation, not empty tiles", async () => {
    mockApi({ "GET /api/dashboard/copilot": { body: usage({ licensedUsers: 0, users: [], seats: null, unusedSeats: null }) } });
    render(<CopilotPage/>);
    expect(await screen.findByText(/Users appear here once they hold a Microsoft 365 Copilot licence/)).toBeInTheDocument();
  });

  it("shows the Graph error when the report can't be read", async () => {
    mockApi({ "GET /api/dashboard/copilot": { body: { configured: true, error: "Needs permission: Reports.Read.All" } } });
    render(<CopilotPage/>);
    expect(await screen.findByText(/Copilot usage could not be read: Needs permission/)).toBeInTheDocument();
  });
});
