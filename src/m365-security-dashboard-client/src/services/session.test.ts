import { describe, it, expect } from "vitest";
import { evaluateSession, IDLE_TIMEOUT_MIN, ABSOLUTE_TIMEOUT_HOURS } from "./session";

/**
 * Session expiry decides when an unattended browser stops showing tenant-wide
 * security data. The policy was previously verified by hand; these lock it.
 */
describe("evaluateSession", () => {
  const T0 = 1_000_000_000_000;
  const MIN = 60_000;
  const HOUR = 3_600_000;

  it("keeps a freshly active session alive", () => {
    const r = evaluateSession(T0, T0, T0);
    expect(r.expired).toBeNull();
    expect(r.secondsUntilIdleExpiry).toBe(IDLE_TIMEOUT_MIN * 60);
  });

  it("expires on idle once the threshold is reached", () => {
    expect(evaluateSession(T0 + IDLE_TIMEOUT_MIN * MIN, T0, T0).expired).toBe("idle");
  });

  it("does not expire one minute short of the idle threshold", () => {
    expect(evaluateSession(T0 + (IDLE_TIMEOUT_MIN - 1) * MIN, T0, T0).expired).toBeNull();
  });

  it("counts down toward the idle cut-off so the warning can fire", () => {
    const r = evaluateSession(T0 + (IDLE_TIMEOUT_MIN - 1) * MIN, T0, T0);
    expect(r.secondsUntilIdleExpiry).toBe(60);
  });

  it("expires on the absolute cap even while the user is active", () => {
    // Continuous activity must not extend a session past the hard ceiling.
    const now = T0 + ABSOLUTE_TIMEOUT_HOURS * HOUR;
    expect(evaluateSession(now, now, T0).expired).toBe("absolute");
  });

  it("stays alive just under the absolute cap when active", () => {
    const now = T0 + ABSOLUTE_TIMEOUT_HOURS * HOUR - MIN;
    expect(evaluateSession(now, now, T0).expired).toBeNull();
  });

  it("reports absolute rather than idle when both have elapsed", () => {
    // The stronger reason wins, so the sign-out message is accurate.
    expect(evaluateSession(T0 + (ABSOLUTE_TIMEOUT_HOURS + 1) * HOUR, T0, T0).expired).toBe("absolute");
  });

  it("honours custom limits", () => {
    expect(evaluateSession(T0 + 5 * MIN, T0, T0, 5, 24).expired).toBe("idle");
    expect(evaluateSession(T0 + 2 * HOUR, T0 + 2 * HOUR, T0, 30, 1).expired).toBe("absolute");
  });
});
