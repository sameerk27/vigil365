import { describe, it, expect } from "vitest";
import { fmtCaControl } from "./utils";

/**
 * Conditional Access controls came straight from Graph as camelCase tokens, so
 * the policy table read like a raw API response. The fallback matters as much as
 * the lookup: Microsoft adds grant controls over time, and a control we cannot
 * name is still a control that applies — it must never render blank.
 */
describe("fmtCaControl", () => {
  it.each([
    ["mfa", "Require MFA"],
    ["compliantDevice", "Require compliant device"],
    ["domainJoinedDevice", "Require hybrid Entra joined device"],
    ["approvedApplication", "Require approved client app"],
    ["compliantApplication", "Require app protection policy"],
    ["passwordChange", "Require password change"],
    ["block", "Block access"],
  ])("names the known control %s", (token, expected) => {
    expect(fmtCaControl(token)).toBe(expected);
  });

  it("de-camelCases an unrecognised control rather than showing the raw token", () => {
    expect(fmtCaControl("someFutureControl")).toBe("Some future control");
  });

  it("leaves a single-word unknown control readable", () => {
    expect(fmtCaControl("passthrough")).toBe("Passthrough");
  });

  it("never renders empty for a missing value", () => {
    expect(fmtCaControl("")).toBe("—");
  });
});
