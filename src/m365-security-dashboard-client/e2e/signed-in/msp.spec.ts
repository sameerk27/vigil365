import { test, expect, type Route } from '@playwright/test';
import { signIn, tenantA, tenantB, rosterRow, rollupRow } from './fixtures';

/**
 * Signed-in journeys for the MSP surface (MSP_V12_PLAN.md T2). These are the
 * first automated tests that reach any authenticated screen.
 */

test('single-organisation install shows no MSP surface', async ({ page }) => {
  const { calls } = await signIn(page, { mode: 'Single', role: 'Admin' });
  await page.goto('/');
  await expect(page.getByRole('button', { name: /Overview/ }).first()).toBeVisible();
  await expect(page.getByRole('button', { name: /^Clients/ })).toHaveCount(0);
  await expect(page.getByLabel('Client tenant')).toHaveCount(0);
  expect(calls.filter(c => c.includes('/api/tenants'))).toEqual([]); // nothing MSP is even requested
});

test('a single-organisation install cannot be deep-linked into the Clients page', async ({ page }) => {
  await signIn(page, { mode: 'Single', role: 'Admin' });
  const me = page.waitForResponse(r => r.url().endsWith('/api/auth/me'));
  await page.goto('/#/clients');
  await me; // the role is known: an Admin would be shown the roster
  // Pages render only once the first data load has settled (every panel 404s here).
  await expect(page.getByText('Failed to load dashboard data. Is the API running?')).toBeVisible();
  await expect(page.getByText('Client roster')).toHaveCount(0);
});

test('a stored client the user may no longer pick is dropped, not a lock-out (U1)', async ({ page }) => {
  // Deactivated or purged since, or the user's access to it was revoked.
  const revoked = '0c000000-0000-0000-0000-00000000000c';
  await page.addInitScript(id => { if (!sessionStorage.getItem('e2e-seeded')) { sessionStorage.setItem('e2e-seeded', '1'); localStorage.setItem('vigil365-tenant', id); } }, revoked);
  // What TenantResolutionMiddleware does: any call naming a client the user may
  // not select is refused with 403 before the endpoint runs.
  const refuseRevoked = (reply: unknown) => async (route: Route) => {
    if (route.request().headers()['x-vigil-tenant'] !== revoked) return reply;
    await route.fulfill({ status: 403, json: { ok: false, message: 'You do not have access to that tenant.' } });
    return undefined;
  };
  await signIn(page, {
    mode: 'Msp', role: 'Admin',
    api: {
      'GET /api/auth/me': refuseRevoked({ email: 'e2e@vigil365.test', name: 'E2E User', role: 'Admin' }),
      'GET /api/tenants/me': refuseRevoked({ current: null, tenants: [tenantA, tenantB] }),
      'GET /api/tenants/rollup': refuseRevoked([rollupRow(tenantA), rollupRow(tenantB)]),
      'GET /api/tenants': refuseRevoked([rosterRow(tenantA), rosterRow(tenantB)]),
    },
  });
  await page.goto('/');

  await expect(page.getByRole('heading', { name: 'Choose a client' })).toBeVisible();
  await expect(page.getByRole('button', { name: /Add client/ })).toBeVisible(); // still signed in as an Admin
  expect(await page.evaluate(() => localStorage.getItem('vigil365-tenant'))).toBeNull();
});

test('MSP admin with several clients and none chosen lands on Choose a client, with no failing calls (U1)', async ({ page }) => {
  const { calls } = await signIn(page, {
    mode: 'Msp', role: 'Admin',
    api: {
      'GET /api/tenants/me': { current: null, tenants: [tenantA, tenantB] },
      'GET /api/tenants/rollup': [rollupRow(tenantA, { critical: 2, high: 1, medium: 0, low: 3 }), rollupRow(tenantB)],
      'GET /api/tenants': [rosterRow(tenantA), rosterRow(tenantB)],
    },
  });
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'Choose a client' })).toBeVisible();
  await expect(page.getByText('Client roster')).toBeVisible();
  await expect(page.getByRole('button', { name: /Add client/ })).toBeVisible();
  // Worst posture first: Contoso (2 critical) before Fabrikam (not connected).
  await expect(page.locator('.rollup-card .rollup-name')).toHaveText(['Contoso Ltd', 'Fabrikam Inc']);
  // Nothing tenant-scoped was requested — those would all fail closed with 400.
  expect(calls.filter(c => !c.includes('/api/auth/') && !c.includes('/api/tenants'))).toEqual([]);
});

test('picking a client opens its dashboard, names it, and scopes every call to it (U1, U5)', async ({ page }) => {
  const meReply = () => ({ current: null, tenants: [tenantA, tenantB] });
  await signIn(page, {
    mode: 'Msp', role: 'Admin',
    api: {
      'GET /api/tenants/me': meReply,
      'GET /api/tenants/rollup': [rollupRow(tenantA), rollupRow(tenantB)],
      'GET /api/tenants': [rosterRow(tenantA), rosterRow(tenantB)],
    },
  });
  await page.goto('/');
  const sent: (string | undefined)[] = [];
  page.on('request', r => { if (r.url().includes('/api/dashboard')) sent.push(r.headers()['x-vigil-tenant']); });

  await page.locator('.rollup-card', { hasText: 'Fabrikam Inc' }).click(); // stores choice + reloads
  await expect(page.locator('.hdr-client')).toHaveText('Fabrikam Inc');
  const switcher = page.getByLabel('Client tenant');
  await expect(switcher).toHaveValue(tenantB.id);
  await expect(switcher.locator('option')).toContainText(['Contoso Ltd', 'Fabrikam Inc (not connected)']);

  await expect.poll(() => sent.length).toBeGreaterThan(0);
  expect(new Set(sent)).toEqual(new Set([tenantB.id]));
});

test('a Viewer with one assigned client gets no switcher and no roster', async ({ page }) => {
  await signIn(page, {
    mode: 'Msp', role: 'Viewer',
    api: {
      'GET /api/tenants/me': { current: tenantA.id, tenants: [tenantA] },
      'GET /api/tenants/rollup': [rollupRow(tenantA)],
    },
  });
  await page.goto('/#/clients');
  await expect(page.locator('.rollup-card')).toHaveCount(1);
  await expect(page.getByText('Client roster')).toHaveCount(0);
  await expect(page.getByRole('button', { name: /Add client/ })).toHaveCount(0);
  await expect(page.getByLabel('Client tenant')).toHaveCount(0);
});

test('one-go onboarding: consent popup, then automatic connection test', async ({ page, context }) => {
  let consented = false;
  const consentPage = 'http://localhost:5174/e2e-consent-done';
  await context.route(consentPage, r => r.fulfill({ contentType: 'text/html', body: '<h1>Consent granted</h1>' }));

  await signIn(page, {
    mode: 'Msp', role: 'Admin',
    api: {
      'GET /api/tenants/me': { current: null, tenants: [tenantA, tenantB] },
      'GET /api/tenants/rollup': [rollupRow(tenantA), rollupRow(tenantB)],
      'GET /api/tenants': [rosterRow(tenantA), rosterRow(tenantB)],
      [`GET /api/tenants/${tenantB.id}/consent-url`]: () => { consented = true; return { ok: true, url: consentPage }; },
      [`GET /api/tenants/${tenantB.id}`]: () => rosterRow(tenantB, consented ? { consentGrantedAt: '2026-10-02T10:00:00Z', microsoftTenantId: '0f000000-0000-0000-0000-00000000000f' } : {}),
      [`POST /api/tenants/${tenantB.id}/test`]: { ok: true, microsoftTenantId: '0f000000-0000-0000-0000-00000000000f', displayName: 'Fabrikam Inc' },
    },
  });
  await page.goto('/#/clients');

  await page.locator('tr', { hasText: 'Fabrikam Inc' }).getByRole('button', { name: /Connect/ }).click();
  const popupPromise = page.waitForEvent('popup');
  await page.getByRole('button', { name: /Sign in as global admin & consent/ }).click();
  const popup = await popupPromise;
  await expect(popup).toHaveURL(consentPage);

  await expect(page.getByText('Connected to Fabrikam Inc.')).toBeVisible({ timeout: 15_000 });
  await expect.poll(() => popup.isClosed()).toBe(true);
});

test('onboarding warns before consent when the app registration cannot accept clients (M2)', async ({ page }) => {
  await signIn(page, {
    mode: 'Msp', role: 'Admin',
    api: {
      'GET /api/tenants/me': { current: null, tenants: [tenantA, tenantB] },
      'GET /api/tenants/rollup': [rollupRow(tenantA), rollupRow(tenantB)],
      'GET /api/tenants': [rosterRow(tenantA), rosterRow(tenantB)],
      'GET /api/setup/msp-app-status': {
        readable: true, multiTenant: false, consentRedirectRegistered: false, missingPermissions: ['SecurityAlert.Read.All'],
        expectedRedirect: 'http://localhost:5174/consented', reason: null, ready: false,
      },
    },
  });
  await page.goto('/');
  await page.locator('tr', { hasText: 'Fabrikam Inc' }).getByRole('button', { name: /Connect/ }).click();
  const warning = page.getByRole('alert').filter({ hasText: 'Client consent will fail' });
  await expect(warning).toContainText('single-tenant');
  await expect(warning).toContainText('http://localhost:5174/consented');
  await expect(warning).toContainText('SecurityAlert.Read.All');
});
