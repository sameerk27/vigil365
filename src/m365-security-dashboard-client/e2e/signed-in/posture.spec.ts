import { test, expect, type Route } from '@playwright/test';
import { signIn, postureSample, tenantA, tenantB, rosterRow, rollupRow } from './fixtures';

/** docs/LICENSE_POSTURE_PLAN.md phase 1: the Posture page and the client report. */

const report = async (route: Route) => {
  await route.fulfill({ status: 200, contentType: 'text/html; charset=utf-8', body: '<!doctype html><title>report</title>' });
};

test('Licence posture: a Viewer sees what needs doing first, the fix, and gets the client report', async ({ page }) => {
  await signIn(page, {
    mode: 'Single', role: 'Viewer',
    api: { 'GET /api/posture': postureSample(), 'GET /api/posture/report.html': report },
  });
  await page.goto('/#/posture');

  const rows = page.getByRole('table').locator('tbody tr');
  await expect(rows).toHaveCount(5);
  await expect(rows.first()).toContainText('Conditional Access requires MFA for all users'); // the Fail leads
  await expect(rows.last()).toContainText('Legacy authentication is blocked');              // the Pass comes last

  await page.getByRole('button', { name: /Needs another licence/ }).click();
  await expect(rows).toHaveCount(1);
  await expect(rows.first()).toContainText('Needs Microsoft Entra ID P2');
  await page.getByRole('button', { name: /Needs another licence/ }).click();
  await expect(rows).toHaveCount(5);

  await page.getByRole('button', { name: 'Conditional Access requires MFA for all users' }).click();
  const dialog = page.getByRole('dialog', { name: 'Conditional Access requires MFA for all users' });
  await expect(dialog).toContainText('it depends on where the sign-in comes from');
  await expect(dialog.getByRole('link', { name: /Open in the admin centre/ })).toHaveAttribute('href', 'https://entra.microsoft.com/');
  await page.screenshot({ path: test.info().outputPath('posture-detail.png'), fullPage: true });
  await page.keyboard.press('Escape');
  await expect(dialog).toHaveCount(0);

  const download = page.waitForEvent('download');
  await page.getByRole('button', { name: /Client report/ }).click();
  expect((await download).suggestedFilename()).toMatch(/^licence-posture-\d{4}-\d{2}-\d{2}\.html$/);
  await page.screenshot({ path: test.info().outputPath('posture.png'), fullPage: true });
});

test('Licence posture in the MSP edition reads the selected client and names the report after it', async ({ page }) => {
  const tenants: (string | undefined)[] = [];
  await signIn(page, {
    mode: 'Msp', role: 'Analyst',
    api: {
      'GET /api/tenants/me': { current: tenantA.id, tenants: [tenantA, tenantB] },
      'GET /api/tenants/rollup': [rollupRow(tenantA), rollupRow(tenantB)],
      'GET /api/tenants': [rosterRow(tenantA), rosterRow(tenantB)],
      'GET /api/posture': (route: Route) => { tenants.push(route.request().headers()['x-vigil-tenant']); return postureSample(); },
      'GET /api/posture/report.html': async (route: Route) => { tenants.push(route.request().headers()['x-vigil-tenant']); await report(route); },
    },
  });
  await page.goto('/#/posture');
  await expect(page.getByRole('table').locator('tbody tr')).toHaveCount(5);

  const download = page.waitForEvent('download');
  await page.getByRole('button', { name: /Client report/ }).click();
  expect((await download).suggestedFilename()).toMatch(/^contoso-ltd-licence-posture-/);
  expect(tenants.length).toBeGreaterThanOrEqual(2);
  expect(tenants.every(t => t === tenantA.id)).toBe(true);
});

test('Licence posture says why every check is Unknown when the licences could not be read', async ({ page }) => {
  await signIn(page, {
    mode: 'Single', role: 'Viewer',
    api: {
      'GET /api/posture': postureSample({
        error: 'The tenant’s licences couldn’t be read: Permission denied: grant the Directory.Read.All application permission.',
        licences: [], includedFeatures: [],
      }),
    },
  });
  await page.goto('/#/posture');
  await expect(page.getByRole('status').filter({ hasText: 'Every check shows as Unknown' })).toContainText('Directory.Read.All');
});
