import { test, expect, type Page } from '@playwright/test';
import { createRequire } from 'node:module';
import { signIn, tenantA, tenantB, rosterRow, rollupRow } from './fixtures';

/**
 * MSP_V12_PLAN.md T3: axe over the real, rendered MSP screens (with the app's
 * stylesheet, so colour contrast is checked too) — not copied markup.
 */
const axePath = createRequire(import.meta.url).resolve('axe-core/axe.min.js');

async function axeViolations(page: Page, include?: string): Promise<string> {
  await page.addScriptTag({ path: axePath });
  const v = await page.evaluate(async (sel) => {
    const r = await (window as any).axe.run(sel ? document.querySelector(sel) : document, { resultTypes: ['violations'] });
    return r.violations.map((x: any) => `${x.id} [${x.impact}]: ${x.help}\n    ${x.nodes.slice(0, 3).map((n: any) => n.target.join(' ') + '  ' + ((n.any[0] && n.any[0].message) || '').slice(0, 150)).join('\n    ')}`);
  }, include ?? null);
  return v.join('\n');
}

const queue = [
  { id: '11111111-0000-0000-0000-000000000001', tenantId: tenantB.id, tenantName: 'Fabrikam Inc', policyName: 'Risky sign-ins spike', severity: 'critical', category: 'identity', condition: 'riskySignIns >= 5', metricValue: 9, triggeredAt: '2026-10-02T08:00:00Z', status: 'new', assignedTo: null, snoozedUntil: null },
];
const policy = { id: 'p1', name: 'Risky sign-ins spike', enabled: true, tenantId: null, category: 'identity', condition: 'riskySignIns >= 5', kind: 'metric', metric: 'riskySignIns', threshold: 5, severity: 'high' };

const msp = (current: string | null, extra: Record<string, unknown> = {}) => ({
  mode: 'Msp' as const, role: 'Admin' as const,
  api: {
    'GET /api/tenants/me': { current, tenants: [tenantA, tenantB] },
    'GET /api/tenants/rollup': [rollupRow(tenantA, { critical: 1, high: 0, medium: 0, low: 0 }), rollupRow(tenantB)],
    'GET /api/tenants': [rosterRow(tenantA), rosterRow(tenantB)],
    'GET /api/tenants/alerts': queue,
    ...extra,
  },
});

test('Choose a client: rollup cards, roster and cross-client queue', async ({ page }) => {
  await signIn(page, msp(null));
  await page.goto('/');
  await expect(page.locator('.queue-tbl tbody tr')).toHaveCount(1);
  expect(await axeViolations(page)).toBe('');
});

test('onboarding dialog', async ({ page }) => {
  await signIn(page, msp(null));
  await page.goto('/#/clients');
  await page.locator('tr', { hasText: 'Fabrikam Inc' }).getByRole('button', { name: /Connect/ }).click();
  await expect(page.getByRole('button', { name: /Sign in as global admin & consent/ })).toBeVisible();
  expect(await axeViolations(page, '[role="dialog"]')).toBe('');
});

test('header with client switcher', async ({ page }) => {
  await signIn(page, msp(tenantA.id));
  await page.goto('/#/clients');
  await expect(page.getByLabel('Client tenant')).toBeVisible();
  expect(await axeViolations(page, 'header')).toBe('');
});

test('User Management: API tokens with client restriction, audit Client column', async ({ page }) => {
  await signIn(page, msp(tenantA.id, {
    'GET /api/admin/users': [], 'GET /api/tenants/assignments': {},
    'GET /api/api-tokens': [{ id: 't1', name: 'SIEM', prefix: 'v365_ab', scopes: 'alerts:read', createdAt: '2026-10-01T00:00:00Z', tenantId: tenantB.id, lastUsedAt: null, revokedAt: null, expiresAt: null }],
    'GET /api/admin/audit-log': [{ id: 1, timestamp: '2026-10-02T08:00:00Z', actorEmail: 'a@msp.test', action: 'tenant.consent', targetType: 'tenant', tenantName: 'Fabrikam Inc' }],
  }));
  await page.goto('/#/users');
  await page.getByRole('button', { name: /New token/ }).click();
  await expect(page.getByLabel('Restrict to client')).toBeVisible();
  expect(await axeViolations(page, 'main')).toBe('');
});

test('Alert Center policies with per-client override column, and routing card', async ({ page }) => {
  await signIn(page, msp(tenantA.id, {
    'GET /api/alert-policies': [policy],
    'GET /api/alert-policies/tenant-overrides': [{ policyId: 'p1', enabled: false, threshold: null, notifyEmail: null }],
    'GET /api/notification-routing': { exists: true, notifyMsp: true, notifyClient: false, recipientEmail: null, teamsWebhookUrl: null, hasWebhookUrl: false, minSeverity: null, lastDigestAt: null },
  }));
  await page.goto('/#/alertcenter');
  await page.getByRole('tab', { name: 'Policies' }).click();
  await expect(page.getByText('Off for this client')).toBeVisible();
  expect(await axeViolations(page, 'main')).toBe('');
  await page.getByRole('tab', { name: 'Notifications', exact: true }).click();
  await expect(page.getByText("This client's routing")).toBeVisible();
  expect(await axeViolations(page, 'main')).toBe('');
});
