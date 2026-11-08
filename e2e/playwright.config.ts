import { defineConfig, devices } from '@playwright/test';

const port = Number(process.env.E2E_PORT ?? 5080);

// WebKit is the engine behind every browser on iPhone and iPad, so these profiles catch Safari-only problems
// without a Mac or device farm. Both projects run the same smoke flows.
export default defineConfig({
  testDir: './tests',
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? [['list'], ['html', { open: 'never' }]] : 'list',
  use: {
    baseURL: `http://localhost:${port}`,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [
    { name: 'iPhone', use: { ...devices['iPhone 15'] } },
    { name: 'iPad', use: { ...devices['iPad (gen 7)'] } },
  ],
  webServer: {
    command: 'node serve.mjs',
    url: `http://localhost:${port}`,
    reuseExistingServer: !process.env.CI,
    timeout: 30_000,
  },
});
