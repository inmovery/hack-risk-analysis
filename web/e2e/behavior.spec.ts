import { expect, test } from '@playwright/test';
import { apiUrl, countReloads, setScoring } from './helpers';

// Поведение клиента по событиям v2 и импорт истории — против работающего стенда (Compose).
test.describe.configure({ mode: 'serial' });

// Признаки поведения с весом 0 — часть правил: проверяются в режиме правил, после — снова ML (по умолчанию)
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

const run = `e2e-imp-${Date.now()}`;

function history(): string {
  const now = Date.now();
  const lines: string[] = [];
  // Клиент 1: входы и нагрузка за 3 дня, неудачное списание, нерешённое обращение (accountSeq — поздний номер: пропуска не создаёт)
  for (let d = 3; d >= 1; d--) {
    const at = new Date(now - d * 86_400_000 + 3_600_000).toISOString();
    const date = new Date(now - d * 86_400_000).toISOString().slice(0, 10);
    lines.push(JSON.stringify({ schemaVersion: 2, eventId: `${run}-l${d}`, type: 'account.login', occurredAt: at, accountId: 1, accountSeq: 1, data: { channel: 'panel', success: true } }));
    lines.push(JSON.stringify({ schemaVersion: 2, eventId: `${run}-u${d}`, type: 'usage.daily', occurredAt: at, accountId: 1, accountSeq: 1, serviceId: 1, data: { serviceId: 1, date, cpuAvgPct: 30 - d * 5, uptimePct: 100 } }));
  }
  const recent = new Date(now - 600_000).toISOString();
  lines.push(JSON.stringify({ schemaVersion: 2, eventId: `${run}-pf`, type: 'payment.failed', occurredAt: recent, accountId: 1, accountSeq: 1, serviceId: 1,
    data: { serviceId: 1, amount: 960, reason: 'insufficient_funds' },
    state: { transaction: { id: 990000 + (now % 9000), serviceId: 1, amount: 960, status: 'not_performed', createdAt: recent, type: 'prolong' } } }));
  lines.push(JSON.stringify({ schemaVersion: 2, eventId: `${run}-tc`, type: 'ticket.closed', occurredAt: recent, accountId: 1, accountSeq: 1, data: { ticketId: 1, resolution: 'unsolved', csat: 1, hours: 30 } }));
  lines.push('{broken');
  return lines.join('\n') + '\n';
}

test('импорт истории из файла: прогресс, исходы, отклонённая строка; коннектор batch-import', async ({ page }) => {
  const loads = countReloads(page);
  await page.goto('/source');
  await expect(page.getByTestId('connector-batch-import')).toBeVisible();
  await page.getByTestId('import-file').setInputFiles({ name: 'history.jsonl', mimeType: 'application/x-ndjson', buffer: Buffer.from(history()) });
  await page.getByTestId('import-start').click();
  const progress = page.getByTestId('import-panel').getByTestId('import-progress');
  await expect(progress).toHaveAttribute('data-status', 'succeeded', { timeout: 30_000 });
  await expect(progress).toContainText('отклонено 1');
  await expect(progress).toContainText('invalid_json');
  await expect(page.getByTestId('connector-batch-import')).toContainText('выполнен', { timeout: 20_000 });
  expect(loads.loads).toBe(1);
});

test('карточка клиента: поведение — спарклайны, итоги 30 дней, лента с фильтром по доменам', async ({ page }) => {
  await page.goto('/customers/1');
  const card = page.getByTestId('behavior-card');
  await expect(card).toBeVisible();
  await expect(card).not.toHaveAttribute('data-empty', 'true');
  await expect(page.getByTestId('spark-cpu')).toBeVisible();
  await expect(page.getByTestId('spark-logins')).toBeVisible();
  await expect(page.getByTestId('tile-failed').locator('.num')).not.toHaveText('0');
  await expect(page.getByTestId('behavior-feed')).toContainText('Списание не прошло');
  await page.getByTestId('feed-filter-support').click();
  const items = page.getByTestId('feed-item');
  await expect(items.first()).toHaveAttribute('data-domain', 'support');
  await expect(page.getByTestId('behavior-feed')).toContainText('не решено');
  // Признаки поведения видны в признаках услуги с весом 0
  await page.getByRole('button', { name: 'Признаки' }).first().click();
  await expect(page.getByText('Неудачные списания').first()).toBeVisible();
});

test('модель: раздел весов поведения, по умолчанию 0', async ({ page }) => {
  await page.goto('/model');
  await expect(page.getByTestId('behavior-weights')).toBeVisible();
  await expect(page.getByTestId('weight-usage_drop')).toHaveValue('0');
  const stream = await (await page.request.get(apiUrl('/api/v1/source/stream'))).json();
  expect(stream.connectors.map((c: { key: string }) => c.key)).toContain('batch-import');
});
