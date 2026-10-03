import { test, expect, type Route } from '@playwright/test';
import { signIn, tenantA, tenantB, rosterRow, rollupRow } from './fixtures';

/** MSP_V12_PLAN.md Stage 4 (U10–U13): page kept on switch, onboarding errors, error states, non-admin view. */

const fail = async (route: Route) => { await route.fulfill({ status: 500, json: { error: 'database unavailable' } }); return undefined; };

const policy = { id: 'p1', name: 'Risky sign-ins spike', enabled: true, tenantId: null, category: 'identity', condition: 'riskySignIns >= 5', kind: 'metric', metric: 'riskySignIns', threshold: 5, severity: 'high' };

test('switching client keeps the page you were on (U10)', async ({ page }) => {
  await signIn(page, {
    mode: 'Msp', role: 'Admin',
    api: {
      'GET /api/tenants/me': { current: tenantA.id, tenants: [tenantA, tenantB] },
      'GET /api/tenants': [rosterRow(tenantA), rosterRow(tenantB)],
      'GET /api/admin/users': [], 'GET /api/admin/audit-log': [], 'GET /api/tenants/assignments': {}, 'GET /api/api-tokens': [],
    },
  });
  await page.goto('/#/users');
  await expect(page.getByText('API tokens (SIEM)')).toBeVisible();
  await page.getByLabel('Client tenant').selectOption(tenantB.id); // stores the choice and reloads
  await expect(page.locator('.hdr-client')).toHaveText('Fabrikam Inc');
  await expect(page).toHaveURL(/#\/users$/);
  await expect(page.getByText('API tokens (SIEM)')).toBeVisible();
});

test('consent window closed after a refusal says why, inline (U11)', async ({ page, context }) => {
  const consentPage = 'http://localhost:5174/e2e-consent-refused';
  await context.route(consentPage, r => r.fulfill({ contentType: 'text/html', body: '<h1>Declined</h1>' }));
  let refused = false;
  await signIn(page, {
    mode: 'Msp', role: 'Admin',
    api: {
      'GET /api/tenants/me': { current: null, tenants: [tenantA, tenantB] },
      'GET /api/tenants/rollup': [rollupRow(tenantA), rollupRow(tenantB)],
      'GET /api/tenants': [rosterRow(tenantA), rosterRow(tenantB)],
      [`GET /api/tenants/${tenantB.id}/consent-url`]: { ok: true, url: consentPage },
      [`GET /api/tenants/${tenantB.id}`]: () => rosterRow(tenantB, refused ? { lastError: 'AADSTS65004: User declined to consent' } : {}),
    },
  });
  await page.goto('/#/clients');
  await page.locator('tr', { hasText: 'Fabrikam Inc' }).getByRole('button', { name: /Connect/ }).click();
  const popupPromise = page.waitForEvent('popup');
  await page.getByRole('button', { name: /Sign in as global admin & consent/ }).click();
  const popup = await popupPromise;
  refused = true;
  await popup.close();
  await expect(page.getByText('Consent was not granted: AADSTS65004: User declined to consent')).toBeVisible({ timeout: 15_000 });
});

test('consent window closed without an answer says so (U11)', async ({ page, context }) => {
  const consentPage = 'http://localhost:5174/e2e-consent-pending';
  await context.route(consentPage, r => r.fulfill({ contentType: 'text/html', body: '<h1>Sign in</h1>' }));
  await signIn(page, {
    mode: 'Msp', role: 'Admin',
    api: {
      'GET /api/tenants/me': { current: null, tenants: [tenantA, tenantB] },
      'GET /api/tenants/rollup': [rollupRow(tenantA), rollupRow(tenantB)],
      'GET /api/tenants': [rosterRow(tenantA), rosterRow(tenantB)],
      [`GET /api/tenants/${tenantB.id}/consent-url`]: { ok: true, url: consentPage },
      [`GET /api/tenants/${tenantB.id}`]: () => rosterRow(tenantB),
    },
  });
  await page.goto('/#/clients');
  await page.locator('tr', { hasText: 'Fabrikam Inc' }).getByRole('button', { name: /Connect/ }).click();
  const popupPromise = page.waitForEvent('popup');
  await page.getByRole('button', { name: /Sign in as global admin & consent/ }).click();
  await (await popupPromise).close();
  await expect(page.getByText('The sign-in window closed before consent was granted')).toBeVisible({ timeout: 15_000 });
});

test('a failed rollup is an error with retry, not "No clients yet" (U12)', async ({ page }) => {
  await signIn(page, {
    mode: 'Msp', role: 'Admin',
    api: {
      'GET /api/tenants/me': { current: null, tenants: [tenantA, tenantB] },
      'GET /api/tenants/rollup': fail,
      'GET /api/tenants': [rosterRow(tenantA), rosterRow(tenantB)],
      'GET /api/tenants/alerts': [],
    },
  });
  await page.goto('/#/clients');
  await expect(page.getByText("Couldn't load your clients")).toBeVisible();
  await expect(page.getByText('No clients yet. Add the first one below.')).toHaveCount(0);
});

test('a Viewer sees client overrides and routing read-only (U13)', async ({ page }) => {
  await signIn(page, {
    mode: 'Msp', role: 'Viewer',
    api: {
      'GET /api/tenants/me': { current: tenantA.id, tenants: [tenantA] },
      'GET /api/alert-policies': [policy],
      'GET /api/alert-policies/tenant-overrides': [{ policyId: 'p1', enabled: false, threshold: null, notifyEmail: null }],
      // Non-Admins get whether a Teams webhook is set, never its URL.
      'GET /api/notification-routing': { exists: true, notifyMsp: true, notifyClient: false, recipientEmail: null, teamsWebhookUrl: null, hasTeamsWebhookUrl: true, hasWebhookUrl: false, minSeverity: null, lastDigestAt: null },
    },
  });
  await page.goto('/#/alertcenter');
  await page.getByRole('tab', { name: 'Policies' }).click();
  const row = page.locator('tr', { hasText: 'Risky sign-ins spike' });
  await expect(row).toContainText('Off for this client');
  await expect(row.getByRole('checkbox')).toHaveCount(0);
  await expect(row.getByRole('button')).toHaveCount(0); // toggle, Edit and Delete all need Analyst

  await page.getByRole('tab', { name: 'Notifications', exact: true }).click();
  await expect(page.getByText('Read-only — only an Admin can change routing.')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Save routing' })).toHaveCount(0);
  await expect(page.getByLabel('Client Teams webhook (optional)')).toHaveAttribute('placeholder', 'Set (only an Admin can see it)');
});

test('an Analyst can run a collection, as the server allows; a Viewer cannot', async ({ page }) => {
  let ran = false;
  await signIn(page, {
    mode: 'Single', role: 'Analyst',
    api: { 'POST /api/collector/run': () => { ran = true; return { status: 'Completed' }; } },
  });
  await page.goto('/');
  await page.getByRole('button', { name: 'Run Collection', exact: true }).click();
  await expect.poll(() => ran).toBe(true);

  const viewer = await page.context().newPage();
  await signIn(viewer, { mode: 'Single', role: 'Viewer' });
  const me = viewer.waitForResponse(r => r.url().endsWith('/api/auth/me'));
  await viewer.goto('/');
  await me; // the role is known: an Analyst would have the button by now
  await expect(viewer.getByRole('button', { name: 'Refresh data' })).toBeVisible();
  await expect(viewer.getByRole('button', { name: 'Run Collection', exact: true })).toHaveCount(0);
});
