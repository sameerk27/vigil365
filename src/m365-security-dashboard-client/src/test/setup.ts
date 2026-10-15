import "@testing-library/jest-dom/vitest";
import { afterEach } from "vitest";

// Component tests run in jsdom (per-file `@vitest-environment jsdom`); pure
// function tests stay in node. Only touch the DOM when there is one.
afterEach(async () => {
  if (typeof document !== "undefined") {
    const { cleanup } = await import("@testing-library/react");
    cleanup();
    try { localStorage.clear(); } catch { /* storage unavailable */ }
  }
});
