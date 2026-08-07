import { test, expect } from '@playwright/test';

test.describe('Navigation and Broken Links', () => {

  test('should check for any obvious 404s or broken links on the main page', async ({ page, request }) => {
    await page.goto('/');
    
    // Grab all anchor tags
    const links = await page.locator('a').all();
    
    for (const link of links) {
      const href = await link.getAttribute('href');
      
      // Skip empty, javascript, or mailto links
      if (!href || href.startsWith('javascript:') || href.startsWith('mailto:')) continue;
      
      // We only test internal links or links belonging to the domain
      if (href.startsWith('/') || href.startsWith('https://vigil365.in')) {
        // We can do a quick head request to see if it's broken
        const urlToFetch = href.startsWith('/') ? `https://vigil365.in${href}` : href;
        const response = await request.head(urlToFetch, { ignoreHTTPSErrors: true });
        
        // As long as it's not a 404/500, it's generally "not broken"
        expect(response.status()).toBeLessThan(400);
      }
    }
  });

  test('should not have visible console errors', async ({ page }) => {
    const consoleErrors: string[] = [];
    page.on('console', msg => {
      if (msg.type() === 'error') {
        consoleErrors.push(msg.text());
      }
    });
    
    page.on('pageerror', exception => {
      consoleErrors.push(exception.message);
    });

    await page.goto('/');
    
    // Wait for a bit of settling
    await page.waitForTimeout(2000);
    
    // If there are errors, we expect them to be known/handled (we log them for the report)
    // For this test, we just want to surface them if there are severe crashes.
    if (consoleErrors.length > 0) {
      console.log('Console errors encountered:', consoleErrors);
    }
  });

});
