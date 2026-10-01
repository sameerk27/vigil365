// @vitest-environment jsdom
import React from "react";
import { describe, it, expect, beforeEach, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { TenantSwitcher } from "./TenantSwitcher";
import { setEditionMode, getSelectedTenantId, setSelectedTenantId } from "../services/api";
import { mockApi } from "../test/apiMock";

const A = { id: "aaaa", name: "Contoso", isActive: true, configured: true, lastCollectionStatus: "Completed" };
const B = { id: "bbbb", name: "Fabrikam", isActive: true, configured: false, lastCollectionStatus: null };

const reload = vi.fn();
beforeEach(() => {
  reload.mockReset();
  Object.defineProperty(window, "location", { configurable: true, value: { ...window.location, reload } });
});

describe("TenantSwitcher", () => {
  it("renders nothing and calls no API in a single-organisation install", async () => {
    setEditionMode("Single");
    const api = mockApi({ "/api/tenants/me": { body: { current: null, tenants: [A, B] } } });
    const { container } = render(<TenantSwitcher />);
    await new Promise(r => setTimeout(r, 20));
    expect(container).toBeEmptyDOMElement();
    expect(api.calls).toHaveLength(0);
  });

  it("renders nothing in MSP mode when the user can see fewer than two clients", async () => {
    setEditionMode("Msp");
    const api = mockApi({ "/api/tenants/me": { body: { current: "aaaa", tenants: [A] } } });
    const { container } = render(<TenantSwitcher />);
    await waitFor(() => expect(api.calls.length).toBe(1));
    expect(container).toBeEmptyDOMElement();
  });

  it("lists the permitted clients and marks unconnected ones", async () => {
    setEditionMode("Msp");
    mockApi({ "/api/tenants/me": { body: { current: null, tenants: [A, B] } } });
    render(<TenantSwitcher />);
    const select = await screen.findByLabelText("Client tenant");
    expect(screen.getByRole("option", { name: "Contoso" })).toBeInTheDocument();
    expect(screen.getByRole("option", { name: "Fabrikam (not connected)" })).toBeInTheDocument();
    expect(select).toHaveValue(""); // nothing selected yet → prompt option
  });

  it("stores the chosen client and reloads so no page keeps the old client's data", async () => {
    setEditionMode("Msp");
    mockApi({ "/api/tenants/me": { body: { current: null, tenants: [A, B] } } });
    render(<TenantSwitcher />);
    await userEvent.selectOptions(await screen.findByLabelText("Client tenant"), "bbbb");
    expect(getSelectedTenantId()).toBe("bbbb");
    expect(reload).toHaveBeenCalledTimes(1);
  });

  it("clears a stored selection the user can no longer see", async () => {
    setEditionMode("Msp");
    setSelectedTenantId("gone");
    mockApi({ "/api/tenants/me": { body: { current: null, tenants: [A, B] } } });
    render(<TenantSwitcher />);
    await waitFor(() => expect(reload).toHaveBeenCalled());
    expect(getSelectedTenantId()).toBeNull();
  });

  it("outside MSP mode the stored selection reads as empty, so no tenant header is sent", () => {
    setEditionMode("Msp");
    setSelectedTenantId("aaaa");
    expect(getSelectedTenantId()).toBe("aaaa");
    setEditionMode("Single");
    expect(getSelectedTenantId()).toBeNull();
  });
});
