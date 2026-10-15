import type { Page, Route } from '@playwright/test';

/**
 * Stubs every /api call for the signed-in journeys. The client runs with
 * VITE_E2E_FAKE_AUTH=1 (MSAL bypassed); identity and role come from the mocked
 * /api/auth/me, edition mode from /api/auth/config. Anything a scenario does not
 * mock answers 404, which pages render as their normal error state — so a
 * journey only has to describe the endpoints it cares about.
 */
export type Role = 'Admin' | 'Analyst' | 'Viewer';
export type Handler = (route: Route, body: unknown) => unknown | Promise<unknown>;

export interface Scenario {
  mode: 'Single' | 'Msp';
  role: Role;
  /** "METHOD /path" or "/path" (any method) → JSON body, or a handler. */
  api?: Record<string, unknown | Handler>;
}

export const tenantA = { id: '0a000000-0000-0000-0000-00000000000a', name: 'Contoso Ltd', isActive: true, configured: true, lastCollectionStatus: 'Completed' };
export const tenantB = { id: '0b000000-0000-0000-0000-00000000000b', name: 'Fabrikam Inc', isActive: true, configured: false, lastCollectionStatus: null };

export function rosterRow(t: typeof tenantA, extra: Record<string, unknown> = {}) {
  return {
    id: t.id, name: t.name, microsoftTenantId: null, isActive: true, notes: null, createdAt: '2026-09-01T00:00:00Z',
    hasOwnCredentials: false, clientId: null, authMode: null, certificateThumbprint: null, brandName: null, brandAccentColor: null,
    consecutiveFailures: 0, nextCollectionAfter: null, credentialSource: t.configured ? 'install' : 'none', configured: t.configured,
    consentGrantedAt: null, lastCollectionAt: null, lastCollectionStatus: t.lastCollectionStatus, lastError: null, openAlerts: 0,
    ...extra,
  };
}

export function rollupRow(t: typeof tenantA, open = { critical: 0, high: 0, medium: 0, low: 0 }) {
  return {
    id: t.id, name: t.name, configured: t.configured, lastCollectionAt: null, lastCollectionStatus: t.lastCollectionStatus, lastError: null,
    open: { ...open, total: open.critical + open.high + open.medium + open.low },
    unresolvedAlerts: { critical: 0, high: 0 },
    health: !t.configured ? 'neutral' : open.critical ? 'error' : open.high ? 'warning' : 'good',
  };
}

export async function signIn(page: Page, s: Scenario): Promise<{ calls: string[] }> {
  const calls: string[] = [];
  const table: Record<string, unknown | Handler> = {
    'GET /api/auth/config': { instance: 'https://login.microsoftonline.com/', clientId: 'e2e-client', tenantId: 'e2e-tenant', redirectUri: 'http://localhost:5174', mode: s.mode },
    'GET /api/auth/me': { email: 'e2e@vigil365.test', name: 'E2E User', role: s.role },
    ...s.api,
  };

  await page.route('**/api/**', async (route) => {
    const req = route.request();
    const path = new URL(req.url()).pathname;
    const key = `${req.method()} ${path}`;
    calls.push(key);
    const hit = key in table ? table[key] : path in table ? table[path] : undefined;
    if (hit === undefined) return route.fulfill({ status: 404, json: { error: 'not mocked in e2e' } });
    let body: unknown = null;
    try { body = req.postDataJSON(); } catch { /* no body */ }
    const value = typeof hit === 'function' ? await (hit as Handler)(route, body) : hit;
    if (value === undefined) return; // handler fulfilled the route itself
    return route.fulfill({ status: 200, json: value });
  });

  return { calls };
}
