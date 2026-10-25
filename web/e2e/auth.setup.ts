import { test as setup } from '@playwright/test';
import { login } from './helpers';

// Одна сессия администратора на прогон: вход ограничен 10 попытками в минуту с IP, тесты не должны его исчерпывать.
setup('сессия администратора', async ({ page }) => {
  await login(page);
  await page.context().storageState({ path: 'e2e/.auth/admin.json' });
});
