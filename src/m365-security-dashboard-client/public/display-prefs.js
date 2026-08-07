/*
 * Applies persisted display preferences to <html> before the app bundle runs,
 * so a returning user never sees a flash of the light theme or of comfortable
 * row spacing while React mounts.
 *
 * Kept as a served file rather than an inline <script> because the app's CSP is
 * script-src 'self' — an inline block would be silently blocked and this would
 * quietly do nothing.
 *
 * Keys must match the ones main.tsx writes: "m365-theme" and "m365-density".
 */
(function () {
  try {
    if (localStorage.getItem("m365-theme") === "dark") {
      document.documentElement.classList.add("dark");
    }
    document.documentElement.dataset.density =
      localStorage.getItem("m365-density") === "compact" ? "compact" : "comfortable";
  } catch (e) {
    /* storage unavailable (private mode / blocked) — defaults are fine */
  }
})();
