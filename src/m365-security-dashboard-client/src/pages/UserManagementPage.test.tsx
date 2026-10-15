// @vitest-environment jsdom
import React from "react";
import { describe, it, expect, beforeEach, vi } from "vitest";
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { UserManagementPage } from "./UserManagementPage";
import { AuthContext, setEditionMode } from "../services/api";
import { mockApi } from "../test/apiMock";

const admin = { email: "admin@x.test", name: "Admin", role: "Admin" as const, isAdmin: true, canMutate: true };
const user = { email: "admin@x.test", displayName: "Admin", role: "Admin", createdAt: "2026-09-01T00:00:00Z", lastSeenAt: "2026-10-02T00:00:00Z" };
const entry = { id: 7, timestamp: "2026-10-02T08:00:00Z", actorEmail: "admin@x.test", action: "token.revoke", targetType: "api_token", targetId: "t1" };

const page = () => render(<AuthContext.Provider value={admin}><UserManagementPage/></AuthContext.Provider>);
const activityLog = () => screen.getByText("Activity Log").closest(".card") as HTMLElement;

beforeEach(() => setEditionMode("Single"));

describe("UserManagementPage load failures (cli-13)", () => {
  it("a failed activity-log read is an error with Retry, never 'No activity recorded yet.'", async () => {
    let fail = true;
    mockApi({
      "GET /api/tenants": { body: [] },
      "GET /api/tenants/assignments": { body: {} },
      "GET /api/api-tokens": { body: [] },
      "GET /api/admin/users": { body: [user] },
      "GET /api/admin/audit-log": () => fail ? { status: 500, body: {} } : { body: [entry] },
    });
    page();
    expect(await screen.findByText("Couldn't load the activity log")).toBeInTheDocument();
    expect(screen.queryByText("No activity recorded yet.")).toBeNull();
    // The users list loaded fine and is still shown.
    expect(screen.getByText("Users (1)")).toBeInTheDocument();

    fail = false;
    await userEvent.click(within(activityLog()).getByRole("button", { name: /Retry/ }));
    expect(await screen.findByText("token.revoke")).toBeInTheDocument();
    expect(screen.queryByText("Couldn't load the activity log")).toBeNull();
  });

  it("an unreachable API is an error for the users and the activity log, not two empty lists", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => { throw new TypeError("Failed to fetch"); }));
    page();
    expect(await screen.findByText("Couldn't load the activity log")).toBeInTheDocument();
    expect(screen.getByText("Couldn't load users")).toBeInTheDocument();
    expect(screen.queryByText("No activity recorded yet.")).toBeNull();
    expect(screen.queryByText("No users yet.")).toBeNull();
  });
});
