import { test, expect, type Page } from '@playwright/test';

/**
 * The real (non-e2e) build before sign-in. Nothing here needs a backend or a
 * Microsoft account: /api/auth/config is stubbed with an app registration, which
 * is what makes the app show its Microsoft sign-in screen. Without the stub the
 * dev proxy's backend is unreachable, the sign-in screen never renders, and a
 * broken login screen would go unnoticed.
 */
async function stubSignInConfig(page: Page) {
  await page.route('**/api/**', route => {
    const url = new URL(route.request().url());
    if (url.pathname === '/api/auth/config')
      return route.fulfill({ json: { instance: 'https://login.microsoftonline.com/', clientId: 'e2e-client', tenantId: 'e2e-tenant', redirectUri: url.origin, mode: 'Single' } });
    return route.fulfill({ status: 401, json: { error: 'signed out' } });
  });
}

test.describe('Before sign-in', () => {
  test('an install with sign-in configured shows the Microsoft sign-in screen', async ({ page }) => {
    await stubSignInConfig(page);
    await page.goto('/');
    await expect(page).toHaveTitle(/Vigil365/);
    await expect(page.getByRole('heading', { name: 'Sign in', exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Sign in with Microsoft' })).toBeEnabled();
    // None of the dashboard is shown before sign-in.
    await expect(page.getByRole('navigation')).toHaveCount(0);
  });

  test('an unknown path still lands on the sign-in screen, not a blank page', async ({ page }) => {
    await stubSignInConfig(page);
    await page.goto('/some-invalid-path-12345');
    await expect(page.getByRole('button', { name: 'Sign in with Microsoft' })).toBeVisible();
  });

  test('the sign-in screen loads without console errors or uncaught exceptions', async ({ page }) => {
    const errors: string[] = [];
    page.on('console', msg => { if (msg.type() === 'error') errors.push(msg.text()); });
    page.on('pageerror', e => errors.push(e.message));
    await stubSignInConfig(page);
    await page.goto('/');
    await expect(page.getByRole('button', { name: 'Sign in with Microsoft' })).toBeVisible();
    expect(errors).toEqual([]);
  });
});
