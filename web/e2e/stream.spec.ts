import { expect, test } from '@playwright/test';
import { apiUrl, countReloads, setScoring } from './helpers';

// Поток событий, типы оттока и выгрузка — против работающего стенда (Compose).
test.describe.configure({ mode: 'serial' });

// Объяснения индексов оттока проверяются в режиме правил; по умолчанию основной балл — ML
test.beforeAll(async ({ browser }) => {
  const ctx = await browser.newContext({ storageState: 'e2e/.auth/admin.json' });
  await setScoring(ctx.request, 'rules');
  await ctx.close();
});
test.afterAll(async ({ browser }) => {
  const ctx = await browser.newContext({ storageState: 'e2e/.auth/admin.json' });
  await setScoring(ctx.request, 'ml');
  await ctx.close();
});

const ingestToken = process.env.INGEST_TOKEN ?? '';

test('событие источника через API → журнал, карточка и факт без перезагрузки', async ({ page }) => {
  test.skip(!ingestToken, 'INGEST_TOKEN не задан в .env');
  const loads = countReloads(page);
  await page.goto('/source');
  await expect(page.getByTestId('connectors')).toBeVisible();
  await expect(page.getByTestId('connector-http-push')).toBeVisible();

  const detail = await (await page.request.get(apiUrl('/api/v1/customers/6'))).json();
  const acc = detail.detail.customer;
  const eventId = `e2e-${Date.now()}`;
  const r = await page.request.post(apiUrl('/api/v1/source/events'), {
    headers: { Authorization: `Bearer ${ingestToken}` },
    data: {
      synthetic: true,
      events: [{ eventId, type: 'account', data: { ...acc, balance: (acc.balance ?? 0) + 100, totalReplenished: (acc.totalReplenished ?? 0) + 100 } }],
    },
  });
  expect(r.status()).toBe(202);
  const body = await r.json();
  expect(body.applied).toBe(1);

  // Журнал обновился live: новая строка сверху с пополнением
  await expect(page.getByTestId('journal-row').first()).toContainText('пополнение', { timeout: 20_000 });
  await expect(page.getByTestId('connector-http-push')).toHaveAttribute('data-status', 'ok');

  // Повтор того же eventId — дубликат, без изменений
  const dup = await page.request.post(apiUrl('/api/v1/source/events'), {
    headers: { Authorization: `Bearer ${ingestToken}` },
    data: { events: [{ eventId, type: 'account', data: acc }] },
  });
  expect((await dup.json()).duplicates).toBe(1);

  await page.getByRole('link', { name: '#6' }).first().click();
  await expect(page.getByTestId('source-timeline')).toContainText('пополнение');
  await page.getByRole('link', { name: 'Обзор' }).click();
  await page.getByTestId('tab-fact').click();
  await expect(page.getByTestId('fact-chart')).toBeVisible();
  await expect(page.getByTestId('churn-types')).toBeVisible();
  expect(loads.loads).toBe(1);
});

test('типы оттока: колонки, фильтр «уже ушли» и выгрузка CSV с теми же фильтрами', async ({ page }) => {
  await page.goto('/customers');
  const table = page.getByTestId('customers-table');
  await expect(table.getByRole('columnheader', { name: /Уход целиком/ })).toBeVisible();
  await expect(table.getByRole('columnheader', { name: /Платежи/ })).toBeVisible();

  await page.getByTestId('filter-churned').click();
  await expect(page).toHaveURL(/fcstate=churned/);
  const rows = page.locator('[data-testid^="row-"]');
  const chips = page.locator('[data-testid^="fc-"]');
  await expect(chips.first()).toHaveText(/Ушёл/);
  await expect(chips.filter({ hasNotText: 'Ушёл' })).toHaveCount(0);

  const href = await page.getByTestId('export-csv').getAttribute('href');
  expect(href).toContain('fullChurnState=churned');
  const csv = await page.request.get(href!);
  expect(csv.status()).toBe(200);
  expect(csv.headers()['content-type']).toContain('text/csv');
  const lines = (await csv.text()).replace(/^﻿/, '').trim().split('\r\n');
  expect(lines[0]).toContain('full_churn_state');
  expect(lines.length - 1).toBe(await rows.count());
  expect(lines.slice(1).every((l) => l.includes(',churned,'))).toBe(true);
});

test('карточка клиента: три типа оттока с объяснениями и журнал источника', async ({ page }) => {
  await page.goto('/customers/5');
  const chips = page.getByTestId('churn-chips');
  await expect(chips).toContainText('Непродление');
  await expect(chips).toContainText('Уход целиком');
  await expect(chips).toContainText('Платежи');
  await expect(page.getByTestId('churn-types-card')).toContainText('только если уйдут все');
  await expect(page.getByTestId('source-timeline')).toBeVisible();
});

test('модель: определения оттока и статус ML видны; источник: только входящие коннекторы и журнал', async ({ page }) => {
  await page.goto('/model');
  await expect(page.getByTestId('churn-settings')).toBeVisible();
  await expect(page.getByTestId('churn-n')).toHaveValue(/\d+/);
  await expect(page.getByTestId('ml-status')).toContainText('ML-модель');
  await expect(page.getByTestId('ml-status')).toContainText(/Исходов созрело|Отложенный период/);
  await page.goto('/source');
  await expect(page.getByTestId('connector-snapshot')).toBeVisible();
  await expect(page.getByTestId('connector-http-push')).toBeVisible();
  // Исходящего pull-коннектора и его API нет
  await expect(page.locator('[data-testid^="connector-pull"]')).toHaveCount(0);
  expect((await page.request.post(apiUrl('/api/v1/source/pull'), { data: { accountId: 1 } })).status()).toBe(404);
  await expect(page.getByTestId('journal')).toBeVisible();
});
