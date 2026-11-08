import { test, expect, type Page } from '@playwright/test';
import { signIn, copilotSample, activityPolicy, purviewStatus, powerPlatformApi } from './fixtures';

/**
 * docs/COPILOT_POWER_PLATFORM_PLAN.md: phase A (agent, flow and app alerts) and phase B (Copilot usage).
 * docs/POWER_PLATFORM_ALERTS_PLAN.md: the Power Platform page's alerts and activity, from the Purview audit log.
 */

test('Rules: the Copilot and Power Platform rules say which permission they are waiting for', async ({ page }) => {
  await signIn(page, {
    mode: 'Single', role: 'Admin',
    api: {
      'GET /api/alert-policies': [
        activityPolicy('c1', 'Agent authentication changed', 'copilot', 'Agent authentication changed*', 'high'),
        activityPolicy('p1', 'Flow uses a high-risk connector', 'powerplatform', 'Flow * (high-risk connector:*', 'high'),
        activityPolicy('p2', 'App published', 'powerplatform', 'App published*', 'low', false),
      ],
      'GET /api/purview-audit/status': purviewStatus('NeedsPermission'),
    },
  });
  await page.goto('/#/alertcenter');
  await page.getByRole('tab', { name: 'Policies' }).click();

  await expect(page.getByRole('alert').filter({ hasText: 'need one more permission' })).toContainText('AuditLogsQuery.Read.All');
  await expect(page.getByLabel('High-risk connectors')).toContainText('Gmail');
  await expect(page.locator('tr', { hasText: 'Agent authentication changed' })).toContainText('Copilot agents');
  await expect(page.locator('tr', { hasText: 'Flow uses a high-risk connector' })).toContainText('Power Platform');
  await page.screenshot({ path: test.info().outputPath('rules.png'), fullPage: true });
});

test('Tenant Activity: the source filter asks the API for that product only', async ({ page }) => {
  const sources: (string | null)[] = [];
  await signIn(page, {
    mode: 'Single', role: 'Admin',
    api: {
      'GET /api/audit-events': (route: any) => {
        const source = new URL(route.request().url()).searchParams.get('source');
        sources.push(source);
        const items = [
          { id: 1, activity: 'Flow created (high-risk connector: HTTP)', category: 'Power Automate', source: 'purviewAudit', actorUpn: 'j.smith@contoso.com', targetName: 'Sync invoices to partner API', result: 'success', occurredAt: new Date().toISOString() },
          { id: 2, activity: 'Agent authentication changed', category: 'Copilot agents', source: 'purviewAudit', actorUpn: 'priya@contoso.com', targetName: 'HR Helper', result: 'success', occurredAt: new Date().toISOString() },
          { id: 3, activity: 'Add member to role', category: 'RoleManagement', source: 'directoryAudit', actorUpn: 'admin@contoso.com', targetName: 'Lena Novak', result: 'success', occurredAt: new Date().toISOString() },
        ].filter(i => !source || i.category === source || (source === 'entra' && i.source === 'directoryAudit'));
        return { total: items.length, page: 1, pageSize: 50, items };
      },
    },
  });
  await page.goto('/#/activityfeed');
  await expect(page.getByText('Flow created (high-risk connector: HTTP)')).toBeVisible();

  await page.getByLabel('Filter by source').selectOption('Power Automate');
  await expect(page.getByText('Agent authentication changed')).toHaveCount(0);
  await expect(page.getByText('Flow created (high-risk connector: HTTP)')).toBeVisible();
  expect(sources).toContain('Power Automate');
  await page.screenshot({ path: test.info().outputPath('activity.png'), fullPage: true });
});

test('Copilot usage: seats, idle licences and the user list', async ({ page }) => {
  await signIn(page, { mode: 'Single', role: 'Analyst', api: { 'GET /api/dashboard/copilot': copilotSample() } });
  await page.goto('/#/copilot');

  await expect(page.getByText('17 licensed users haven\'t used Copilot in 30 days')).toBeVisible();
  await expect(page.getByText('of 60 seats bought')).toBeVisible();
  await page.getByRole('button', { name: 'Show inactive users' }).click();
  await expect(page.getByRole('table').getByRole('row')).toHaveCount(2); // header + Lena
  await page.getByLabel('Filter by status').selectOption('');

  await page.getByRole('button', { name: 'Status' }).click();
  await expect(page.getByRole('table').locator('tbody tr').first()).toContainText('Marco Rossi'); // never used first
  await page.screenshot({ path: test.info().outputPath('copilot.png'), fullPage: true });
});

test('Copilot usage: a part-read report and unreadable licences are said, not shown as complete', async ({ page }) => {
  await signIn(page, {
    mode: 'Single', role: 'Viewer',
    api: {
      'GET /api/dashboard/copilot': copilotSample({
        partial: true, seats: null, assignedSeats: null, unusedSeats: null, skus: [],
        seatsError: 'Only some of the tenant’s licences could be read, so seat numbers aren’t shown. Refresh to try again.',
      }),
    },
  });
  await page.goto('/#/copilot');

  await expect(page.getByRole('status').filter({ hasText: 'Only part of the usage report could be read' })).toContainText('cover the 48 users');
  await expect(page.getByRole('status').filter({ hasText: "Seat numbers aren't shown" })).toBeVisible();
  await expect(page.getByText("licences couldn't be read")).toBeVisible();
  await expect(page.getByText('no Copilot licence found')).toHaveCount(0);
  await page.screenshot({ path: test.info().outputPath('copilot-partial.png'), fullPage: true });
});

/** The Power Platform page: alerts and activity from the Purview audit log, read by the server. */

const card = (page: Page, title: string) =>
  page.locator('.card').filter({ has: page.getByRole('heading', { level: 2, name: title, exact: true }) });

test('Power Platform: an open alert is listed, and selecting it opens it in the Alert Center', async ({ page }) => {
  await signIn(page, { mode: 'Single', role: 'Analyst', api: powerPlatformApi() });
  await page.goto('/#/powerplatform');

  const alerts = card(page, 'Alerts');
  const open = alerts.getByRole('button', { name: 'Open Flow uses a high-risk connector in the Alert Center' });
  await expect(open).toBeVisible();
  await expect(open).toContainText('Power Platform');
  // Open alerts only by default; an identity alert belongs to another page.
  await expect(alerts.getByText('Agent authentication changed')).toHaveCount(0);
  await expect(alerts.getByText('Risky sign-ins spike')).toHaveCount(0);
  await expect(page.locator('.ui-stat', { hasText: 'Open alerts' })).toContainText('1');

  await alerts.getByLabel('Filter alerts by status').selectOption('');
  await expect(alerts.getByRole('button', { name: 'Open Agent authentication changed in the Alert Center' })).toContainText('Copilot agents');
  await page.screenshot({ path: test.info().outputPath('power-platform-alerts.png'), fullPage: true });

  await open.click();
  await expect(page).toHaveURL(/#\/alertcenter/);
  await expect(page.getByRole('dialog', { name: 'Flow uses a high-risk connector' })).toBeVisible();
});

test('Power Platform: activity says which rule would alert', async ({ page }) => {
  const queries: string[] = [];
  await signIn(page, { mode: 'Single', role: 'Analyst', api: powerPlatformApi({ weekTotal: 42, onAuditQuery: q => queries.push(q.toString()) }) });
  await page.goto('/#/powerplatform');

  const activity = card(page, 'Activity');
  const flow = activity.getByRole('row', { name: /Flow created \(high-risk connector: HTTP\)/ });
  await expect(flow).toContainText('Would alert: Flow uses a high-risk connector');
  // "App published" only matches a rule that is off; nothing matches "Environment created".
  await expect(activity.getByRole('row', { name: /App published/ })).not.toContainText('Would alert');
  await expect(activity.getByRole('row', { name: /Environment created/ })).not.toContainText('Would alert');
  await expect(page.locator('.ui-stat', { hasText: 'Events in 7 days' })).toContainText('42');

  // The table reads every Purview audit product a page of 200 at a time; the tile reads just the 7-day total.
  expect(queries).toContain('source=purviewAudit&days=7&page=1&pageSize=200');
  expect(queries).toContain('source=purviewAudit&days=7&pageSize=1');

  await activity.getByLabel('Only what a rule alerts on').check();
  await expect(activity.locator('tbody tr')).toHaveCount(1);
  await activity.getByLabel('Only what a rule alerts on').uncheck();

  await activity.getByLabel('Filter activity by product').selectOption('Power Platform admin');
  await expect(activity.locator('tbody tr')).toHaveCount(1);
  await expect(activity.locator('tbody tr')).toContainText('Environment created');
  expect(queries).toContain('source=Power+Platform+admin&days=7&page=1&pageSize=200');
  await page.screenshot({ path: test.info().outputPath('power-platform-activity.png'), fullPage: true });
});

test('Power Platform: the browser talks only to Vigil365, never to Microsoft', async ({ page, baseURL }) => {
  const elsewhere: string[] = [];
  page.on('request', r => { if (new URL(r.url()).origin !== new URL(baseURL!).origin) elsewhere.push(r.url()); });
  const { calls } = await signIn(page, { mode: 'Single', role: 'Analyst', api: powerPlatformApi() });
  await page.goto('/#/powerplatform');

  await expect(card(page, 'Activity').getByText('Would alert: Flow uses a high-risk connector')).toBeVisible();
  await expect(card(page, 'Rules').locator('tbody tr')).toHaveCount(4);
  // No Microsoft sign-in prompt, and no Power Platform API or Graph read from the browser.
  await expect(page.locator('main').getByRole('button', { name: /sign-in|sign in/i })).toHaveCount(0);
  expect(elsewhere).toEqual([]);
  expect(calls.filter(c => c.includes('/api/power-platform'))).toEqual([]);
});

test('Power Platform: says which permission is missing when a Purview audit search needs one', async ({ page }) => {
  await signIn(page, {
    mode: 'Single', role: 'Viewer',
    api: powerPlatformApi({ status: purviewStatus('Ok', { powerPlatformAdmin: 'NeedsPermission' }), events: [] }),
  });
  await page.goto('/#/powerplatform');

  await expect(page.getByRole('alert').filter({ hasText: 'need one more permission' })).toContainText('AuditLogsQuery.Read.All');
  await expect(card(page, 'Activity').getByText(/Nothing logged in the last 7 days\. If the status above says a permission is missing/)).toBeVisible();
  await page.screenshot({ path: test.info().outputPath('power-platform-permission.png'), fullPage: true });
});
