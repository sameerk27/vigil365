import { test, expect } from '@playwright/test';
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
  await page.goto('/#/clients');
  await expect(page.getByText('Client roster')).toHaveCount(0);
});

test('MSP admin sees Clients, the switcher, rollup and roster', async ({ page }) => {
  await signIn(page, {
    mode: 'Msp', role: 'Admin',
    api: {
      'GET /api/tenants/me': { current: null, tenants: [tenantA, tenantB] },
      'GET /api/tenants/rollup': [rollupRow(tenantA, { critical: 2, high: 1, medium: 0, low: 3 }), rollupRow(tenantB)],
      'GET /api/tenants': [rosterRow(tenantA), rosterRow(tenantB)],
    },
  });
  await page.goto('/#/clients');

  const switcher = page.getByLabel('Client tenant');
  await expect(switcher).toBeVisible();
  await expect(switcher.locator('option')).toContainText(['Contoso Ltd', 'Fabrikam Inc (not connected)']);

  await expect(page.getByText('Client roster')).toBeVisible();
  await expect(page.getByRole('button', { name: /Add client/ })).toBeVisible();
  // Worst posture first: Contoso (2 critical) before Fabrikam (not connected).
  const cards = page.locator('.rollup-card .rollup-name');
  await expect(cards).toHaveText(['Contoso Ltd', 'Fabrikam Inc']);
});

test('choosing a client in the switcher scopes every API call to it', async ({ page }) => {
  const { calls } = await signIn(page, {
    mode: 'Msp', role: 'Admin',
    api: {
      'GET /api/tenants/me': { current: null, tenants: [tenantA, tenantB] },
      'GET /api/tenants/rollup': [rollupRow(tenantA), rollupRow(tenantB)],
      'GET /api/tenants': [rosterRow(tenantA), rosterRow(tenantB)],
    },
  });
  await page.goto('/#/clients');
  await page.getByLabel('Client tenant').selectOption(tenantB.id);
  await page.waitForLoadState('load'); // switching reloads the app

  // Count only requests made after the switch; earlier ones correctly carry no
  // client (the no-client-selected state is MSP_V12_PLAN.md U1).
  const sent: (string | undefined)[] = [];
  page.on('request', r => { if (r.url().includes('/api/dashboard')) sent.push(r.headers()['x-vigil-tenant']); });
  await page.goto('/#/overview');
  await page.reload(); // a fresh boot with the stored selection
  await expect.poll(() => sent.length).toBeGreaterThan(0);
  expect(new Set(sent)).toEqual(new Set([tenantB.id]));
  expect(calls.length).toBeGreaterThan(0);
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
