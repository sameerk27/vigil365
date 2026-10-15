import { test, expect } from '@playwright/test';
import { signIn, tenantA, tenantB, rosterRow, rollupRow } from './fixtures';

/** MSP_V12_PLAN.md Stage 4 (U6–U9). */

const queue = [
  { id: '11111111-0000-0000-0000-000000000001', tenantId: tenantB.id, tenantName: 'Fabrikam Inc', policyName: 'Risky sign-ins spike', severity: 'critical', category: 'identity', condition: 'riskySignIns >= 5', metricValue: 9, triggeredAt: '2026-10-02T08:00:00Z', status: 'new', assignedTo: null, snoozedUntil: null },
  { id: '11111111-0000-0000-0000-000000000002', tenantId: tenantA.id, tenantName: 'Contoso Ltd', policyName: 'MFA coverage drop', severity: 'medium', category: 'identity', condition: 'mfa < 90%', metricValue: 84, triggeredAt: '2026-10-02T07:00:00Z', status: 'acknowledged', assignedTo: null, snoozedUntil: null },
];

const mspAdmin = (extra: Record<string, unknown> = {}) => ({
  mode: 'Msp' as const, role: 'Admin' as const,
  api: {
    'GET /api/tenants/me': { current: null, tenants: [tenantA, tenantB] },
    'GET /api/tenants/rollup': [rollupRow(tenantA), rollupRow(tenantB)],
    'GET /api/tenants': [rosterRow(tenantA), rosterRow(tenantB)],
    'GET /api/tenants/alerts': queue,
    ...extra,
  },
});

test('cross-client queue lists every client and acts in the alert\'s own client (U6)', async ({ page }) => {
  let ackTenant: string | undefined;
  await signIn(page, mspAdmin({
    // Contoso is selected, so the alert's own client has to win over the selection.
    'GET /api/tenants/me': { current: tenantA.id, tenants: [tenantA, tenantB] },
    [`POST /api/triggered-alerts/${queue[0].id}/acknowledge`]: (route: any) => { ackTenant = route.request().headers()['x-vigil-tenant']; return { ok: true }; },
  }));
  await page.goto('/#/clients');
  await expect(page.locator('.hdr-client')).toHaveText('Contoso Ltd');

  const table = page.locator('.queue-tbl');
  await expect(table.locator('tbody tr')).toHaveCount(2);
  await expect(table.locator('tbody tr').first()).toContainText('Fabrikam Inc'); // critical first

  await table.locator('tr', { hasText: 'Risky sign-ins spike' }).getByRole('button', { name: 'Acknowledge' }).click();
  await expect.poll(() => ackTenant).toBe(tenantB.id);                  // the alert's client, not the selected one
  await expect(page.getByText('Fabrikam Inc: Risky sign-ins spike acknowledged')).toBeVisible();

  await page.getByLabel('Filter by client').selectOption('Contoso Ltd');
  await expect(table.locator('tbody tr')).toHaveCount(1);
});

test('opening an alert from the queue switches to its client and opens it there (U6)', async ({ page }) => {
  const triggered = { id: queue[0].id, policyId: 'p1', policyName: 'Risky sign-ins spike', severity: 'critical', category: 'identity', condition: 'riskySignIns >= 5', metricValue: 9, threshold: 5, triggeredAt: '2026-10-02T08:00:00Z', status: 'new' };
  await signIn(page, mspAdmin({
    // Only Fabrikam has this alert: it opens only once the dashboard is scoped to it.
    'GET /api/triggered-alerts': (route: any) => route.request().headers()['x-vigil-tenant'] === tenantB.id ? [triggered] : [],
  }));
  await page.goto('/');
  await page.locator('.queue-tbl').getByRole('button', { name: 'Risky sign-ins spike' }).click(); // stores the client + reloads

  await expect(page.locator('.hdr-client')).toHaveText('Fabrikam Inc');
  await expect(page.locator('.detail-modal .dm-title')).toHaveText('Risky sign-ins spike');
});

test('a Viewer sees the queue but cannot act on it (U6, U13)', async ({ page }) => {
  await signIn(page, { ...mspAdmin(), role: 'Viewer' });
  await page.goto('/');
  await expect(page.locator('.queue-tbl tbody tr')).toHaveCount(2);
  await expect(page.getByRole('button', { name: 'Acknowledge', exact: true })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Resolve', exact: true })).toHaveCount(0);
});

test('API tokens: create one restricted to a client, shown once (U7)', async ({ page }) => {
  let posted: any;
  await signIn(page, {
    mode: 'Msp', role: 'Admin',
    api: {
      'GET /api/tenants/me': { current: tenantA.id, tenants: [tenantA] },
      'GET /api/tenants': [rosterRow(tenantA), rosterRow(tenantB)],
      'GET /api/admin/users': [],
      'GET /api/admin/audit-log': [],
      'GET /api/tenants/assignments': {},
      'GET /api/api-tokens': [],
      'POST /api/api-tokens': (_r: any, body: any) => { posted = body; return { id: 't1', name: body.name, prefix: 'v365_ab', scopes: body.scopes, createdAt: '2026-10-02T00:00:00Z', tenantId: body.tenantId, token: 'v365_abSECRETVALUE' }; },
    },
  });
  await page.goto('/#/users');
  await page.getByRole('button', { name: /New token/ }).click();
  await page.getByLabel('Restrict to client').selectOption(tenantB.id);
  await page.getByRole('button', { name: 'Create token' }).click();

  await expect(page.getByText('v365_abSECRETVALUE')).toBeVisible();
  expect(posted.tenantId).toBe(tenantB.id);
  expect(posted.scopes).toBe('alerts:read,health:read');
});

test('database size warning shows for Admins only (U8)', async ({ page }) => {
  const health = { checks: { database: { ok: true, sizeBytes: 9 * 1024 ** 3, sizeWarning: true, sizeWarningBytes: 8 * 1024 ** 3 } } };
  await page.route('**/health', r => r.fulfill({ json: health }));
  await signIn(page, { mode: 'Single', role: 'Admin' });
  await page.goto('/');
  // /health does not say which database engine this is, so the advice must hold
  // for any of them: the SQL Server Express limit is a condition, not a verdict.
  const banner = page.getByRole('alert').filter({ hasText: 'Shorten retention' });
  await expect(banner).toContainText('9.0 GB');
  await expect(banner).toContainText('On SQL Server Express, writes stop at 10 GB');

  const viewer = await page.context().newPage();
  await viewer.route('**/health', r => r.fulfill({ json: health }));
  await signIn(viewer, { mode: 'Single', role: 'Viewer' });
  await viewer.goto('/');
  await expect(viewer.getByRole('button', { name: /Overview/ }).first()).toBeVisible();
  await viewer.waitForLoadState('networkidle'); // every /health answer is in, so a banner would be up by now
  await expect(viewer.getByText('Shorten retention')).toHaveCount(0);
});

test('MSP audit log labels every entry with its client (U9)', async ({ page }) => {
  await signIn(page, {
    mode: 'Msp', role: 'Admin',
    api: {
      'GET /api/tenants/me': { current: tenantA.id, tenants: [tenantA] },
      'GET /api/tenants': [rosterRow(tenantA)],
      'GET /api/admin/users': [],
      'GET /api/tenants/assignments': {},
      'GET /api/api-tokens': [],
      'GET /api/admin/audit-log': [
        { id: 2, timestamp: '2026-10-02T09:00:00Z', actorEmail: 'a@msp.test', action: 'tenant.consent', targetType: 'tenant', tenantName: 'Fabrikam Inc' },
        { id: 1, timestamp: '2026-10-02T08:00:00Z', actorEmail: 'a@msp.test', action: 'user.add', targetType: 'user', tenantName: null },
      ],
    },
  });
  await page.goto('/#/users');
  const audit = page.locator('table', { hasText: 'tenant.consent' });
  await expect(audit.getByRole('columnheader', { name: 'Client' })).toBeVisible();
  await expect(audit.locator('tr', { hasText: 'tenant.consent' })).toContainText('Fabrikam Inc');
  await expect(audit.locator('tr', { hasText: 'user.add' })).toContainText('MSP');
});
