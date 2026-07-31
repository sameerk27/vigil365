import { describe, it, expect } from "vitest";
import { csvSafe, tzLabel, fmtUtc } from "./utils";

/**
 * The CSV guard is the frontend half of a security control: exported alert
 * titles and display names are tenant-controlled, so a value beginning =, +, -
 * or @ executes as a formula when the file is opened in Excel or Sheets. The
 * backend half is locked by CsvSanitizerTests; this locks the browser export.
 */
describe("csvSafe", () => {
  it.each([
    '=HYPERLINK("http://evil","click")',
    "+1+1",
    "-2+3",
    "@SUM(A1:A9)",
    "\tleading-tab",
    "\rleading-cr",
  ])("neutralises a leading formula trigger: %j", (dangerous) => {
    expect(csvSafe(dangerous)).toBe(`'${dangerous}`);
  });

  it.each([
    "Risky user detected",
    "user@contoso.com",       // @ only matters in first position
    "Score dropped 51-38",    // - only matters in first position
    "",
  ])("leaves a safe value untouched: %j", (safe) => {
    expect(csvSafe(safe)).toBe(safe);
  });
});

describe("fmtUtc", () => {
  it("renders an unambiguous UTC instant", () => {
    expect(fmtUtc("2026-07-31T09:05:00.000Z")).toBe("2026-07-31 09:05:00 UTC");
  });

  it("normalises an offset timestamp to UTC", () => {
    // Evidence timestamps must not depend on where the reader is sitting.
    expect(fmtUtc("2026-07-31T14:35:00+05:30")).toBe("2026-07-31 09:05:00 UTC");
  });

  it.each([undefined, null, "not a date"])("degrades safely for %j", (input) => {
    expect(fmtUtc(input as string | undefined)).toBe("–");
  });
});

describe("tzLabel", () => {
  it("never repeats the same fact twice", () => {
    // "GMT+5:30 (UTC+05:30)" was a real defect: zones whose short name is just
    // an offset must render the offset once, not alongside a restatement.
    const label = tzLabel();
    const matches = label.match(/UTC[+-]\d{2}:\d{2}|GMT[+-][\d:]+/g) ?? [];
    expect(matches.length).toBeLessThanOrEqual(1);
  });

  it("produces a non-empty label", () => {
    expect(tzLabel().length).toBeGreaterThan(0);
  });
});
