import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { api, apiUrl } from './client';
import type {
  ComposedEvent, ComposerEventType, CustomerActivity, CustomerDetailResponse, CustomerEventsResponse, CustomerListResponse, HistoryResponse, ImportView, JournalPage, MlStatus,
  ModelSettingsResponse, Revision, SimulationView, SnapshotInfo, StreamStatus, SummaryResponse,
} from './types';
import { useLive } from '../live/LiveContext';

// Все запросы, зависящие от поколения, лежат под ключом ['gen', …]: событие generationPublished инвалидирует их разом.
// Журнал и состояние приёма — под ['source', …]: их инвалидирует sourceChanged (раньше пересчёта).

export function useSummary() {
  const { pollInterval } = useLive();
  return useQuery({
    queryKey: ['gen', 'summary'],
    queryFn: ({ signal }) => api<SummaryResponse>('/api/v1/analytics/summary', { signal }),
    refetchInterval: pollInterval,
    retry: (n, e) => n < 2 && !(e as { status?: number }).status,
  });
}

export interface CustomerQuery {
  sortBy: string;
  sortDirection: 'asc' | 'desc';
  levels: string[];
  state: string;
  flag: string;
  search: string;
  page: number;
  pageSize: number;
  /** Индекс «уход целиком»: уровни */
  fullChurnLevels: string[];
  /** Индекс «уход целиком»: состояние (churned — уже ушли) */
  fullChurnState: string;
  /** Тренд платежей */
  paymentTrends: string[];
}

function filterParams(q: CustomerQuery): URLSearchParams {
  const p = new URLSearchParams({ sortBy: q.sortBy, sortDirection: q.sortDirection });
  q.levels.forEach((l) => p.append('riskLevel', l));
  if (q.state) p.set('scoreState', q.state);
  if (q.flag) p.set('flag', q.flag);
  if (q.search.trim()) p.set('search', q.search.trim());
  q.fullChurnLevels.forEach((l) => p.append('fullChurnLevel', l));
  if (q.fullChurnState) p.set('fullChurnState', q.fullChurnState);
  q.paymentTrends.forEach((t) => p.append('paymentTrend', t));
  return p;
}

export function customersUrl(q: CustomerQuery): string {
  const p = filterParams(q);
  p.set('page', String(q.page));
  p.set('pageSize', String(q.pageSize));
  return `/api/v1/customers?${p.toString()}`;
}

/** Ручная выгрузка CSV всех строк под текущими фильтрами (передаётся в сервис возврата пользователей) */
export function exportUrl(q: CustomerQuery): string {
  return apiUrl(`/api/v1/export/customers?${filterParams(q).toString()}`);
}

export function useCustomers(q: CustomerQuery) {
  const { pollInterval } = useLive();
  return useQuery({
    queryKey: ['gen', 'customers', q],
    queryFn: ({ signal }) => api<CustomerListResponse>(customersUrl(q), { signal }),
    placeholderData: keepPreviousData,
    refetchInterval: pollInterval,
  });
}

export function useCustomer(id: number) {
  const { pollInterval } = useLive();
  return useQuery({
    queryKey: ['gen', 'customer', id],
    queryFn: ({ signal }) => api<CustomerDetailResponse>(`/api/v1/customers/${id}`, { signal }),
    placeholderData: keepPreviousData,
    refetchInterval: pollInterval,
    retry: false,
  });
}

export function useCustomerEvents(id: number) {
  const { pollInterval } = useLive();
  return useQuery({
    queryKey: ['source', 'customer-events', id],
    queryFn: ({ signal }) => api<CustomerEventsResponse>(`/api/v1/customers/${id}/events?limit=30`, { signal }),
    refetchInterval: pollInterval,
  });
}

/** Поведение клиента: дневная активность и лента доменных событий */
export function useCustomerActivity(id: number, domain: string, beforeSeq: number | null) {
  const p = new URLSearchParams({ days: '90', limit: '30' });
  if (domain) p.set('domain', domain);
  if (beforeSeq !== null) p.set('beforeSeq', String(beforeSeq));
  return useQuery({
    queryKey: ['source', 'activity', id, domain, beforeSeq],
    queryFn: ({ signal }) => api<CustomerActivity>(`/api/v1/customers/${id}/activity?${p}`, { signal }),
    enabled: Number.isFinite(id),
  });
}

/** Импорт истории: опрос, пока выполняется */
export function useImport(id: number | null) {
  return useQuery({
    queryKey: ['source', 'import', id],
    queryFn: ({ signal }) => api<ImportView>(`/api/v1/source/imports/${id}`, { signal }),
    enabled: id !== null,
    refetchInterval: (q) => (q.state.data && ['succeeded', 'failed'].includes(q.state.data.status) ? false : 1500),
  });
}

export function useSettings() {
  const { pollInterval } = useLive();
  return useQuery({ queryKey: ['settings'], queryFn: ({ signal }) => api<ModelSettingsResponse>('/api/v1/model/settings', { signal }), refetchInterval: pollInterval });
}

export function useRevisions() {
  return useQuery({ queryKey: ['settings', 'revisions'], queryFn: ({ signal }) => api<Revision[]>('/api/v1/model/revisions', { signal }) });
}

export function useMlStatus() {
  return useQuery({ queryKey: ['source', 'ml'], queryFn: ({ signal }) => api<MlStatus>('/api/v1/ml/status', { signal }) });
}

/** Симуляция: настройки, snapshot системы и отчёт симулятора — опрос раз в 3 с (статус меняется без событий поколения) */
export function useSimulation() {
  return useQuery({
    queryKey: ['source', 'simulation'],
    queryFn: ({ signal }) => api<SimulationView>('/api/v1/simulation', { signal }),
    refetchInterval: 3000,
  });
}

export function useSnapshots() {
  const { pollInterval } = useLive();
  return useQuery({ queryKey: ['source', 'snapshots'], queryFn: ({ signal }) => api<SnapshotInfo[]>('/api/v1/source/snapshots', { signal }), refetchInterval: pollInterval });
}

export function useStream() {
  const { pollInterval } = useLive();
  // Даже при live: раз в 30 с — чтобы «поток молчит» и свежесть пересчитывались без событий
  return useQuery({
    queryKey: ['source', 'stream'],
    queryFn: ({ signal }) => api<StreamStatus>('/api/v1/source/stream', { signal }),
    refetchInterval: Math.min(pollInterval, 30000),
  });
}

export function useJournal(filter: { outcome: string; kind: string }, beforeSeq: number | null) {
  const { pollInterval } = useLive();
  const p = new URLSearchParams({ limit: '50' });
  if (filter.outcome) p.set('outcome', filter.outcome);
  if (filter.kind) p.set('kind', filter.kind);
  if (beforeSeq !== null) p.set('beforeSeq', String(beforeSeq));
  return useQuery({
    queryKey: ['source', 'journal', filter, beforeSeq],
    queryFn: ({ signal }) => api<JournalPage>(`/api/v1/source/events?${p.toString()}`, { signal }),
    placeholderData: keepPreviousData,
    refetchInterval: beforeSeq === null ? pollInterval : false,
  });
}

export function useHistory(days = 30) {
  const { pollInterval } = useLive();
  return useQuery({
    queryKey: ['source', 'history', days],
    queryFn: ({ signal }) => api<HistoryResponse>(`/api/v1/analytics/history?days=${days}`, { signal }),
    refetchInterval: pollInterval,
  });
}

/** Типы событий конструктора — справочник, не меняется без выкладки */
export function useComposerTypes() {
  return useQuery({ queryKey: ['composer-types'], queryFn: ({ signal }) => api<ComposerEventType[]>('/api/v1/simulation/events/types', { signal }), staleTime: Infinity });
}

/** События из конструктора: пока есть ожидающие — опрос чаще */
export function useComposedEvents() {
  return useQuery({
    queryKey: ['source', 'composed'],
    queryFn: ({ signal }) => api<ComposedEvent[]>('/api/v1/simulation/events', { signal }),
    refetchInterval: (q) => (q.state.data?.some((e) => e.status === 'pending') ? 1500 : 5000),
  });
}

/** Услуги клиента для конструктора (только когда клиент выбран) */
export function useCustomerServices(id: number | null) {
  return useQuery({
    queryKey: ['gen', 'customer', id],
    queryFn: ({ signal }) => api<CustomerDetailResponse>(`/api/v1/customers/${id}`, { signal }),
    enabled: id !== null,
    retry: false,
  });
}
