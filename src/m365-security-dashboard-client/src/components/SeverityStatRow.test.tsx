// @vitest-environment jsdom
import React from "react";
import { describe, it, expect } from "vitest";
import { render } from "@testing-library/react";
import { SeverityStatRow } from "./SharedComponents";

// The count boxes above the Cloud Apps, Insider Risk, MDE and MDI alert lists.
describe("SeverityStatRow", () => {
  const boxes = (c: HTMLElement) => [...c.querySelectorAll(".stat-box")].map(b => ({
    value: b.querySelector(".stat-val")!.textContent,
    label: b.querySelector(".stat-lbl")!.textContent,
    color: (b.querySelector(".stat-val") as HTMLElement).style.color,
  }));

  it("shows high, medium, low and info, colouring only the serious non-zero counts", () => {
    const { container } = render(<SeverityStatRow counts={{ high: 2, medium: 0, low: 1, informational: 3 }}/>);
    expect(boxes(container)).toEqual([
      { value: "2", label: "High", color: "var(--status-error-text)" },
      { value: "0", label: "Medium", color: "" },
      { value: "1", label: "Low", color: "" },
      { value: "3", label: "Info", color: "" },
    ]);
  });

  it("takes the levels to show, and counts missing levels or data as zero", () => {
    const { container } = render(<SeverityStatRow counts={undefined} levels={["high", "medium", "low"]}/>);
    expect(boxes(container).map(b => `${b.label}:${b.value}`)).toEqual(["High:0", "Medium:0", "Low:0"]);
  });
});
