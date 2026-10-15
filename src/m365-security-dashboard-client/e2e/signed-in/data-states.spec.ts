import { test, expect } from '@playwright/test';
import { signIn, tenantA } from './fixtures';

/** Alert Center, Trends and Reports: failures are not all-clears, and exports name the client. */

const alert = {
  id: '11111111-0000-0000-0000-000000000001', policyId: 'p1', policyName: 'Risky sign-ins spike', severity: 'high', category: 'identity',
  condition: 'riskySignIns >= 5', metricValue: 9, threshold: 5, triggeredAt: '2026-10-02T08:00:00Z', status: 'new',
};

test('a failed refresh keeps the open alerts on screen and says the list may be out of date', async ({ page }) => {
  let fail = false;
  await signIn(page, {
    mode: 'Single', role: 'Analyst',
    api: {
      'GET /api/triggered-alerts': async (route) => {
        if (fail) { await route.fulfill({ status: 500, json: {} }); return undefined; }
        return [alert];
      },
    },
  });
  await page.goto('/#/alertcenter');
  const row = page.locator('tr', { hasText: 'Risky sign-ins spike' });
  await expect(row).toBeVisible();
  // Every unmocked dashboard panel failed; clear that banner so the next one is ours.
  await page.getByRole('button', { name: 'Dismiss' }).click();

  fail = true;
  await page.getByRole('tab', { name: 'Policies' }).click();
  await page.getByRole('tab', { name: 'Alerts' }).click(); // re-reads the alerts
  await expect(page.getByRole('alert')).toContainText('Alert rules or triggered alerts failed to load');
  await expect(row).toBeVisible();
  await expect(page.getByText(/No alerts triggered yet/)).toHaveCount(0);
});

const msp = (extra: Record<string, unknown>) => ({
  mode: 'Msp' as const, role: 'Analyst' as const,
  api: { 'GET /api/tenants/me': { current: tenantA.id, tenants: [tenantA] }, ...extra },
});

test('MSP: the Trends CSV is named after the client (tq-7)', async ({ page }) => {
  const snap = (capturedAt: string) => ({ id: capturedAt, capturedAt, riskyUsersCount: 1, mfaCoveragePct: 90, nonCompliantDevicesCount: 2, criticalAlertsCount: 0, highAlertsCount: 1, secureScorePct: 60, complianceIssuesCount: 0 });
  await signIn(page, msp({ 'GET /api/dashboard/trends': [snap('2026-10-01T08:00:00Z'), snap('2026-10-02T08:00:00Z')] }));
  await page.goto('/#/trends');
  const download = page.waitForEvent('download');
  await page.getByTitle('Export all trend data as CSV').click();
  expect((await download).suggestedFilename()).toMatch(/^contoso-ltd-vigil365-trends-30days-\d{4}-\d{2}-\d{2}\.csv$/);
});

test('MSP: the digest CSV is named after the client (tq-7)', async ({ page }) => {
  await signIn(page, msp({
    'GET /api/report-schedules': [],
    'GET /api/reports/exec-digest/preview': { subject: 'Digest', htmlBody: '', csv: 'a,b', generatedAt: '2026-10-02T08:00:00Z', hasData: true, metrics: [], topAlerts: [] },
  }));
  await page.goto('/#/reports');
  const download = page.waitForEvent('download');
  await page.getByRole('button', { name: 'CSV' }).click();
  expect((await download).suggestedFilename()).toBe('contoso-ltd-vigil365-digest-2026-10-02.csv');
});
