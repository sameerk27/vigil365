// @vitest-environment jsdom
import React from "react";
import { describe, it, expect } from "vitest";
import { render, fireEvent, screen } from "@testing-library/react";
import { NetworkPage } from "./NetworkPage";

const data = {
  configured: true, total: 2, countries: 1, failures: 1, byCountry: [],
  recent: [
    { upn: "anna@x.test", app: "Teams", created: "2026-11-02T00:00:00Z", city: "Pune", country: "IN", success: true },
    { upn: "bilal@x.test", app: "Outlook", created: "2026-11-01T00:00:00Z", city: "Delhi", country: "IN", success: false },
  ],
} as never;

describe("NetworkPage sign-ins", () => {
  it("searches and filters by result", () => {
    render(<NetworkPage serviceHealth={null} signInLocations={data}/>);
    expect(screen.getByText("anna")).toBeTruthy();
    expect(screen.getByText("bilal")).toBeTruthy();

    fireEvent.change(screen.getByLabelText("Search sign-ins"), { target: { value: "pune" } });
    expect(screen.getByText("anna")).toBeTruthy();
    expect(screen.queryByText("bilal")).toBeNull();

    fireEvent.change(screen.getByLabelText("Search sign-ins"), { target: { value: "" } });
    fireEvent.change(screen.getByLabelText("Filter sign-ins by result"), { target: { value: "failed" } });
    expect(screen.queryByText("anna")).toBeNull();
    expect(screen.getByText("bilal")).toBeTruthy();
  });
});
