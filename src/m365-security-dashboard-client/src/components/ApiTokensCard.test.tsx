// @vitest-environment jsdom
import React from "react";
import { describe, it, expect, beforeEach, beforeAll, afterAll, vi } from "vitest";
import { render, screen, waitFor, fireEvent } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ApiTokensCard } from "./ApiTokensCard";
import { setEditionMode } from "../services/api";
import { registerToastHandler } from "../services/toast";
import { mockApi } from "../test/apiMock";

const day = (offset: number) => {
  const d = new Date();
  d.setDate(d.getDate() + offset);
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
};

// West of UTC, so the end of the admin's day and UTC midnight are different instants.
const tz = process.env.TZ;
beforeAll(() => { process.env.TZ = "America/Los_Angeles"; });
afterAll(() => { if (tz === undefined) delete process.env.TZ; else process.env.TZ = tz; });

let toasts: string[] = [];
beforeEach(() => {
  setEditionMode("Single");
  toasts = [];
  registerToastHandler(t => toasts.push(t.message));
});

async function openForm() {
  render(<ApiTokensCard />);
  await userEvent.click(await screen.findByRole("button", { name: /New token/ }));
  return screen.getByLabelText("Expiry date");
}

describe("ApiTokensCard list (cli-13)", () => {
  it("a failed read is an error with Retry, never 'No API tokens yet'", async () => {
    let fail = true;
    mockApi({
      "GET /api/api-tokens": () => fail ? { status: 500, body: {} }
        : { body: [{ id: "t1", name: "Leaked SIEM token", prefix: "v365_ab", scopes: "alerts:read", createdAt: "2026-10-01T00:00:00Z" }] },
    });
    render(<ApiTokensCard />);
    expect(await screen.findByText("Couldn't load API tokens")).toBeInTheDocument();
    expect(screen.queryByText("No API tokens yet.")).toBeNull();

    fail = false;
    await userEvent.click(screen.getByRole("button", { name: /Retry/ }));
    expect(await screen.findByText("Leaked SIEM token")).toBeInTheDocument();
    expect(screen.queryByText("Couldn't load API tokens")).toBeNull();
  });

  it("an unreachable API is an error too", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => { throw new TypeError("Failed to fetch"); }));
    render(<ApiTokensCard />);
    expect(await screen.findByText("Couldn't load API tokens")).toBeInTheDocument();
    expect(screen.queryByText("No API tokens yet.")).toBeNull();
  });
});

describe("ApiTokensCard expiry", () => {
  it("lasts to the end of the chosen day where the admin is, not UTC midnight", async () => {
    const api = mockApi({
      "GET /api/api-tokens": { body: [] },
      "POST /api/api-tokens": { body: { id: "t1", name: "SIEM integration", prefix: "v365_ab", scopes: "alerts:read", createdAt: "2026-10-02T00:00:00Z", token: "v365_abSECRET" } },
    });
    const input = await openForm();
    expect(input).toHaveAttribute("min", day(0));
    fireEvent.change(input, { target: { value: day(30) } });
    await userEvent.click(screen.getByRole("button", { name: "Create token" }));
    await waitFor(() => expect(api.calls.some(c => c.method === "POST")).toBe(true));
    expect((api.calls.find(c => c.method === "POST")!.body as { expiresAt: string }).expiresAt)
      .toBe(new Date(`${day(30)}T23:59:59`).toISOString());
  });

  it("refuses a day that is already over instead of issuing a token born expired", async () => {
    const api = mockApi({ "GET /api/api-tokens": { body: [] } });
    const input = await openForm();
    fireEvent.change(input, { target: { value: day(-1) } });
    await userEvent.click(screen.getByRole("button", { name: "Create token" }));
    expect(toasts).toContain("Pick an expiry date from today onwards");
    expect(api.calls.some(c => c.method === "POST")).toBe(false);
  });
});
