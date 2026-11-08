/**
 * MSAL 5 popups: Microsoft returns the sign-in result to the redirect URI inside the
 * popup, and that page has to hand it to the window that opened it (over a
 * BroadcastChannel; Microsoft's sign-in page cuts window.opener). Vigil365's redirect
 * URI is the app itself, so the app checks at start-up whether it was loaded as such a
 * popup and, if so, passes the result on instead of starting.
 *
 * MSAL marks its requests in the OAuth state: "<base64url JSON library state>|<app state>",
 * whose meta.interactionType is "popup" for a popup.
 */
export function isMsalPopupResponse(loc: Pick<Location, "hash" | "search"> = window.location): boolean {
  for (const part of [loc.hash, loc.search]) {
    if (!part || part.length < 2) continue;
    const state = new URLSearchParams(part.slice(1)).get("state");
    if (!state) continue;
    try {
      const encoded = state.split("|")[0].replace(/-/g, "+").replace(/_/g, "/");
      const json = JSON.parse(new TextDecoder().decode(Uint8Array.from(atob(encoded.padEnd(Math.ceil(encoded.length / 4) * 4, "=")), c => c.charCodeAt(0))));
      if (json?.meta?.interactionType === "popup") return true;
    } catch { /* not MSAL's state */ }
  }
  return false;
}

/** Passes the popup's sign-in result to the opener and closes the popup. */
export async function finishMsalPopup(): Promise<void> {
  const { broadcastResponseToMainFrame } = await import("@azure/msal-browser/redirect-bridge");
  await broadcastResponseToMainFrame();
}
