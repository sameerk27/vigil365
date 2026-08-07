import { test, expect } from '@playwright/test';

test.describe('Core User Flows', () => {

  test('should load the application and show authentication or landing page', async ({ page }) => {
    await page.goto('/');
    
    // We expect the page to load without crashing and show some Vigil365 branding or login
    await expect(page).toHaveTitle(/Vigil365|Login|Sign In/i);
    
    // Check if there's a sign-in button or redirect
    const url = page.url();
    if (url.includes('login.microsoftonline.com')) {
      console.log('Redirected to Microsoft Login successfully.');
      return;
    }
    
    // Check for login button on our own UI
    const loginBtn = page.locator('button:has-text("Sign in"), button:has-text("Login")');
    if (await loginBtn.count() > 0) {
      await expect(loginBtn.first()).toBeVisible();
    }
  });

  test('should handle invalid paths gracefully', async ({ page }) => {
    // Navigate to a random 404 path
    await page.goto('/some-invalid-path-12345');
    
    // It should either redirect to login, show a 404, or load the dashboard shell
    const bodyText = await page.textContent('body');
    expect(bodyText).toBeTruthy();
    
    // No blank white screens of death (React crashes)
    const rootHasContent = await page.locator('#root').innerHTML();
    expect(rootHasContent.length).toBeGreaterThan(0);
  });

});
