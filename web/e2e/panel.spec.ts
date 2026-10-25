import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { admin, analyst, apiUrl, countReloads, generationOf, login, setScoring } from './helpers';

test.describe.configure({ mode: 'serial' });

// Сценарии весов и подписи «индекс, не вероятность» относятся к режиму правил; по умолчанию основной балл — ML
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

const fresh = { storageState: { cookies: [], origins: [] } };
const desktop = { viewport: { width: 1440, height: 900 }, locale: 'ru-RU' };

test.describe('без сохранённой сессии', () => {
  test.use(fresh);

  test('вход: защищённая страница ведёт на login, после входа — обзор с KPI', async ({ page }) => {
    await page.goto('/customers');
    await expect(page).toHaveURL(/\/login\?returnUrl=/);
    await login(page);
    await expect(page.getByTestId('kpi-at-risk')).toContainText('€');
    await expect(page.getByTestId('kpi-planned')).toContainText('€');
    await expect(page.getByTestId('level-count-unknown')).toBeVisible();
    await expect(page.getByText('Индекс, не вероятность').first()).toBeVisible();
    await expect(page.getByTestId('live-status')).toHaveAttribute('data-status', 'live');
  });
});

const fields = ['id', 'email', 'balance', 'totalReplenished'] as const;

test('8 направлений обязательной сортировки совпадают с API по всему набору', async ({ page }) => {
  await page.goto('/customers');
  for (const field of fields) {
    for (const dir of ['asc', 'desc'] as const) {
      // Кликаем заголовок до нужного направления; URL — источник истины состояния таблицы.
      for (let i = 0; i < 3 && !decodeURIComponent(page.url()).includes(`sort=${field}:${dir}`); i++) {
        await page.getByTestId(`sort-${field}`).click();
        await page.waitForTimeout(200);
      }
      expect(decodeURIComponent(page.url())).toContain(`sort=${field}:${dir}`);
      const th = page.locator('th', { has: page.getByTestId(`sort-${field}`) });
      await expect(th).toHaveAttribute('aria-sort', dir === 'asc' ? 'ascending' : 'descending');
      const api = await page.request.get(apiUrl(`/api/v1/customers?sortBy=${field}&sortDirection=${dir}&pageSize=50`));
      const expectedIds = ((await api.json()) as { items: { id: number }[] }).items.map((i) => String(i.id));
      await expect(page.locator('[data-testid^="row-"] td:first-child')).toHaveText(expectedIds);
    }
  }
});

test('карточка клиента: причины, вклад и денежная арифметика', async ({ page }) => {
  await page.goto('/customers?sort=riskScore:desc');
  await page.locator('[data-testid^="row-"]').first().click();
  await expect(page).toHaveURL(/\/customers\/\d+$/);
  await expect(page.getByTestId('reason').first()).toBeVisible();
  await expect(page.getByTestId('customer-at-risk')).toContainText('€');
  await expect(page.getByText('Не покрыто балансом U')).toBeVisible();
  await expect(page.getByText('индекс 0–100, не вероятность')).toBeVisible();
});

test('изменение веса в панели: новое поколение без reload у другого пользователя в другом браузере', async ({ page, browser }) => {
  // Отдельный контекст = отдельный браузер с собственной сессией (cookie не общие).
  const other = await browser.newContext({ ...desktop, storageState: { cookies: [], origins: [] } });
  const b = await other.newPage();
  await login(b);
  await b.goto('/customers?level=high,medium,low,unknown&sort=email:desc');
  const genBefore = await generationOf(b);
  const reloadsB = countReloads(b);

  await page.goto('/model');
  const input = page.getByTestId('weight-unpaid_transaction');
  await expect(input).toBeVisible();
  const current = Number(await input.inputValue());
  await input.fill(String(current >= 99 ? current - 1 : current + 1));
  await page.getByTestId('save-model').click();
  await expect(page.getByText(/Ревизия r\d+ (сохранена · пересчёт|в расчёте)/)).toBeVisible();

  await expect.poll(async () => Number(await b.getByTestId('generation-label').getAttribute('data-generation'))).toBeGreaterThan(genBefore);
  expect(reloadsB.loads).toBe(0);
  expect(decodeURIComponent(b.url())).toContain('level=high,medium,low,unknown'); // фильтры сохранились
  expect(decodeURIComponent(b.url())).toContain('sort=email:desc'); // и сортировка
  await expect(b.locator('th[aria-sort="descending"]')).toContainText('Email');
  await expect(page.getByText(/Ревизия r\d+ в расчёте/)).toBeVisible();
  await other.close();
});

test('невалидные пороги: ошибка проверки, ревизия не меняется', async ({ page }) => {
  await page.goto('/model');
  const before = await page.request.get(apiUrl('/api/v1/model/settings')).then((r) => r.json());
  await page.getByTestId('threshold-medium').fill('80');
  await page.getByTestId('save-model').click();
  await expect(page.getByText(/Настройки не прошли проверку/)).toBeVisible();
  const after = await page.request.get(apiUrl('/api/v1/model/settings')).then((r) => r.json());
  expect(after.latest.revision).toBe(before.latest.revision);
});

test('загрузка изменённых тестовых данных через UI → live-обновление второй вкладки', async ({ page, context }) => {
  const b = await context.newPage();
  await b.goto('/');
  const genBefore = await generationOf(b);
  const reloadsB = countReloads(b);

  const original = JSON.parse(readFileSync('../test_data_yandex/data.json', 'utf8'));
  const account = original.accounts.find((x: { id: number }) => x.id === 6);
  account.balance += Date.now() % 1000; // меняем реальный баланс одного клиента (новое содержимое при каждом прогоне)
  const envelope = { schemaVersion: 1, sourceAsOf: new Date().toISOString(), synthetic: true, ...original };
  const file = join(tmpdir(), `aeza-e2e-${Date.now()}.json`);
  writeFileSync(file, JSON.stringify(envelope));

  await page.goto('/source');
  await page.getByTestId('snapshot-file').setInputFiles(file);
  await page.getByTestId('validate-snapshot').click();
  await expect(page.getByTestId('validation-report')).toContainText('Проверка пройдена');
  await page.getByTestId('replace-snapshot').click();
  await page.getByTestId('confirm-replace').click();
  await expect(page.getByText(/Тестовые данные загружены: ревизия r\d+/)).toBeVisible();

  await expect.poll(async () => Number(await b.getByTestId('generation-label').getAttribute('data-generation'))).toBeGreaterThan(genBefore);
  expect(reloadsB.loads).toBe(0);
});

test('доступность: обзор, рейтинг, карточка, модель, источник — без серьёзных нарушений axe (WCAG A/AA)', async ({ page }) => {
  for (const path of ['/', '/customers', '/customers/1', '/model', '/source']) {
    await page.goto(path);
    await page.waitForTimeout(600);
    const r = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa']).analyze();
    const serious = r.violations.filter((v) => v.impact === 'critical' || v.impact === 'serious');
    expect(serious.map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(' ')).slice(0, 3).join(' | ')}`), path).toEqual([]);
  }
});

test('офлайн → опрос, восстановление сети → live', async ({ page, context }) => {
  await page.goto('/customers');
  await expect(page.getByTestId('live-status')).toHaveAttribute('data-status', 'live');
  await context.setOffline(true);
  await expect(page.getByTestId('live-status')).not.toHaveAttribute('data-status', 'live', { timeout: 45_000 });
  await context.setOffline(false);
  await expect(page.getByTestId('live-status')).toHaveAttribute('data-status', 'live', { timeout: 60_000 });
});

test.describe('отдельные сессии', () => {
  test.use(fresh);

  test('аналитик видит данные, но не может менять модель', async ({ page }) => {
    test.skip(!analyst.login, 'аналитик не настроен в .env');
    await login(page, analyst);
    await page.goto('/model');
    await expect(page.getByText(/Менять режим, пороги и веса может администратор модели/)).toBeVisible();
    await expect(page.getByTestId('scoring-rules')).toHaveAttribute('aria-disabled', 'true');
    await expect(page.getByTestId('save-model')).toHaveCount(0);
  });

  test('выход завершает сессию: API снова 401', async ({ page }) => {
    await login(page);
    await page.getByTestId('logout').click();
    await expect(page).toHaveURL(/\/login/);
    expect((await page.request.get(apiUrl('/api/v1/customers'))).status()).toBe(401);
    expect(admin.login).toBeTruthy();
  });
});

test('выпадающий список фильтра: клавиатура открывает, выбирает и закрывает', async ({ page }) => {
  await page.goto('/customers');
  const trigger = page.getByTestId('filter-state');
  await trigger.focus();
  await page.keyboard.press('ArrowDown');
  await expect(trigger).toHaveAttribute('aria-expanded', 'true');
  await expect(page.getByRole('listbox')).toBeVisible();
  await page.keyboard.press('ArrowDown');
  await page.keyboard.press('Enter');
  await expect(trigger).toHaveAttribute('aria-expanded', 'false');
  await expect(trigger).toBeFocused();
  await expect(page).toHaveURL(/state=/);
  await trigger.click();
  await page.keyboard.press('Escape');
  await expect(page.getByRole('listbox')).toHaveCount(0);
  await expect(page).toHaveURL(/state=/);
});
