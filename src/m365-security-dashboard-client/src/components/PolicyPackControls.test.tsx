// @vitest-environment jsdom
import React from "react";
import { describe, it, expect } from "vitest";
import { render, screen } from "@testing-library/react";
import { PolicyPackControls } from "./PolicyPackControls";
import { AuthContext, setEditionMode } from "../services/api";
import type { AppRole } from "../services/types";

const as = (role: AppRole) => ({ email: "u@x.test", name: "U", role, isAdmin: role === "Admin", canMutate: role !== "Viewer" });

function renderAs(role: AppRole) {
  return render(<AuthContext.Provider value={as(role)}><PolicyPackControls onChanged={() => {}} /></AuthContext.Provider>);
}

describe("PolicyPackControls", () => {
  it("MSP: an Analyst cannot import, since a pack's policies would run for every client", () => {
    setEditionMode("Msp");
    renderAs("Analyst");
    const importBtn = screen.getByRole("button", { name: /Import/ });
    expect(importBtn).toBeDisabled();
    expect(importBtn).toHaveAttribute("title", expect.stringMatching(/Only an Admin/));
    expect(screen.getByRole("button", { name: /Export/ })).toBeEnabled();
  });

  it("MSP Admins, and Analysts on a single-organisation install, can import", () => {
    setEditionMode("Msp");
    const admin = renderAs("Admin");
    expect(screen.getByRole("button", { name: /Import/ })).toBeEnabled();
    admin.unmount();

    setEditionMode("Single");
    renderAs("Analyst");
    expect(screen.getByRole("button", { name: /Import/ })).toBeEnabled();
  });
});
