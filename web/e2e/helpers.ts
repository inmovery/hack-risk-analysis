import { expect, type Page } from '@playwright/test';

/** Адрес API: пусто — тот же origin, что и панель (Compose); иначе отдельный API-хост (раздельный деплой). */
export const apiBase = (process.env.E2E_API_URL ?? '').replace(/\/+$/, '');
export const apiUrl = (path: string) => apiBase + path;

export const admin = { login: process.env.BOOTSTRAP_ADMIN_LOGIN ?? 'admin', password: process.env.BOOTSTRAP_ADMIN_PASSWORD ?? '' };
export const analyst = { login: process.env.BOOTSTRAP_ANALYST_LOGIN ?? '', password: process.env.BOOTSTRAP_ANALYST_PASSWORD ?? '' };

export async function login(page: Page, who = admin) {
  await page.goto('/login');
  await page.getByLabel('Логин').fill(who.login);
  await page.getByLabel('Пароль').fill(who.password);
  await page.getByRole('button', { name: 'Войти' }).click();
  await expect(page.getByRole('heading', { name: 'Обзор' })).toBeVisible();
}

/** Счётчик полных загрузок документа: live-обновление не должно перезагружать страницу. */
export function countReloads(page: Page) {
  const counter = { loads: 0 };
  page.on('load', () => counter.loads++);
  return counter;
}

export async function generationOf(page: Page): Promise<number> {
  const label = page.getByTestId('generation-label');
  await expect(label).toBeVisible();
  return Number(await label.getAttribute('data-generation'));
}
