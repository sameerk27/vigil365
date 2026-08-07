import { describe, it, expect } from "vitest";
import { selectTrendWindow } from "./trendWindow";

type Snap = { capturedAt: string };
const at = (s: Snap) => s.capturedAt;

/** Snapshots `daysAgo` before a fixed "now", oldest first. */
function snaps(...daysAgo: number[]): Snap[] {
  const now = new Date("2026-08-01T12:00:00Z").getTime();
  return daysAgo
    .slice()
    .sort((a, b) => b - a)
    .map(d => ({ capturedAt: new Date(now - d * 86_400_000).toISOString() }));
}

describe("selectTrendWindow", () => {
  it("returns only snapshots inside the window", () => {
    const r = selectTrendWindow(snaps(30, 20, 5, 1, 0), 7, at);
    expect(r.items).toHaveLength(3); // 5, 1, 0 days ago
    expect(r.usingFallback).toBe(false);
  });

  it("flags the fallback when the window is too sparse to plot", () => {
    // Only "today" falls inside 7 days; a single point cannot form a line.
    const r = selectTrendWindow(snaps(90, 60, 0), 7, at);
    expect(r.usingFallback).toBe(true);
    expect(r.items).toHaveLength(2);
  });

  it("does not flag a fallback when the window has exactly two points", () => {
    const r = selectTrendWindow(snaps(30, 3, 1), 7, at);
    expect(r.items).toHaveLength(2);
    expect(r.usingFallback).toBe(false);
  });

  it("never claims a fallback it could not perform", () => {
    // A single snapshot overall: there is nothing to fall back to, so the flag
    // must stay false rather than implying data exists outside the window. The
    // lone point is still returned — the window is measured from the newest
    // snapshot, so it is in range — and LineChart renders its own
    // "collecting data" state below two points.
    const r = selectTrendWindow(snaps(90), 7, at);
    expect(r.usingFallback).toBe(false);
    expect(r.items).toHaveLength(1);
  });

  it("handles no snapshots", () => {
    expect(selectTrendWindow<Snap>([], 30, at)).toEqual({ items: [], usingFallback: false });
  });

  it("measures the window from the newest snapshot, not wall-clock now", () => {
    // Collection may have been down for weeks; the chart should still show the
    // last N days of data that exists rather than an empty window.
    const r = selectTrendWindow(snaps(100, 98, 96), 7, at);
    expect(r.items).toHaveLength(3);
    expect(r.usingFallback).toBe(false);
  });

  it("a wide window keeps everything", () => {
    const r = selectTrendWindow(snaps(60, 30, 10, 0), 90, at);
    expect(r.items).toHaveLength(4);
    expect(r.usingFallback).toBe(false);
  });
});
