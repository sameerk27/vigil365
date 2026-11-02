import { describe, it, expect } from "vitest";
import { isMsalPopupResponse } from "./msalPopup";

// MSAL's state: base64url(JSON library state) "|" app state.
const state = (interactionType: string) =>
  btoa(JSON.stringify({ id: "abc", meta: { interactionType } })).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "") + "|app";

describe("isMsalPopupResponse", () => {
  it("is true for a popup sign-in coming back in the hash or the query", () => {
    expect(isMsalPopupResponse({ hash: `#code=x&state=${state("popup")}`, search: "" })).toBe(true);
    expect(isMsalPopupResponse({ hash: "", search: `?code=x&state=${state("popup")}` })).toBe(true);
    expect(isMsalPopupResponse({ hash: `#error=access_denied&state=${state("popup")}`, search: "" })).toBe(true);
  });

  it("is false for the app's own redirect sign-in, so that keeps working as before", () => {
    expect(isMsalPopupResponse({ hash: `#code=x&state=${state("redirect")}`, search: "" })).toBe(false);
  });

  it("is false for an ordinary page and for state that isn't MSAL's", () => {
    expect(isMsalPopupResponse({ hash: "#/powerplatform", search: "" })).toBe(false);
    expect(isMsalPopupResponse({ hash: "", search: "?state=not-base64-json" })).toBe(false);
    expect(isMsalPopupResponse({ hash: "", search: "" })).toBe(false);
  });
});
