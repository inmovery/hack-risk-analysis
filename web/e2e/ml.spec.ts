import { expect, test } from '@playwright/test';
import { readFileSync } from 'node:fs';

// Теневая ML-модель — против работающего стенда (Compose); фикстура — логистическая модель на синтетике.
test.describe.configure({ mode: 'serial' });

const bundle = readFileSync('../test/Aeza.Risk.IntegrationTests/Fixtures/ml/model-bundle.zip');

test('модель в тени: загрузка пакета, сравнение с правилами, карточка клиента, выключение', async ({ page }) => {
  await page.goto('/model');
  const card = page.getByTestId('ml-status');
  await expect(card).toBeVisible();
  if ((await card.getAttribute('data-status')) === 'shadow') {
    await page.getByTestId('ml-off').click();
    await expect(card).toHaveAttribute('data-status', 'not_trained');
  }

  // Битый пакет — понятная ошибка, модель не включается
  await page.getByTestId('ml-file').setInputFiles({ name: 'broken.zip', mimeType: 'application/zip', buffer: Buffer.from('not a zip') });
  await expect(card.getByRole('alert')).toContainText('bundle_invalid');
  await expect(card).toHaveAttribute('data-status', 'not_trained');

  await page.getByTestId('ml-file').setInputFiles({ name: 'model-bundle.zip', mimeType: 'application/zip', buffer: bundle });
  await expect(card).toHaveAttribute('data-status', 'shadow');
  await expect(card).toContainText('синтетике');
  await expect(card.getByTestId('ml-offline')).toContainText('Правила (панель)');
  await expect(card.getByTestId('ml-offline')).toContainText('Модель');
  await expect(card.getByTestId('ml-top-features').locator('li')).toHaveCount(5);
  // Пересчёт с моделью приходит live, без перезагрузки страницы
  await expect(card.getByTestId('ml-generation')).toContainText('Услуг в окне модели', { timeout: 30_000 });
  await expect(card.getByTestId('ml-online')).toContainText('Прогнозов в точках решения');

  await page.goto('/customers/1');
  await expect(page.getByTestId('ml-shadow')).toBeVisible();
  await expect(page.getByTestId('ml-shadow')).toContainText('не влияет на score');

  await page.goto('/model');
  await page.getByTestId('ml-off').click();
  await expect(card).toHaveAttribute('data-status', 'not_trained');
  await page.goto('/customers/1');
  await expect(page.getByRole('heading', { name: 'Услуги', exact: true })).toBeVisible();
  await expect(page.getByTestId('ml-shadow')).toHaveCount(0, { timeout: 30_000 });
});
