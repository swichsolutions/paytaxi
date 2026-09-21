import { defineConfig, devices } from '@playwright/test';

/**
 * End-to-end suite: the driver app and the admin console, driven in a real browser against the
 * Angular dev server and the .NET API.
 *
 * Servers:
 *  - Angular: started here (`ng serve`) unless one is already listening on :4200.
 *  - API:     must already be listening on :5196. Locally that is your normal `dotnet run`;
 *             in CI the workflow starts it against the Postgres service with mock rates set to 0.
 *
 * Locally, set PW_CHROMIUM to a chrome.exe if the bundled download is missing
 * (e.g. C:/Users/<you>/AppData/Local/ms-playwright/chromium-NNNN/chrome-win64/chrome.exe).
 *
 * Seeded drivers each get 3 OTP codes per 10 minutes on a dev API; the specs use one phone per
 * spec file and log in once per file, so a whole run costs one code per driver.
 */
export default defineConfig({
  testDir: './e2e',
  timeout: 60_000,
  expect: { timeout: 10_000 },
  fullyParallel: false,
  workers: 1,
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? [['github'], ['html', { open: 'never' }]] : [['list']],
  use: {
    baseURL: 'http://localhost:4200',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    launchOptions: process.env.PW_CHROMIUM ? { executablePath: process.env.PW_CHROMIUM } : {},
  },
  projects: [
    // Driver app + admin-on-a-phone checks at 360 px.
    { name: 'phone', testMatch: /(driver|admin-phone)\.spec\.ts/, use: { ...devices['Desktop Chrome'], viewport: { width: 360, height: 780 } } },
    // Admin console proper.
    { name: 'desktop', testMatch: /(admin|alerts)\.spec\.ts/, use: { ...devices['Desktop Chrome'], viewport: { width: 1280, height: 900 } } },
  ],
  webServer: [
    {
      command: 'npx ng serve --port 4200',
      url: 'http://localhost:4200/login',
      reuseExistingServer: true,
      timeout: 240_000,
    },
    {
      // Only waits for it; the API is started outside (see above). 401 counts as "up".
      command: process.platform === 'win32' ? 'cmd /c "timeout /t 1 >nul"' : 'sleep 1',
      url: 'http://localhost:5196/api/driver/me',
      reuseExistingServer: true,
      timeout: 120_000,
    },
  ],
});
