import { defineConfig, devices } from '@playwright/test';
import { existsSync, readFileSync } from 'node:fs';

// Учётные данные берутся из ../.env (локальный стенд Compose) или переменных окружения CI; в отчёт не попадают.
if (existsSync('../.env')) {
  for (const line of readFileSync('../.env', 'utf8').split(/\r?\n/)) {
    const m = line.match(/^([A-Z0-9_]+)=(.*)$/);
    if (m && !process.env[m[1]!]) process.env[m[1]!] = m[2];
  }
}

const desktop = { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 900 }, locale: 'ru-RU' };

export default defineConfig({
  testDir: './e2e',
  fullyParallel: false,
  workers: 1,
  timeout: 90_000,
  expect: { timeout: 20_000 },
  reporter: [['list'], ['html', { open: 'never', outputFolder: 'playwright-report' }]],
  use: {
    baseURL: process.env.E2E_BASE_URL ?? 'http://localhost:8080',
    // Локальная проверка через nginx с самоподписанным сертификатом.
    ignoreHTTPSErrors: process.env.E2E_IGNORE_HTTPS_ERRORS === '1',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [
    { name: 'setup', testMatch: /auth\.setup\.ts/, use: desktop },
    { name: 'chromium', dependencies: ['setup'], testIgnore: /auth\.setup\.ts/, use: { ...desktop, storageState: 'e2e/.auth/admin.json' } },
  ],
});
