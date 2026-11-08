import type { Page, Route } from '@playwright/test';

/**
 * Stubs every /api call for the signed-in journeys. The client runs with
 * VITE_E2E_FAKE_AUTH=1 (MSAL bypassed); identity and role come from the mocked
 * /api/auth/me, edition mode from /api/auth/config. Anything a scenario does not
 * mock answers 404, which pages render as their normal error state — so a
 * journey only has to describe the endpoints it cares about.
 */
export type Role = 'Admin' | 'Analyst' | 'Viewer';
export type Handler = (route: Route, body: unknown) => unknown | Promise<unknown>;

export interface Scenario {
  mode: 'Single' | 'Msp';
  role: Role;
  /** "METHOD /path" or "/path" (any method) → JSON body, or a handler. */
  api?: Record<string, unknown | Handler>;
}

export const tenantA = { id: '0a000000-0000-0000-0000-00000000000a', name: 'Contoso Ltd', isActive: true, configured: true, lastCollectionStatus: 'Completed' };
export const tenantB = { id: '0b000000-0000-0000-0000-00000000000b', name: 'Fabrikam Inc', isActive: true, configured: false, lastCollectionStatus: null };

export function rosterRow(t: typeof tenantA, extra: Record<string, unknown> = {}) {
  return {
    id: t.id, name: t.name, microsoftTenantId: null, isActive: true, notes: null, createdAt: '2026-09-01T00:00:00Z',
    hasOwnCredentials: false, clientId: null, authMode: null, certificateThumbprint: null, brandName: null, brandAccentColor: null,
    consecutiveFailures: 0, nextCollectionAfter: null, credentialSource: t.configured ? 'install' : 'none', configured: t.configured,
    consentGrantedAt: null, lastCollectionAt: null, lastCollectionStatus: t.lastCollectionStatus, lastError: null, openAlerts: 0,
    ...extra,
  };
}

export function rollupRow(t: typeof tenantA, open = { critical: 0, high: 0, medium: 0, low: 0 }) {
  return {
    id: t.id, name: t.name, configured: t.configured, lastCollectionAt: null, lastCollectionStatus: t.lastCollectionStatus, lastError: null,
    open: { ...open, total: open.critical + open.high + open.medium + open.low },
    unresolvedAlerts: { critical: 0, high: 0 },
    health: !t.configured ? 'neutral' : open.critical ? 'error' : open.high ? 'warning' : 'good',
  };
}

export async function signIn(page: Page, s: Scenario): Promise<{ calls: string[] }> {
  const calls: string[] = [];
  const table: Record<string, unknown | Handler> = {
    'GET /api/auth/config': { instance: 'https://login.microsoftonline.com/', clientId: 'e2e-client', tenantId: 'e2e-tenant', redirectUri: 'http://localhost:5174', mode: s.mode },
    'GET /api/auth/me': { email: 'e2e@vigil365.test', name: 'E2E User', role: s.role },
    ...s.api,
  };

  await page.route('**/api/**', async (route) => {
    const req = route.request();
    const path = new URL(req.url()).pathname;
    const key = `${req.method()} ${path}`;
    calls.push(key);
    const hit = key in table ? table[key] : path in table ? table[path] : undefined;
    if (hit === undefined) return route.fulfill({ status: 404, json: { error: 'not mocked in e2e' } });
    let body: unknown = null;
    try { body = req.postDataJSON(); } catch { /* no body */ }
    const value = typeof hit === 'function' ? await (hit as Handler)(route, body) : hit;
    if (value === undefined) return; // handler fulfilled the route itself
    return route.fulfill({ status: 200, json: value });
  });

  return { calls };
}

/** A /api/dashboard/copilot answer: 48 licensed users, 17 idle, 12 seats never assigned. */
export function copilotSample(extra: Record<string, unknown> = {}) {
  return {
    configured: true, partial: false, seatsError: null, reportRefreshDate: '2026-10-17',
    licensedUsers: 48, active: 31, inactive: 12, neverUsed: 5,
    seats: 60, assignedSeats: 48, unusedSeats: 12, skus: [{ partNumber: 'Microsoft_365_Copilot', purchased: 60, assigned: 48 }],
    namesConcealed: false, activeDays: 30,
    apps: [['Teams', 27], ['Outlook', 22], ['Copilot Chat', 19], ['Word', 15], ['PowerPoint', 9], ['Excel', 7], ['OneNote', 3], ['Loop', 1]]
      .map(([name, active]) => ({ name, active })),
    users: [
      { name: 'Priya Shah', upn: 'priya@contoso.com', lastActivity: '2026-10-16', apps: ['Teams', 'Outlook', 'Word'], status: 'Active' },
      { name: 'Daniel Okafor', upn: 'daniel@contoso.com', lastActivity: '2026-10-14', apps: ['Copilot Chat', 'Excel'], status: 'Active' },
      { name: 'Lena Novak', upn: 'lena@contoso.com', lastActivity: '2026-09-12', apps: [], status: 'Inactive' },
      { name: 'Marco Rossi', upn: 'marco@contoso.com', lastActivity: null, apps: [], status: 'Never used' },
    ],
    ...extra,
  };
}

/** An activity (audit event) rule, as /api/alert-policies returns it. */
export const activityPolicy = (id: string, name: string, category: string, pattern: string, severity: string, enabled = true) => ({
  id, name, enabled, tenantId: null, category, kind: 'activity', metric: '', activityPattern: pattern,
  windowMinutes: 60, threshold: 1, severity, condition: `Activity "${pattern}" ≥ 1 in 60m`, triggerCount: 0,
});

/** /api/purview-audit/status: the three Purview audit searches in `status`, or per feed in `byFeed`. */
export function purviewStatus(status = 'Ok', byFeed: Record<string, string> = {}) {
  const lastSuccessAt = new Date(Date.now() - 40 * 60_000).toISOString();
  return {
    enabled: true, intervalMinutes: 60, highRiskConnectors: ['HTTP*', 'FTP*', 'SFTP*', 'Gmail', 'Dropbox'],
    feeds: ['powerPlatform', 'copilotStudio', 'powerPlatformAdmin'].map(feed => {
      const s = byFeed[feed] ?? status;
      return { feed, status: s, lastRunAt: lastSuccessAt, lastSuccessAt: s === 'Ok' ? lastSuccessAt : null, lastEventCount: 0, lastError: null, pending: false };
    }),
  };
}

const hoursAgo = (h: number) => new Date(Date.now() - h * 3_600_000).toISOString();

/** Power Platform page rules: three on, one off, and an identity rule the page must leave out. */
export const powerPlatformPolicies = [
  activityPolicy('pp1', 'Flow uses a high-risk connector', 'powerplatform', 'Flow * (high-risk connector:*', 'high'),
  activityPolicy('pp2', 'Power Platform DLP policy deleted', 'powerplatform', 'Power Platform DLP policy deleted*', 'high'),
  activityPolicy('pp3', 'App published', 'powerplatform', 'App published*', 'low', false),
  activityPolicy('cp1', 'Agent authentication changed', 'copilot', 'Agent authentication changed*', 'high'),
  { id: 'id1', name: 'Risky sign-ins spike', enabled: true, tenantId: null, category: 'identity', kind: 'metric', metric: 'riskySignIns',
    threshold: 5, severity: 'high', condition: 'riskySignIns >= 5', triggerCount: 0 },
];

const triggered = (n: number, policyId: string, policyName: string, category: string, severity: string, status: string, h: number) => ({
  id: `22222222-0000-0000-0000-00000000000${n}`, policyId, policyName, severity, category,
  condition: `Activity matched ${policyName}`, metricValue: 1, threshold: 1, triggeredAt: hoursAgo(h), status,
  affectedEntities: null, assignedTo: null,
});

/** One open Power Platform alert, one resolved agent alert, and one open identity alert that belongs elsewhere. */
export const powerPlatformTriggered = [
  triggered(1, 'pp1', 'Flow uses a high-risk connector', 'powerplatform', 'high', 'new', 2),
  triggered(2, 'cp1', 'Agent authentication changed', 'copilot', 'high', 'resolved', 30),
  triggered(3, 'id1', 'Risky sign-ins spike', 'identity', 'critical', 'new', 1),
];

/** Purview audit events as /api/audit-events returns them: one a rule alerts on, one only an off rule matches, one no rule matches. */
export const powerPlatformEvents = [
  { id: 1, activity: 'Flow created (high-risk connector: HTTP)', category: 'Power Automate', source: 'purviewAudit', actorUpn: 'j.smith@contoso.com', actorApp: null, targetName: 'Sync invoices to partner API', result: 'success', occurredAt: hoursAgo(3) },
  { id: 2, activity: 'App published', category: 'Power Apps', source: 'purviewAudit', actorUpn: 'priya@contoso.com', actorApp: null, targetName: 'Expense claims', result: 'success', occurredAt: hoursAgo(5) },
  { id: 3, activity: 'Environment created', category: 'Power Platform admin', source: 'purviewAudit', actorUpn: 'admin@contoso.com', actorApp: null, targetName: 'Contoso (dev)', result: 'success', occurredAt: hoursAgo(26) },
];

/**
 * The Power Platform page's API: rules, alerts, the Purview audit status and its events.
 * The tile asks /api/audit-events for pageSize=1 (just the 7-day total); the table pages
 * by 200. Every audit-events query string is passed to `onAuditQuery`.
 */
export function powerPlatformApi(opts: { status?: unknown; events?: unknown[]; weekTotal?: number; onAuditQuery?: (q: URLSearchParams) => void } = {}) {
  const events = opts.events ?? powerPlatformEvents;
  return {
    'GET /api/alert-policies': powerPlatformPolicies,
    'GET /api/triggered-alerts': powerPlatformTriggered,
    'GET /api/purview-audit/status': opts.status ?? purviewStatus(),
    'GET /api/audit-events': (route: Route) => {
      const q = new URL(route.request().url()).searchParams;
      opts.onAuditQuery?.(q);
      const pageSize = Number(q.get('pageSize') ?? 50);
      const source = q.get('source');
      const items = events.filter(e => source === 'purviewAudit' || (e as { category: string }).category === source);
      if (pageSize === 1) return { total: opts.weekTotal ?? items.length, page: 1, pageSize: 1, items: items.slice(0, 1) };
      return { total: items.length, page: Number(q.get('page') ?? 1), pageSize, items };
    },
  };
}

/** A /api/posture answer for a Business Premium tenant with one check in each status that matters. */
export function postureSample(extra: Record<string, unknown> = {}) {
  const finding = (id: string, title: string, workload: string, status: string, detail: string, more: Record<string, unknown> = {}) => ({
    id, title, workload, status, detail,
    licensedBy: status === 'NotLicensed' ? null : 'Microsoft 365 Business Premium',
    unlockedBy: status === 'NotLicensed' ? ['Microsoft Entra ID P2', 'Microsoft 365 E5'] : [],
    readFrom: 'Microsoft Graph', whyItMatters: 'Attackers try stolen passwords first.', minimum: 'An enabled policy for all users and all resources.',
    fix: ['Open Conditional Access in the Microsoft Entra admin center.', 'Create the policy in report-only, then turn it on.'],
    portalUrl: 'https://entra.microsoft.com/', feature: 'Microsoft Entra ID P1 (Conditional Access)',
    featureSource: 'https://learn.microsoft.com/en-us/entra/identity/conditional-access/overview',
    baselines: { scuba: ['MS.AAD.3.2v2'], cis: [], secureScore: [] }, ...more,
  });
  return {
    configured: true, generatedAt: new Date().toISOString(), organizationName: 'Contoso Ltd', error: null,
    licences: [{ partNumber: 'SPB', name: 'Microsoft 365 Business Premium', enabled: 25, consumed: 22 }],
    includedFeatures: ['Microsoft Entra ID P1 (Conditional Access)', 'Microsoft Intune Plan 1'],
    summary: { total: 5, pass: 1, fail: 1, licensedNotConfigured: 1, licensedLowAdoption: 0, notLicensed: 1, unknown: 1 },
    scuba: { pass: 1, total: 3 }, secureScore: { current: 41.6, max: 80 },
    sources: [
      { name: 'Microsoft Graph', state: 'Connected', detail: 'All Graph checks read' },
      { name: 'Exchange Online PowerShell', state: 'Not set up yet', detail: 'Safe Links and preset policies are read here. Coming in the next phase.' },
    ],
    findings: [
      finding('PST-AAD-002', 'Legacy authentication is blocked', 'Entra ID', 'Pass', 'Enabled policy “Block legacy authentication” blocks legacy authentication for all users.'),
      finding('PST-INT-001', 'Devices have a compliance policy', 'Intune', 'LicensedNotConfigured', 'No device compliance policies.'),
      finding('PST-AAD-005', 'High-risk users and sign-ins are blocked', 'Entra ID', 'NotLicensed', 'Needs Microsoft Entra ID P2. No licence in this tenant includes it.'),
      finding('PST-MDO-001', 'Safe Links is on for everyone', 'Defender for Office 365', 'Unknown',
        'Read with Exchange Online PowerShell, which Vigil365 doesn’t connect to yet.', { readFrom: 'Exchange Online PowerShell' }),
      finding('PST-AAD-001', 'Conditional Access requires MFA for all users', 'Entra ID', 'Fail',
        'Enabled policy “Require MFA” is for all users, but it depends on where the sign-in comes from.'),
    ],
    upgrades: [{ unlockedBy: ['Microsoft Entra ID P2', 'Microsoft 365 E5'], checks: ['PST-AAD-005'] }],
    ...extra,
  };
}
