import { test, expect } from '@playwright/test';
import { signIn } from './fixtures';

/** docs/COPILOT_POWER_PLATFORM_PLAN.md: phase A (agent, flow and app alerts) and phase B (Copilot usage). */

const packPolicy = (id: string, name: string, category: string, pattern: string, severity: string, enabled = true) => ({
  id, name, enabled, tenantId: null, category, kind: 'activity', metric: '', activityPattern: pattern,
  windowMinutes: 60, threshold: 1, severity, condition: `Activity "${pattern}" ≥ 1 in 60m`, triggerCount: 0,
});

const feeds = (status: string) => [
  { feed: 'powerPlatform', status, lastRunAt: null, lastSuccessAt: null, lastEventCount: 0, lastError: null, pending: false },
  { feed: 'copilotStudio', status, lastRunAt: null, lastSuccessAt: null, lastEventCount: 0, lastError: null, pending: false },
];

test('Rules: the Copilot and Power Platform rules say which permission they are waiting for', async ({ page }) => {
  await signIn(page, {
    mode: 'Single', role: 'Admin',
    api: {
      'GET /api/alert-policies': [
        packPolicy('c1', 'Agent authentication changed', 'copilot', 'Agent authentication changed*', 'high'),
        packPolicy('p1', 'Flow uses a high-risk connector', 'powerplatform', 'Flow * (high-risk connector:*', 'high'),
        packPolicy('p2', 'App published', 'powerplatform', 'App published*', 'low', false),
      ],
      'GET /api/purview-audit/status': { enabled: true, intervalMinutes: 60, highRiskConnectors: ['HTTP*', 'FTP*', 'SFTP*', 'Gmail', 'Dropbox'], feeds: feeds('NeedsPermission') },
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
  await signIn(page, {
    mode: 'Single', role: 'Analyst',
    api: {
      'GET /api/dashboard/copilot': {
        configured: true, reportRefreshDate: '2026-10-17', licensedUsers: 48, active: 31, inactive: 12, neverUsed: 5,
        seats: 60, assignedSeats: 48, unusedSeats: 12, skus: [{ partNumber: 'Microsoft_365_Copilot', purchased: 60, assigned: 48 }],
        namesConcealed: false, activeDays: 30,
        apps: [['Teams', 27], ['Outlook', 22], ['Copilot Chat', 19], ['Word', 15], ['PowerPoint', 9], ['Excel', 7], ['OneNote', 3], ['Loop', 1]].map(([name, active]) => ({ name, active })),
        users: [
          { name: 'Priya Shah', upn: 'priya@contoso.com', lastActivity: '2026-10-16', apps: ['Teams', 'Outlook', 'Word'], status: 'Active' },
          { name: 'Daniel Okafor', upn: 'daniel@contoso.com', lastActivity: '2026-10-14', apps: ['Copilot Chat', 'Excel'], status: 'Active' },
          { name: 'Lena Novak', upn: 'lena@contoso.com', lastActivity: '2026-09-12', apps: [], status: 'Inactive' },
          { name: 'Marco Rossi', upn: 'marco@contoso.com', lastActivity: null, apps: [], status: 'Never used' },
        ],
      },
    },
  });
  await page.goto('/#/copilot');

  await expect(page.getByText('17 licensed users haven\'t used Copilot in 30 days')).toBeVisible();
  await expect(page.getByText('of 60 seats bought')).toBeVisible();
  await page.getByRole('button', { name: 'Show inactive users' }).click();
  await expect(page.getByRole('table').getByRole('row')).toHaveCount(2); // header + Lena
  await page.getByLabel('Filter by status').selectOption('');
  await page.screenshot({ path: test.info().outputPath('copilot.png'), fullPage: true });
});

/** Phase D: the Power Platform inventory, read with the viewer's own Microsoft sign-in. */

test('Power Platform: the page offers to read with the viewer’s own Microsoft sign-in', async ({ page }) => {
  await signIn(page, {
    mode: 'Single', role: 'Analyst',
    api: {
      'GET /api/power-platform/settings': { configured: true, tenantId: '11111111-1111-1111-1111-111111111111',
        powerPlatformApi: 'https://api.powerplatform.com', graphApi: 'https://graph.microsoft.com', maxEnvironments: 50 },
    },
  });
  await page.goto('/#/powerplatform');

  await expect(page.getByText('Read Power Platform with your Microsoft sign-in')).toBeVisible();
  await expect(page.getByText(/Vigil365's app gets no Power Platform access/)).toBeVisible();
  // The Microsoft popup can't run under the test sign-in; the button is what a viewer uses.
  await expect(page.getByRole('button', { name: 'Read with Microsoft sign-in' })).toBeEnabled();
  await page.screenshot({ path: test.info().outputPath('power-platform.png'), fullPage: true });
});
