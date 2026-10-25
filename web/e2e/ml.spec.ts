import { expect, test } from '@playwright/test';
import { readFileSync } from 'node:fs';
import { setScoring } from './helpers';

// Страница «Модель» и ML — против работающего стенда (Compose): основной балл по умолчанию — ML, правила включаются явно.
test.describe.configure({ mode: 'serial' });

const bundle = readFileSync('../test/Aeza.Risk.IntegrationTests/Fixtures/ml/model-bundle-fasttree.zip');

test('модель: ML по умолчанию, правила скрыты до включения, переключение через диалог', async ({ page }) => {
  await page.goto('/model');
  const card = page.getByTestId('ml-status');
  await expect(card).toBeVisible();
  // Битый пакет — понятная ошибка, действующая модель не меняется
  const statusBefore = await card.getAttribute('data-status');
  await page.getByTestId('ml-file').setInputFiles({ name: 'broken.zip', mimeType: 'application/zip', buffer: Buffer.from('not a zip') });
  await expect(card.getByRole('alert')).toContainText('bundle_invalid');
  await expect(card).toHaveAttribute('data-status', statusBefore ?? '');
  if (statusBefore !== 'shadow') {
    await page.getByTestId('ml-file').setInputFiles({ name: 'model-bundle.zip', mimeType: 'application/zip', buffer: bundle });
    await expect(card).toHaveAttribute('data-status', 'shadow');
  }
  await setScoring(page.request, 'ml');
  await page.reload();

  // ML: выбран, правила выключены, общие настройки доступны, предпросмотра нет
  const scoring = page.getByTestId('scoring');
  await expect(scoring).toHaveAttribute('data-primary', 'ml');
  await expect(scoring).toHaveAttribute('data-used', 'ml', { timeout: 30_000 });
  await expect(page.getByTestId('scoring-ml')).toHaveAttribute('aria-checked', 'true');
  await expect(page.getByTestId('rules-off')).toBeVisible();
  await expect(page.getByTestId('rules-editor')).toHaveCount(0);
  await expect(page.getByTestId('preview-model')).toHaveCount(0);
  await expect(page.getByTestId('threshold-high')).toBeEnabled();
  await expect(page.getByTestId('churn-settings')).toBeVisible();
  await expect(card).toContainText('основной балл');
  await expect(card).not.toContainText('синтетик');
  await expect(card.getByTestId('ml-offline')).toContainText('Модель');

  // Обзор без напоминаний о режиме и источнике данных
  await page.goto('/');
  await expect(page.getByTestId('kpi-at-risk')).toBeVisible();
  await expect(page.getByTestId('scoring-ml-banner')).toHaveCount(0);
  await expect(page.getByText(/Среди данных есть синтетика/)).toHaveCount(0);
  await page.goto('/customers');
  await expect(page.getByText('баллы: ML-модель')).toBeVisible();

  // Включить правила: диалог → Отмена ничего не меняет
  await page.goto('/model');
  await page.getByTestId('enable-rules').click();
  const dialog = page.getByTestId('mode-dialog');
  await expect(dialog).toBeVisible();
  await expect(dialog).toContainText('Включить правила?');
  await dialog.getByRole('button', { name: 'Отмена' }).click();
  await expect(dialog).not.toBeVisible();
  await expect(scoring).toHaveAttribute('data-primary', 'ml');

  // Включить правила: подтверждение → редактор весов и предпросмотр
  await page.getByTestId('scoring-rules').click();
  await page.getByTestId('mode-confirm').click();
  await expect(scoring).toHaveAttribute('data-primary', 'rules');
  await expect(scoring).toHaveAttribute('data-used', 'rules', { timeout: 30_000 });
  await expect(page.getByTestId('rules-editor')).toBeVisible();
  await expect(page.getByTestId('weight-unpaid_transaction')).toBeEnabled();
  await expect(page.getByTestId('rules-off')).toHaveCount(0);
  await expect(card).toContainText('рядом с правилами');
  await page.goto('/');
  await expect(page.getByText('Индекс, не вероятность').first()).toBeVisible();

  // Вернуть ML
  await page.goto('/model');
  await page.getByTestId('scoring-ml').click();
  await expect(page.getByTestId('mode-dialog')).toContainText('Вернуть ML-модель?');
  await page.getByTestId('mode-confirm').click();
  await expect(scoring).toHaveAttribute('data-used', 'ml', { timeout: 30_000 });
  await expect(page.getByTestId('rules-off')).toBeVisible();
  await page.goto('/customers/1');
  await expect(page.getByRole('heading', { name: 'Услуги', exact: true })).toBeVisible();
});

test('модель: карточки режима управляются с клавиатуры, Esc закрывает диалог', async ({ page }) => {
  await page.goto('/model');
  const ml = page.getByTestId('scoring-ml');
  await ml.focus();
  await page.keyboard.press('ArrowRight');
  await expect(page.getByTestId('scoring-rules')).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(page.getByTestId('mode-dialog')).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(page.getByTestId('mode-dialog')).not.toBeVisible();
  await expect(page.getByTestId('scoring')).toHaveAttribute('data-primary', 'ml');
});
