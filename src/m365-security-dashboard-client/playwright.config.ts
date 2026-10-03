import { defineConfig, devices } from '@playwright/test';

/**
 * Two projects:
 *  - chromium: the normal dev build before sign-in (real MSAL path), with
 *    /api/auth/config stubbed so the Microsoft sign-in screen renders.
 *  - signed-in: journeys past sign-in. Runs a separate dev server built with
 *    VITE_E2E_FAKE_AUTH=1 (MSAL bypassed, see AuthGate in main.tsx) and every
 *    /api call stubbed by e2e/signed-in/fixtures.ts, so no backend or Microsoft
 *    account is needed.
 */
export default defineConfig({
  testDir: './e2e',
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 2 : 0,
  workers: process.env.CI ? 1 : undefined,
  reporter: 'list',
  use: {
    trace: 'on-first-retry',
    ignoreHTTPSErrors: true,
  },
  webServer: [
    {
      command: 'npm run dev',
      url: 'http://localhost:5173',
      reuseExistingServer: !process.env.CI,
    },
    {
      command: 'npx vite --port 5174 --strictPort',
      url: 'http://localhost:5174',
      reuseExistingServer: !process.env.CI,
      env: { VITE_E2E_FAKE_AUTH: '1' },
    },
  ],
  projects: [
    {
      name: 'chromium',
      testIgnore: /signed-in\//,
      use: { ...devices['Desktop Chrome'], baseURL: process.env.PLAYWRIGHT_TEST_BASE_URL || 'http://localhost:5173' },
    },
    {
      name: 'signed-in',
      testMatch: /signed-in\/.*\.spec\.ts/,
      use: { ...devices['Desktop Chrome'], baseURL: 'http://localhost:5174' },
    },
  ],
});
