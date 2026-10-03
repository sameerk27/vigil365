// @vitest-environment jsdom
import React from "react";
import { describe, it, expect } from "vitest";
import { render, screen } from "@testing-library/react";
import { TenantAssignmentPicker } from "./TenantAssignmentPicker";

const tenants = [
  { id: "aaaa", name: "Contoso Ltd", isActive: true },
  { id: "bbbb", name: "Fabrikam Inc", isActive: true },
  { id: "cccc", name: "Northwind", isActive: false },
];

describe("TenantAssignmentPicker", () => {
  it("counts only active clients: an assignment to a deactivated one grants nothing", () => {
    render(<TenantAssignmentPicker email="bob@x.test" role="Analyst" tenants={tenants} assigned={["aaaa", "cccc"]} onChanged={() => {}} />);
    expect(screen.getByRole("button", { name: "1 of 2" })).toBeInTheDocument();
  });

  it("says All clients only when every active client is assigned", () => {
    render(<TenantAssignmentPicker email="bob@x.test" role="Analyst" tenants={tenants} assigned={["aaaa", "bbbb"]} onChanged={() => {}} />);
    expect(screen.getByRole("button", { name: "All clients" })).toBeInTheDocument();
  });

  it("says No clients when only deactivated ones are assigned", () => {
    render(<TenantAssignmentPicker email="bob@x.test" role="Analyst" tenants={tenants} assigned={["cccc"]} onChanged={() => {}} />);
    expect(screen.getByRole("button", { name: "No clients" })).toBeInTheDocument();
  });
});
