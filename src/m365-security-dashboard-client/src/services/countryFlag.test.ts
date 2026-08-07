import { describe, it, expect } from "vitest";
import { countryFlag } from "./utils";

/**
 * Sign-in location chips. The old hardcoded list covered ~25 countries, so a
 * sign-in from anywhere else rendered as "—" — visually identical to missing
 * data. Coverage now comes from Intl, with aliases for the names Intl does not
 * use ("USA", "UK", "UAE").
 */
describe("countryFlag", () => {
  it.each([
    ["United States", "US"],
    ["United Kingdom", "GB"],
    ["Germany", "DE"],
    ["India", "IN"],
    ["Japan", "JP"],
  ])("resolves the common case %s", (name, code) => {
    expect(countryFlag(name)).toBe(code);
  });

  it.each([
    ["Netherlands", "NL"],
    ["Poland", "PL"],
    ["Norway", "NO"],
    ["Philippines", "PH"],
    ["Kenya", "KE"],
    ["Chile", "CL"],
    ["Estonia", "EE"],
    ["Malaysia", "MY"],
  ])("resolves %s, which the old hardcoded list missed", (name, code) => {
    expect(countryFlag(name)).toBe(code);
  });

  it.each([
    ["USA", "US"],
    ["UK", "GB"],
    ["UAE", "AE"],
    ["Czech Republic", "CZ"],
    ["South Korea", "KR"],
  ])("resolves the alias %s", (name, code) => {
    expect(countryFlag(name)).toBe(code);
  });

  it("is case and whitespace insensitive", () => {
    expect(countryFlag("  gErMaNy  ")).toBe("DE");
  });

  it("passes through a country already given as a 2-letter code", () => {
    expect(countryFlag("fr")).toBe("FR");
  });

  it.each([undefined, "", "   ", "Atlantis"])("returns empty for %j so no chip renders", (input) => {
    expect(countryFlag(input as string | undefined)).toBe("");
  });
});
