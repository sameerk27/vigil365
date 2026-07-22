import { useEffect, useRef } from "react";
import { showToast } from "./toast";

/**
 * Session lifetime controls.
 *
 * Vigil365 displays tenant-wide security posture — risky users, device
 * compliance, audit trails. An unattended browser left open on a SOC desk (or a
 * shared workstation) exposes all of it indefinitely, which is why enterprise
 * security tooling is expected to bound session length. Two independent limits:
 *
 *  • Idle    — no *user* activity for this long. Note the app polls the API
 *              every few minutes on its own; that is app traffic, not presence,
 *              so only real input events count here.
 *  • Absolute— total time since sign-in, regardless of how active the user is.
 *              Caps the damage from a token or workstation left unattended.
 */
export const IDLE_TIMEOUT_MIN = 30;
export const ABSOLUTE_TIMEOUT_HOURS = 12;
const WARN_BEFORE_SEC = 60;

/** Survives a page refresh (and clears on tab close) so refreshing cannot be
 *  used to reset the absolute cap. */
const SESSION_START_KEY = "vigil365-session-start";

export type ExpiryReason = "idle" | "absolute";

/** Real user-presence signals. Deliberately excludes anything the app triggers. */
const ACTIVITY_EVENTS = ["mousedown", "keydown", "touchstart", "scroll", "wheel"] as const;

export function sessionStartedAt(): number {
  const stored = Number(sessionStorage.getItem(SESSION_START_KEY));
  if (stored > 0) return stored;
  const now = Date.now();
  sessionStorage.setItem(SESSION_START_KEY, String(now));
  return now;
}

export function clearSessionStart(): void {
  sessionStorage.removeItem(SESSION_START_KEY);
}

/**
 * Pure decision function — kept separate from the effect so the policy is
 * testable without timers or a DOM.
 */
export function evaluateSession(
  now: number, lastActivity: number, startedAt: number,
  idleMin = IDLE_TIMEOUT_MIN, absoluteHours = ABSOLUTE_TIMEOUT_HOURS,
): { expired: ExpiryReason | null; secondsUntilIdleExpiry: number } {
  const idleMs = idleMin * 60_000;
  const absoluteMs = absoluteHours * 3_600_000;

  if (now - startedAt >= absoluteMs) return { expired: "absolute", secondsUntilIdleExpiry: 0 };
  if (now - lastActivity >= idleMs) return { expired: "idle", secondsUntilIdleExpiry: 0 };

  return { expired: null, secondsUntilIdleExpiry: Math.ceil((idleMs - (now - lastActivity)) / 1000) };
}

/**
 * Signs the user out after inactivity or once the absolute cap is reached.
 * Warns once shortly before the idle cut-off; any input cancels the warning.
 */
export function useSessionTimeout(onExpire: (reason: ExpiryReason) => void, enabled = true): void {
  const lastActivity = useRef(Date.now());
  const warned = useRef(false);
  const firedRef = useRef(false);
  const onExpireRef = useRef(onExpire);
  onExpireRef.current = onExpire;

  useEffect(() => {
    if (!enabled) return;

    const startedAt = sessionStartedAt();

    const markActive = () => {
      lastActivity.current = Date.now();
      warned.current = false;
    };
    ACTIVITY_EVENTS.forEach(e => window.addEventListener(e, markActive, { passive: true }));

    const timer = window.setInterval(() => {
      if (firedRef.current) return;
      const { expired, secondsUntilIdleExpiry } =
        evaluateSession(Date.now(), lastActivity.current, startedAt);

      if (expired) {
        firedRef.current = true;
        onExpireRef.current(expired);
        return;
      }
      if (secondsUntilIdleExpiry <= WARN_BEFORE_SEC && !warned.current) {
        warned.current = true;
        showToast(`Signing out in ${secondsUntilIdleExpiry}s due to inactivity — move the mouse or press a key to stay signed in.`, "error");
      }
    }, 15_000);

    return () => {
      window.clearInterval(timer);
      ACTIVITY_EVENTS.forEach(e => window.removeEventListener(e, markActive));
    };
  }, [enabled]);
}
