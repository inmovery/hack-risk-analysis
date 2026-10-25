import { expect, test } from '@playwright/test';
import { analyst, countReloads, login } from './helpers';

// Страница «Симуляция» — против работающего стенда; snapshot системы не пересоздаётся (это удалило бы данные стенда).
test.describe.configure({ mode: 'serial' });

test('симуляция: статус и настройки видны, такт сохраняется без перезагрузки', async ({ page }) => {
  const loads = countReloads(page);
  await page.goto('/simulation');
  await expect(page.getByRole('heading', { name: 'Симуляция', level: 1 })).toBeVisible();
  await expect(page.getByTestId('sim-status')).toBeVisible();
  await expect(page.getByText('● работает постоянно')).toBeVisible();
  await expect(page.getByTestId('sim-running')).toHaveCount(0);
  await expect(page.getByTestId('sim-world')).toBeVisible();
  await expect(page.getByTestId('sim-reset')).toBeVisible();

  const tick = page.getByTestId('sim-tick');
  const before = Number(await tick.inputValue());
  const next = before >= 60 ? before - 1 : before + 1;
  await tick.fill(String(next));
  await page.getByTestId('sim-save').click();
  await expect(page.getByTestId('sim-save')).toBeDisabled();
  await page.reload();
  await expect(page.getByTestId('sim-tick')).toHaveValue(String(next));

  // вернуть как было
  await page.getByTestId('sim-tick').fill(String(before));
  await page.getByTestId('sim-save').click();
  await expect(page.getByTestId('sim-save')).toBeDisabled();
  expect(loads.loads).toBe(2);
});

test('конструктор: собранное пополнение уходит в поток через симулятор', async ({ page }) => {
  await page.goto('/simulation');
  await expect(page.getByTestId('sim-status')).toHaveAttribute('data-online', 'true', { timeout: 60_000 });
  const submit = page.getByTestId('composer-submit');
  await expect(submit).toBeDisabled();
  await page.getByTestId('composer-type').click();
  await page.getByTestId('composer-type-option-balance.topped_up').click();
  await page.getByTestId('composer-account').click();
  await page.getByTestId('composer-account-list').getByRole('option').first().click();
  const account = await page.getByTestId('composer-account').getAttribute('data-value');
  await page.getByTestId('composer-amount').fill('7');
  await expect(page.getByTestId('composer-preview')).toContainText('"amount": 7');
  await submit.click();
  await expect(page.getByTestId('composer-sent')).toBeVisible();
  const id = (await page.getByTestId('composer-sent').textContent())!.match(/№(\d+)/)![1];
  const row = page.getByTestId(`composed-${id}`);
  await expect(row).toBeVisible();
  // Симулятор применяет событие при следующем опросе и сообщает итог
  await expect(row).toHaveAttribute('data-status', 'applied', { timeout: 60_000 });
  await expect(row).toContainText(`#${account}`);
  await expect(row).toContainText('в потоке');
});

test('конструктор: событие услуги требует услугу; удалённые недоступны', async ({ page }) => {
  await page.goto('/simulation');
  await page.getByTestId('composer-type').click();
  await page.getByTestId('composer-type-option-service.suspended').click();
  await expect(page.getByTestId('composer-service')).toHaveAttribute('aria-disabled', 'true');
  await page.getByTestId('composer-account').click();
  await page.getByTestId('composer-account-list').getByRole('option').first().click();
  await expect(page.getByTestId('composer-service')).not.toHaveAttribute('aria-disabled', 'true');
  await expect(page.getByTestId('composer-submit')).toBeDisabled();
  await page.getByTestId('composer-service').click();
  const options = page.getByTestId('composer-service-list').getByRole('option');
  await expect(options.first()).toBeVisible();
  for (const o of await options.all()) if ((await o.textContent())?.includes('удалена')) await expect(o).toHaveAttribute('aria-disabled', 'true');
  await page.keyboard.press('Escape');
});

test.describe('аналитик', () => {
  test.use({ storageState: { cookies: [], origins: [] } });

  test('видит симуляцию, но не меняет её', async ({ page }) => {
    test.skip(!analyst.login, 'аналитик не настроен в .env');
    await login(page, analyst);
    await page.goto('/simulation');
    await expect(page.getByText(/Менять настройки симуляции может администратор/)).toBeVisible();
    await expect(page.getByTestId('sim-save')).toHaveCount(0);
    await expect(page.getByTestId('sim-scenario')).toBeDisabled();
    await expect(page.getByTestId('composer-submit')).toHaveCount(0);
  });
});
