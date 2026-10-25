import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { SnapshotInfo } from '../api/types';

const state = vi.hoisted(() => ({ snapshots: { isSuccess: true, isLoading: false, error: null, data: [] as unknown[] } }));
const api = vi.hoisted(() => vi.fn());

vi.mock('../api/client', () => ({ api, ApiError: class extends Error {} }));
vi.mock('../api/queries', () => ({ useSnapshots: () => state.snapshots, useSummary: () => ({ data: undefined }) }));
vi.mock('../auth/AuthContext', () => ({ useAuth: () => ({ user: { login: 'admin', role: 'admin', sessionExpiresAt: null } }) }));
vi.mock('../layout/AppShell', () => ({ Topbar: () => null }));
vi.mock('../components/StreamSection', () => ({ StreamSection: () => null }));

import { SourcePage } from './SourcePage';

const snapshot = (revision: number): SnapshotInfo => ({
  revision, sourceAsOf: '2026-10-19T15:00:00Z', sourceAsOfAssumed: false, receivedAt: '2026-10-20T10:00:00Z', contentHash: 'a'.repeat(64), rawSha256: 'b'.repeat(64),
  adapter: 'json', schemaVersion: 1, sourceRevisionLabel: null, synthetic: false, provenance: '{"label":"organizer:test"}',
  counts: { accounts: 1, services: 1, transactions: 1 }, warnings: {}, rawBytes: 10, actor: 'admin', active: true,
});

function renderPage() {
  render(<QueryClientProvider client={new QueryClient()}><SourcePage /></QueryClientProvider>);
  fireEvent.change(screen.getByTestId('snapshot-file'), { target: { files: [new File(['{"accounts":[]}'], 'data.json', { type: 'application/json' })] } });
  return screen.getByTestId('replace-snapshot') as HTMLButtonElement;
}

async function confirmUpload(button: HTMLButtonElement) {
  fireEvent.click(button);
  fireEvent.click(screen.getByTestId('confirm-replace'));
  await waitFor(() => expect(api).toHaveBeenCalled());
  const [path, opts] = api.mock.calls[0] as [string, { method: string; headers: Record<string, string> }];
  return { path, opts };
}

beforeAll(() => {
  // jsdom не реализует модальный <dialog>
  HTMLDialogElement.prototype.showModal ??= function (this: HTMLDialogElement) { this.open = true; };
  HTMLDialogElement.prototype.close ??= function (this: HTMLDialogElement) { this.open = false; };
});
beforeEach(() => { api.mockReset(); api.mockResolvedValue({ sourceRevision: 1, jobId: 1, idempotent: false }); });
afterEach(cleanup);

describe('SourcePage: загрузка тестовых данных', () => {
  it('в пустую базу: кнопка активна, первая загрузка уходит без If-Match', async () => {
    state.snapshots = { isSuccess: true, isLoading: false, error: null, data: [] };
    const button = renderPage();
    expect(button.disabled).toBe(false);
    expect(button.textContent).toContain('Загрузить тестовые данные');
    const { path, opts } = await confirmUpload(button);
    expect(path).toBe('/api/v1/source/snapshot');
    expect(opts.method).toBe('PUT');
    expect(opts.headers['If-Match']).toBeUndefined();
  });

  it('поверх активной ревизии: замена с If-Match этой ревизии', async () => {
    state.snapshots = { isSuccess: true, isLoading: false, error: null, data: [snapshot(3)] };
    const button = renderPage();
    expect(button.disabled).toBe(false);
    expect(button.textContent).toContain('Заменить тестовые данные');
    const { opts } = await confirmUpload(button);
    expect(opts.headers['If-Match']).toBe('"3"');
  });

  it('пока список ревизий не получен — загрузка недоступна (иначе не с чем сверить If-Match)', () => {
    state.snapshots = { isSuccess: false, isLoading: true, error: null, data: undefined as unknown as unknown[] };
    expect(renderPage().disabled).toBe(true);
  });
});
