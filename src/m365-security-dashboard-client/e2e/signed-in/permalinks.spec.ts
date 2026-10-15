import { test, expect } from '@playwright/test';
import { signIn } from './fixtures';

/** The hash router: alert permalinks must not swallow entity drill-down routes. */

const upn = 'megan@contoso.test';
const alert = {
  id: 7, alertType: 'riskyUser', service: 'EntraId', severity: 'High', title: 'Risky user detected',
  userPrincipalName: upn, detectedAt: '2026-10-02T08:00:00Z', lastUpdatedAt: '2026-10-02T08:00:00Z', isResolved: false,
};

test('"Investigate →" on an alert opens the user\'s investigation page', async ({ page }) => {
  await signIn(page, { mode: 'Single', role: 'Analyst', api: { 'GET /api/alerts': { items: [alert], total: 1 } } });
  await page.goto('/#/incidents?alert=7');
  const modal = page.locator('.detail-modal');
  await expect(modal).toContainText('Risky user detected');

  // Closes the alert and sets the entity route in one click.
  await modal.getByRole('button', { name: 'Investigate →' }).click();
  await expect(page.getByRole('heading', { level: 1, name: upn })).toBeVisible();
  await expect(page).toHaveURL(/#\/entity\/user\/megan%40contoso\.test\?fromAlert=7$/);
});

test('a shared entity link keeps its address, so a refresh lands on the same page', async ({ page }) => {
  await signIn(page, { mode: 'Single', role: 'Analyst' });
  await page.goto(`/#/entity/user/${encodeURIComponent(upn)}`);
  await expect(page.getByRole('heading', { level: 1, name: upn })).toBeVisible();
  await expect(page).toHaveURL(/#\/entity\/user\/megan%40contoso\.test$/);

  await page.reload();
  await expect(page.getByRole('heading', { level: 1, name: upn })).toBeVisible();
});
