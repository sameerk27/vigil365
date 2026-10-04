import { test, expect, type Page } from '@playwright/test';
import { createRequire } from 'node:module';
import { signIn, tenantA, tenantB, rosterRow, rollupRow } from './fixtures';

/**
 * MSP_V12_PLAN.md T3: axe over the real, rendered MSP screens (with the app's
 * stylesheet, so colour contrast is checked too) — not copied markup.
 */
const axePath = createRequire(import.meta.url).resolve('axe-core/axe.min.js');

async function axeViolations(page: Page, include?: string): Promise<string> {
  // Pages and dialogs fade in (.18-.2s). Axe samples colours at the moment it runs,
  // so mid-fade it reports contrast for a half-transparent blend that never exists
  // on screen (it failed only on CI's slower runner). Measure the settled state.
  // Infinite animations (spinners, the loading-skeleton slide) never finish; skip them.
  await page.evaluate(() => Promise.all(document.getAnimations()
    .filter(a => a.effect?.getComputedTiming().iterations !== Infinity)
    .map(a => a.finished.catch(() => undefined))));
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

// The data pages, rendered (src/a11y.test.ts covers components without the stylesheet).
const triggered = [
  { id: '11111111-0000-0000-0000-000000000001', policyId: 'p1', policyName: 'Risky sign-ins spike', severity: 'high', category: 'identity', condition: 'riskySignIns >= 5', metricValue: 9, threshold: 5, triggeredAt: '2026-10-02T08:00:00Z', status: 'new' },
  { id: '11111111-0000-0000-0000-000000000002', policyId: 'p1', policyName: 'MFA coverage drop', severity: 'medium', category: 'identity', condition: 'mfaMissing >= 5', metricValue: 7, threshold: 5, triggeredAt: '2026-10-01T08:00:00Z', status: 'new', snoozedUntil: null },
];
const single = (api: Record<string, unknown> = {}) => ({ mode: 'Single' as const, role: 'Analyst' as const, api: {
  'GET /api/triggered-alerts': triggered, 'GET /api/alert-policies': [policy], ...api,
} });

test('Alert Center queue and an open alert', async ({ page }) => {
  await signIn(page, single());
  await page.goto('/#/alertcenter');
  await expect(page.getByRole('button', { name: 'Open triggered alert Risky sign-ins spike' })).toBeVisible();
  expect(await axeViolations(page, 'main')).toBe('');
  await page.getByRole('button', { name: 'Open triggered alert Risky sign-ins spike' }).click();
  await expect(page.getByRole('dialog', { name: 'Risky sign-ins spike' })).toBeVisible();
  expect(await axeViolations(page, '[role="dialog"]')).toBe('');
});

test('Reports with a schedule and the digest preview', async ({ page }) => {
  await signIn(page, { mode: 'Single', role: 'Admin', api: {
    'GET /api/report-schedules': [{ id: 's1', tenantId: null, name: 'Weekly executive digest', reportType: 'exec-digest', cadence: 'weekly', dayOfWeek: 1, dayOfMonth: 1, hourUtc: 7, recipients: 'ciso@contoso.com', includeCsv: true, includePdf: true, enabled: true, createdAt: '2026-09-01T00:00:00Z' }],
    'GET /api/reports/exec-digest/preview': { subject: 'Digest', htmlBody: '', csv: 'a,b', generatedAt: '2026-10-02T08:00:00Z', hasData: true, metrics: [{ label: 'Secure score', value: '60%', delta: 2, higherIsWorse: false }], topAlerts: [{ severity: 'high', category: 'identity', policyName: 'Risky sign-ins spike', condition: 'riskySignIns >= 5', status: 'new', assignedTo: null }] },
  } });
  await page.goto('/#/reports');
  await expect(page.getByRole('row', { name: /Weekly executive digest/ })).toBeVisible();
  expect(await axeViolations(page, 'main')).toBe('');
});

test('Overview', async ({ page }) => {
  await signIn(page, single());
  await page.goto('/#/overview');
  await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
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

test('Incidents with a collected alert', async ({ page }) => {
  const collected = {
    id: 7, alertType: 'riskyUser', service: 'EntraId', severity: 'High', title: 'Risky user detected',
    userPrincipalName: 'megan@contoso.test', detectedAt: '2026-10-02T08:00:00Z', lastUpdatedAt: '2026-10-02T08:00:00Z', isResolved: false,
  };
  await signIn(page, single({ 'GET /api/alerts': { items: [collected], total: 1 } }));
  await page.goto('/#/incidents');
  await expect(page.getByText('Risky user detected').first()).toBeVisible();
  expect(await axeViolations(page, 'main')).toBe('');
});

test('Trends with snapshots', async ({ page }) => {
  const snap = (capturedAt: string, score: number) => ({ id: capturedAt, capturedAt, riskyUsersCount: 1, mfaCoveragePct: 90, nonCompliantDevicesCount: 2, criticalAlertsCount: 0, highAlertsCount: 1, secureScorePct: score, complianceIssuesCount: 0 });
  await signIn(page, single({ 'GET /api/dashboard/trends': [snap('2026-10-01T08:00:00Z', 58), snap('2026-10-02T08:00:00Z', 60)] }));
  await page.goto('/#/trends');
  await expect(page.locator('.line-chart-svg').first()).toBeVisible();
  expect(await axeViolations(page, 'main')).toBe('');
});
