import { describe, it, expect } from "vitest";
import { csvSafe, tzLabel, fmtUtc, fmtDate, fmtShort, setUtcMode, severityRank, matchesQuery, filterAlerts, defenderAlertUrl, PORTAL } from "./utils";

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

// Pins the two app-wide date formats before they share one implementation.
describe("fmtDate / fmtShort", () => {
  const thisYear = new Date().getUTCFullYear();
  it("shows the year only when it is not this year, in UTC mode", () => {
    setUtcMode(true);
    try {
      expect(fmtDate("2020-03-05T14:07:00Z")).toBe("Mar 5, 2020, 02:07 PM");
      expect(fmtShort("2020-03-05T14:07:00Z")).toBe("Mar 5, 2020");
      expect(fmtDate(`${thisYear}-03-05T14:07:00Z`)).toBe("Mar 5, 02:07 PM");
      expect(fmtShort(`${thisYear}-03-05T14:07:00Z`)).toBe("Mar 5");
    } finally { setUtcMode(false); }
  });
  it("uses the UTC day, not the local one, in UTC mode", () => {
    setUtcMode(true);
    try { expect(fmtShort("2020-03-05T23:59:00Z")).toBe("Mar 5, 2020"); } finally { setUtcMode(false); }
  });
  it("renders a dash for a missing value", () => {
    expect(fmtDate(undefined)).toBe("–");
    expect(fmtShort("")).toBe("–");
  });
  it("in local mode, shows the year only for other years and the time only in fmtDate", () => {
    expect(fmtShort("2020-06-15T12:00:00Z")).toContain("2020");
    expect(fmtShort(`${thisYear}-06-15T12:00:00Z`)).not.toContain(String(thisYear));
    expect(fmtDate("2020-06-15T12:00:00Z")).toMatch(/\d{2}:\d{2}/);
    expect(fmtShort("2020-06-15T12:00:00Z")).not.toMatch(/\d{2}:\d{2}/);
  });
});

describe("severityRank", () => {
  it("orders known severities, ignoring case, with unknown and missing last", () => {
    const sorted = ["low", "unknownFutureValue", "Critical", undefined, "informational", "HIGH", "medium"]
      .sort((a, b) => severityRank(a) - severityRank(b));
    expect(sorted.slice(0, 5)).toEqual(["Critical", "HIGH", "medium", "low", "informational"]);
    expect(severityRank(null)).toBe(5);
  });
});

describe("matchesQuery / filterAlerts", () => {
  const alerts = [
    { title: "Mass download", severity: "high", category: "Exfiltration" },
    { title: "Impossible travel", severity: "Medium", category: null },
    { title: "Odd login", severity: null as unknown as string, category: "InitialAccess" },
  ];
  it("matches any field ignoring case; a blank query matches everything", () => {
    expect(matchesQuery("  DOWN ", "Mass download", null)).toBe(true);
    expect(matchesQuery("travel", undefined, "Exfiltration")).toBe(false);
    expect(matchesQuery("   ", undefined)).toBe(true);
  });
  it("filters by severity and text together, tolerating a missing severity", () => {
    expect(filterAlerts(alerts, "", "", a => [a.title]).length).toBe(3);
    expect(filterAlerts(alerts, "medium", "", a => [a.title]).map(a => a.title)).toEqual(["Impossible travel"]);
    expect(filterAlerts(alerts, "", "access", a => [a.title, a.category]).map(a => a.title)).toEqual(["Odd login"]);
    expect(filterAlerts(undefined, "high", "x", a => [a.severity])).toEqual([]);
  });
});

describe("defenderAlertUrl", () => {
  it("prefers Graph's own link, then the alert by id, then the queue", () => {
    expect(defenderAlertUrl({ alertWebUrl: "https://security.microsoft.com/alerts/x?tid=1", id: "a1" })).toBe("https://security.microsoft.com/alerts/x?tid=1");
    expect(defenderAlertUrl({ id: "da63_1" })).toBe(`${PORTAL.defenderAlerts}/da63_1`);
    expect(defenderAlertUrl({ alertWebUrl: "", id: undefined })).toBe(PORTAL.defenderAlerts);
  });
});
