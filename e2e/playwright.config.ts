import { defineConfig, devices } from '@playwright/test';

const port = Number(process.env.E2E_PORT ?? 5080);
// The API runs in Development, so /auth/dev signs in as alice, bob or carol without credentials. Its data lives in memory and is gone after the run.
const apiPort = Number(process.env.E2E_API_PORT ?? 5190);
const api = `http://localhost:${apiPort}`;

// WebKit is the engine behind every browser on iPhone and iPad, so these profiles catch Safari-only problems
// without a Mac or device farm. Both projects run the same smoke flows.
export default defineConfig({
  testDir: './tests',
  // Blazor WebAssembly downloads and starts the .NET runtime on first load, which is slow on a CI runner.
  timeout: 120_000,
  expect: { timeout: 30_000 },
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
  webServer: [
    {
      command: `dotnet run --project ../src/Dialed.Api/Dialed.Api.csproj --no-launch-profile --urls ${api}`,
      url: api,
      env: { ASPNETCORE_ENVIRONMENT: 'Development' },
      reuseExistingServer: !process.env.CI,
      // Builds the API first when needed.
      timeout: 240_000,
    },
    {
      command: 'node serve.mjs',
      url: `http://localhost:${port}`,
      env: { E2E_API: api },
      reuseExistingServer: !process.env.CI,
      timeout: 30_000,
    },
  ],
});
