import { expect, test, type Page } from '@playwright/test';
import { admin, apiUrl } from './helpers';

function cspErrors(page: Page) {
  const errors: string[] = [];
  page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });
  page.on('pageerror', (e) => errors.push(e.message));
  return () => errors.filter((e) => /Content Security Policy|EvalError|unsafe-eval/i.test(e));
}

async function execute(page: Page, operationId: string) {
  const op = page.locator(`#${operationId}`);
  if (!(await op.locator('.opblock-body').isVisible())) await op.locator('.opblock-summary').click();
  await op.getByRole('button', { name: 'Execute' }).click();
  return op.locator('.live-responses-table .response .response-col_status').first();
}

test.describe('без входа', () => {
  test.use({ storageState: { cookies: [], origins: [] } });

  test('Swagger UI на /api/v1/docs открывается сразу (не форма входа панели); вход — из Swagger', async ({ page }) => {
    const csp = cspErrors(page);
    await page.goto(apiUrl('/api/v1/docs'));
    await expect(page).toHaveURL(/\/api\/v1\/docs\/index\.html/);
    await expect(page.locator('.swagger-ui .info .title')).toContainText('Aeza Risk Analyzer API');
    expect(csp()).toEqual([]);

    await expect(await execute(page, 'operations-Auth-GetMe')).toHaveText('401');

    const loginOp = page.locator('#operations-Auth-Login');
    await loginOp.locator('.opblock-summary').click();
    await loginOp.locator('textarea.body-param__text').fill(JSON.stringify({ login: admin.login, password: admin.password }));
    await expect(await execute(page, 'operations-Auth-Login')).toHaveText('200');
    await expect(await execute(page, 'operations-Auth-GetMe')).toHaveText('200');
  });
});

test('Swagger UI с сессией: Try it out и изменяющий запрос с X-CSRF-TOKEN из cookie', async ({ page }) => {
  const csp = cspErrors(page);
  await page.goto(apiUrl('/api/v1/docs'));
  await expect(page.locator('.swagger-ui .info .title')).toContainText('Aeza Risk Analyzer API');
  expect(csp()).toEqual([]);
  await expect(await execute(page, 'operations-Auth-GetMe')).toHaveText('200');

  // Тот же приём, что у requestInterceptor: X-CSRF-TOKEN из cookie XSRF-TOKEN API-хоста.
  const status = await page.evaluate(async () => {
    const m = document.cookie.match(/(?:^|; )XSRF-TOKEN=([^;]+)/);
    const settings = await (await fetch('/api/v1/model/settings')).json();
    const r = await fetch('/api/v1/model/settings', {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': m ? decodeURIComponent(m[1]!) : '' },
      body: JSON.stringify({ baseRevision: settings.latest.revision, weights: { unpaid_transaction: (settings.latest.settings.weights.unpaid_transaction + 1) % 100 } }),
    });
    return r.status;
  });
  expect(status).toBe(202);
});
