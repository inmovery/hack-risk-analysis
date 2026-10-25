import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router';
import type { SimulationView } from '../api/types';

const view = (patch: Partial<SimulationView> = {}): SimulationView => ({
  eventsOnly: true,
  settings: { running: true, tickSeconds: 5, scenarioStepSeconds: 15, chaos: true, world: { source: 'organizer', seed: 42, extraCustomers: 50, customers: 300, historyDays: 90 } },
  revision: 3, updatedAt: '2026-10-25T10:00:00Z', updatedBy: 'admin',
  world: { revision: 2, startedAt: '2026-10-25T09:00:00Z', settings: { source: 'organizer', seed: 42, extraCustomers: 50, customers: 300, historyDays: 90 }, resetAt: null, resetBy: null },
  scenarioRequest: 0,
  agent: {
    online: true, reportedAt: '2026-10-25T10:00:05Z',
    status: { agentVersion: '1', worldRevision: 2, runId: 'ab12cd34', phase: 'history', simClock: '2026-10-20T00:00:00Z', customers: 60, services: 140, liveServices: 120,
      eventsGenerated: 1000, eventsSent: 750, backlog: 250, lastSentAt: '2026-10-25T10:00:04Z', lastError: null, lastNarration: null, scenarioHandled: 0 },
  },
  pending: { worldChanged: false },
  ...patch,
});

const state = vi.hoisted(() => ({ data: undefined as unknown, role: 'admin', composed: [] as unknown[] }));
const api = vi.hoisted(() => vi.fn());

vi.mock('../api/client', () => ({ api, ApiError: class extends Error { status = 0; get fieldErrors() { return {}; } } }));
const composerTypes = [
  { type: 'balance.topped_up', title: 'Пополнение баланса', description: 'Баланс растёт.', needsService: false,
    fields: [{ name: 'amount', label: 'Сумма, €', kind: 'money', options: null, min: 1, max: 10000, default: 10 }] },
  { type: 'service.suspended', title: 'Выключить сервер', description: 'suspended', needsService: true, fields: [] },
];
vi.mock('../api/queries', () => ({
  useSimulation: () => ({ data: state.data, isLoading: false, error: null }),
  useSummary: () => ({ data: undefined }),
  useComposerTypes: () => ({ data: composerTypes }),
  useComposedEvents: () => ({ data: state.composed }),
  useCustomers: () => ({ data: { items: [{ id: 7, email: 'c7@example.test' }, { id: 9, email: 'c9@example.test' }] }, isFetching: false }),
  useCustomerServices: (id: number | null) => ({ data: id === null ? undefined : { detail: { services: [{ id: 70, name: 'VPS', status: 'active' }, { id: 71, name: 'Old', status: 'deleted' }] } }, isLoading: false }),
}));
vi.mock('../auth/AuthContext', () => ({ useAuth: () => ({ user: { login: state.role, role: state.role, sessionExpiresAt: null } }) }));
vi.mock('../layout/AppShell', () => ({ Topbar: () => null }));

import { SimulationPage } from './SimulationPage';

function renderPage() {
  render(<QueryClientProvider client={new QueryClient()}><MemoryRouter><SimulationPage /></MemoryRouter></QueryClientProvider>);
}

beforeAll(() => {
  HTMLDialogElement.prototype.showModal ??= function (this: HTMLDialogElement) { this.open = true; };
  HTMLDialogElement.prototype.close ??= function (this: HTMLDialogElement) { this.open = false; };
});
beforeEach(() => { state.data = view(); state.role = 'admin'; state.composed = []; api.mockReset(); api.mockImplementation(async () => view()); });
afterEach(() => cleanup());

describe('SimulationPage', () => {
  it('показывает статус симулятора, фазу и прогресс отправки', () => {
    renderPage();
    expect(screen.getByTestId('sim-status').dataset.online).toBe('true');
    expect(screen.getByText('Симулятор на связи')).toBeTruthy();
    expect(screen.getByTestId('sim-phase').textContent).toBe('отправляет историю');
    expect(screen.getByRole('progressbar').getAttribute('aria-valuenow')).toBe('75');
  });

  it('симуляция постоянная: паузы нет, сохранение меняет только такт и параметры', async () => {
    renderPage();
    expect(screen.queryByTestId('sim-running')).toBeNull();
    expect(screen.getByText('● работает постоянно')).toBeTruthy();
    fireEvent.change(screen.getByTestId('sim-tick'), { target: { value: '7' } });
    fireEvent.click(screen.getByTestId('sim-save'));
    await waitFor(() => expect(api).toHaveBeenCalled());
    const [path, opts] = api.mock.calls[0] as [string, { method: string; body: { running: boolean; tickSeconds: number } }];
    expect(path).toBe('/api/v1/simulation/settings');
    expect(opts.method).toBe('PUT');
    expect(opts.body.tickSeconds).toBe(7);
  });

  it('конструктор: тип → клиент → услуга, отправка в поток со значениями полей', async () => {
    api.mockImplementation(async () => ({ id: 5, createdAt: '2026-10-25T10:00:00Z', createdBy: 'admin', type: 'service.suspended', accountId: 7, serviceId: 70, data: {}, status: 'pending', eventIds: [] }));
    renderPage();
    const submit = screen.getByTestId('composer-submit') as HTMLButtonElement;
    expect(submit.disabled).toBe(true);
    fireEvent.click(screen.getByTestId('composer-type'));
    fireEvent.click(screen.getByTestId('composer-type-option-service.suspended'));
    fireEvent.click(screen.getByTestId('composer-account'));
    fireEvent.click(screen.getByTestId('composer-account-option-7'));
    expect(submit.disabled).toBe(true);
    fireEvent.click(screen.getByTestId('composer-service'));
    fireEvent.click(screen.getByTestId('composer-service-option-71'));
    expect(screen.getByTestId('composer-service').dataset.value).toBe('');
    fireEvent.click(screen.getByTestId('composer-service-option-70'));
    expect(submit.disabled).toBe(false);
    fireEvent.click(submit);
    await waitFor(() => expect(api).toHaveBeenCalledWith('/api/v1/simulation/events',
      expect.objectContaining({ method: 'POST', body: { type: 'service.suspended', accountId: 7, serviceId: 70, data: {} } })));
    expect(await screen.findByTestId('composer-sent')).toBeTruthy();
  });

  it('конструктор: сумма пополнения уходит числом в евро', async () => {
    renderPage();
    fireEvent.click(screen.getByTestId('composer-type'));
    fireEvent.click(screen.getByTestId('composer-type-option-balance.topped_up'));
    fireEvent.click(screen.getByTestId('composer-account'));
    fireEvent.click(screen.getByTestId('composer-account-option-9'));
    fireEvent.change(screen.getByTestId('composer-amount'), { target: { value: '25.5' } });
    fireEvent.click(screen.getByTestId('composer-submit'));
    await waitFor(() => expect(api).toHaveBeenCalledWith('/api/v1/simulation/events',
      expect.objectContaining({ body: { type: 'balance.topped_up', accountId: 9, data: { amount: 25.5 } } })));
  });

  it('итоги собранных событий: в очереди, в потоке, не применено с причиной', () => {
    state.composed = [
      { id: 3, createdAt: '2026-10-25T10:00:00Z', createdBy: 'admin', type: 'service.suspended', accountId: 7, serviceId: 70, data: {}, status: 'rejected', eventIds: [], message: 'Клиент уже ушёл' },
      { id: 2, createdAt: '2026-10-25T09:59:00Z', createdBy: 'admin', type: 'balance.topped_up', accountId: 9, data: { amount: 10 }, status: 'applied', eventIds: ['sim-1'] },
      { id: 1, createdAt: '2026-10-25T09:58:00Z', createdBy: 'admin', type: 'balance.topped_up', accountId: 9, data: { amount: 10 }, status: 'pending', eventIds: [] },
    ];
    renderPage();
    expect(screen.getByTestId('composed-3').dataset.status).toBe('rejected');
    expect(screen.getByText('Клиент уже ушёл')).toBeTruthy();
    expect(screen.getByTestId('composed-2').textContent).toContain('в потоке');
    expect(screen.getByTestId('composed-1').textContent).toContain('в очереди симулятора');
  });

  it('пересоздание snapshot системы — только после подтверждения', async () => {
    renderPage();
    fireEvent.click(screen.getByTestId('sim-reset'));
    expect(api).not.toHaveBeenCalled();
    fireEvent.click(screen.getByTestId('sim-reset-confirm'));
    await waitFor(() => expect(api).toHaveBeenCalledWith('/api/v1/simulation/reset', expect.objectContaining({ method: 'POST' })));
  });

  it('аналитик видит состояние, но не меняет настройки', () => {
    state.role = 'analyst';
    renderPage();
    expect(screen.getByText(/Менять настройки симуляции может администратор/)).toBeTruthy();
    expect(screen.queryByTestId('sim-save')).toBeNull();
    expect(screen.queryByTestId('composer-submit')).toBeNull();
    expect((screen.getByTestId('sim-scenario') as HTMLButtonElement).disabled).toBe(true);
  });

  it('без режима событий — предупреждение, что симуляция выключена', () => {
    state.data = view({ eventsOnly: false });
    renderPage();
    expect(screen.getByText(/симуляция выключена/)).toBeTruthy();
  });
});
