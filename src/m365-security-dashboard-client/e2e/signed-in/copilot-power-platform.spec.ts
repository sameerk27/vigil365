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

/** Phase D: the optional Power Platform inventory. */

const ppSetup = {
  tenantId: '11111111-1111-1111-1111-111111111111', appId: '22222222-2222-2222-2222-222222222222',
  role: 'Power Platform reader', roleId: 'c886ad2e-27f7-4874-8381-5849b8d8a090', script: 'grant-power-platform-reader.ps1',
  command: '.\\grant-power-platform-reader.ps1 -TenantId 11111111-1111-1111-1111-111111111111 -AppId 22222222-2222-2222-2222-222222222222',
};

const ppInventory = {
  configured: true, status: 'Ready', reason: null, readAt: new Date().toISOString(), setup: ppSetup,
  summary: { environments: 3, environmentsRead: 3, environmentsSkipped: 0, apps: 3, flows: 3, consentBypassed: 1, highRiskConnector: 2, widelyShared: 1, customConnector: 1 },
  environments: [
    { id: 'env-prod', name: 'Contoso (default)', type: 'Default', state: 'Ready', region: 'europe', hasDataverse: true, created: null, apps: 2, flows: 3, problem: null },
    { id: 'env-fin', name: 'Finance', type: 'Production', state: 'Ready', region: 'europe', hasDataverse: true, created: null, apps: 1, flows: 0, problem: null },
    { id: 'env-trial', name: "Sam's trial", type: 'Trial', state: 'Ready', region: 'unitedstates', hasDataverse: false, created: null, apps: 0, flows: 0, problem: null },
  ],
  apps: [
    { id: 'app-1', name: 'Expense approvals', environmentId: 'env-prod', environmentName: 'Contoso (default)', owner: 'Priya Shah', created: null, modified: '2026-10-01T09:00:00Z',
      sharedUsers: 120, sharedGroups: 1, bypassConsent: true, connectors: ['SharePoint', 'Contoso ERP'],
      flags: [{ label: 'Consent prompt bypassed', severity: 'high' }, { label: 'Custom connector', severity: 'medium' }, { label: 'Shared with 120 people', severity: 'medium' }, { label: 'Shared with a group', severity: 'low' }] },
    { id: 'app-2', name: 'Room booking', environmentId: 'env-prod', environmentName: 'Contoso (default)', owner: 'Lena Novak', created: null, modified: '2026-08-12T09:00:00Z',
      sharedUsers: 3, sharedGroups: 0, bypassConsent: false, connectors: ['Office 365 Outlook'], flags: [] },
    { id: 'app-3', name: 'Dropbox uploader', environmentId: 'env-fin', environmentName: 'Finance', owner: 'Daniel Okafor', created: null, modified: '2026-10-15T09:00:00Z',
      sharedUsers: 2, sharedGroups: 0, bypassConsent: false, connectors: ['Dropbox'], flags: [{ label: 'High-risk connector: Dropbox', severity: 'high' }] },
  ],
  flows: [
    { id: 'f-1', name: 'Sync invoices to partner API', environmentId: 'env-prod', environmentName: 'Contoso (default)', state: 'On', created: null, modified: '2026-10-18T08:00:00Z',
      connectors: ['HTTP with Microsoft Entra ID', 'Office 365 Outlook'], flags: [{ label: 'High-risk connector: HTTP with Microsoft Entra ID', severity: 'high' }] },
    { id: 'f-2', name: 'Weekly sales digest', environmentId: 'env-prod', environmentName: 'Contoso (default)', state: 'On', created: null, modified: '2026-09-02T08:00:00Z',
      connectors: ['Dataverse', 'Office 365 Outlook'], flags: [] },
    { id: 'f-3', name: 'Old reminder', environmentId: 'env-prod', environmentName: 'Contoso (default)', state: 'Suspended', created: null, modified: null,
      connectors: [], flags: [{ label: 'Suspended', severity: 'low' }] },
  ],
};

test('Power Platform: a tenant that has not opted in sees how, then the inventory after Check again', async ({ page }) => {
  const asked: string[] = [];
  await signIn(page, {
    mode: 'Single', role: 'Admin',
    api: {
      'GET /api/power-platform/inventory': (route: any) => {
        const url = route.request().url();
        asked.push(url);
        return url.includes('refresh=true') ? ppInventory : { configured: true, status: 'NotEnabled', reason: 'RoleMissing', setup: ppSetup };
      },
    },
  });
  await page.goto('/#/powerplatform');

  await expect(page.getByText('Power Platform inventory is off for this tenant')).toBeVisible();
  await expect(page.getByText(ppSetup.command)).toBeVisible();
  await page.screenshot({ path: test.info().outputPath('power-platform-off.png'), fullPage: true });

  await page.getByRole('button', { name: 'Check again' }).click();
  await expect(page.getByText('Expense approvals')).toBeVisible();
  expect(asked.some(u => u.includes('refresh=true'))).toBe(true);

  await page.getByLabel('Filter apps').selectOption('flagged');
  await expect(page.getByText('Room booking')).toHaveCount(0);
  await page.getByLabel('Filter apps').selectOption('');
  await page.screenshot({ path: test.info().outputPath('power-platform.png'), fullPage: true });
});
