import { useEffect, useMemo, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { api, ApiError } from '../api/client';
import { useComposedEvents, useComposerTypes, useCustomers, useCustomerServices } from '../api/queries';
import type { ComposedEvent, ComposedStatus, ComposerEventType, ComposerField } from '../api/types';
import { localTime } from '../format';
import { Button, Checkbox, Select } from './controls';
import { Banner, Tag } from './ui';

const statusText: Record<ComposedStatus, [string, 'info' | 'success' | 'danger']> = {
  pending: ['в очереди симулятора', 'info'],
  applied: ['в потоке', 'success'],
  rejected: ['не применено', 'danger'],
};

const serviceStatus: Record<string, string> = {
  active: 'активна', suspended: 'выключена', prolong_wait: 'ждёт оплаты', blocked: 'заблокирована', deleted: 'удалена',
};

type Values = Record<string, string | boolean>;

function defaults(t: ComposerEventType | undefined): Values {
  const v: Values = {};
  for (const f of t?.fields ?? []) v[f.name] = f.kind === 'bool' ? f.default === true : f.default == null ? '' : String(f.default);
  return v;
}

/** Значения формы → data события (суммы — в евро, как ждёт API) */
function toData(t: ComposerEventType, values: Values) {
  const data: Record<string, unknown> = {};
  for (const f of t.fields) {
    const v = values[f.name];
    data[f.name] = f.kind === 'bool' ? v === true : f.kind === 'enum' ? v : v === '' ? null : Number(String(v).replace(',', '.'));
  }
  return data;
}

function useDebounced<T>(value: T, ms: number) {
  const [v, setV] = useState(value);
  useEffect(() => {
    const t = window.setTimeout(() => setV(value), ms);
    return () => window.clearTimeout(t);
  }, [value, ms]);
  return v;
}

/**
 * Конструктор события: тип → клиент → услуга (если нужна) → поля → «Отправить в поток».
 * Событие встаёт в очередь симулятора; он применяет его к snapshot системы и отправляет в общий поток — статус обновляется в списке ниже.
 */
export function EventComposer({ isAdmin, eventsOnly, online }: { isAdmin: boolean; eventsOnly: boolean; online: boolean }) {
  const qc = useQueryClient();
  const types = useComposerTypes();
  const recent = useComposedEvents();
  const [typeKey, setTypeKey] = useState<string | null>(null);
  const [search, setSearch] = useState('');
  const [accountId, setAccountId] = useState<number | null>(null);
  const [serviceId, setServiceId] = useState<number | null>(null);
  const [values, setValues] = useState<Values>({});
  const [errors, setErrors] = useState<Record<string, string[]>>({});
  const [busy, setBusy] = useState(false);
  const [sent, setSent] = useState<ComposedEvent | null>(null);

  const type = types.data?.find((t) => t.type === typeKey);
  const q = useDebounced(search, 300);
  const customers = useCustomers({
    sortBy: 'riskScore', sortDirection: 'desc', levels: [], state: '', flag: '', search: q, page: 1, pageSize: 20,
    fullChurnLevels: [], fullChurnState: '', paymentTrends: [],
  });
  const detail = useCustomerServices(type?.needsService ? accountId : null);

  useEffect(() => setValues(defaults(type)), [type]);
  useEffect(() => setServiceId(null), [accountId]);

  const accountOptions = useMemo(() => {
    const items = customers.data?.items ?? [];
    const list = items.map((c) => ({ value: String(c.id), label: `#${c.id} · ${c.email}` }));
    if (accountId !== null && !list.some((o) => o.value === String(accountId))) list.unshift({ value: String(accountId), label: `#${accountId}` });
    return list;
  }, [customers.data, accountId]);

  const serviceOptions = useMemo(() => (detail.data?.detail.services ?? []).map((s) => ({
    value: String(s.id), label: `#${s.id} · ${s.name}`,
    description: s.status ? serviceStatus[s.status] ?? s.status : undefined,
    disabled: s.status === 'deleted',
  })), [detail.data]);

  const err = (k: string) => errors[k]?.join(' ');
  const ready = !!type && accountId !== null && (!type.needsService || serviceId !== null);
  const payload = type && accountId !== null
    ? { type: type.type, accountId, ...(type.needsService ? { serviceId } : {}), data: toData(type, values) }
    : null;

  async function submit() {
    if (!payload) return;
    setBusy(true); setErrors({}); setSent(null);
    try {
      const r = await api<ComposedEvent>('/api/v1/simulation/events', { method: 'POST', body: payload });
      setSent(r);
      await qc.invalidateQueries({ queryKey: ['source', 'composed'] });
    } catch (e) {
      if (e instanceof ApiError && e.status === 422) setErrors(e.fieldErrors);
      else setErrors({ _: [e instanceof ApiError && e.code === 'events_only_disabled' ? 'Симуляция выключена: событие некому применить.' : (e as Error).message] });
    } finally { setBusy(false); }
  }

  const title = (t: string) => types.data?.find((x) => x.type === t)?.title ?? t;

  return (
    <section className="card stack" aria-labelledby="composer-title" data-testid="composer">
      <div className="card-head" style={{ marginBottom: 0 }}>
        <h2 id="composer-title">Собрать событие</h2>
        <span className="caption muted">уходит в общий поток через симулятор</span>
      </div>
      {!online && eventsOnly && <Banner tone="warning">Симулятор не на связи: событие подождёт в очереди и применится, когда он вернётся.</Banner>}

      <div className="composer-grid">
        <label className="field">
          <span className="field-label" id="composer-type-label">Тип события</span>
          <Select labelledBy="composer-type-label" value={typeKey} onChange={(v) => { setTypeKey(v); setErrors({}); setSent(null); }} disabled={!isAdmin || !types.data}
            placeholder="Выберите тип" invalid={!!err('type')} testId="composer-type"
            options={(types.data ?? []).map((t) => ({ value: t.type, label: t.title, description: t.description }))} />
          {err('type') ? <span className="field-error">{err('type')}</span> : <span className="field-hint">{type ? type.description : 'Что произойдёт в snapshot системы'}</span>}
        </label>

        <div className="field" style={{ gridColumn: '1 / -1' }}>
          <span className="field-label" id="composer-account-label">Клиент</span>
          <div className="row" style={{ gap: 8 }}>
            <input className="input" type="search" placeholder="Поиск по ID или email" aria-label="Поиск клиента" value={search} disabled={!isAdmin}
              onChange={(e) => setSearch(e.target.value)} style={{ width: 240, flex: 'none' }} data-testid="composer-account-search" />
            <Select labelledBy="composer-account-label" value={accountId === null ? null : String(accountId)} onChange={(v) => setAccountId(Number(v))}
              disabled={!isAdmin} placeholder={customers.isFetching ? 'Поиск…' : 'Выберите клиента'} invalid={!!err('accountId')} testId="composer-account" width="100%"
              options={accountOptions} />
          </div>
          {err('accountId') ? <span className="field-error">{err('accountId')}</span> : <span className="field-hint">сначала клиенты с наибольшим риском</span>}
        </div>

        {type?.needsService && (
          <label className="field">
            <span className="field-label" id="composer-service-label">Услуга</span>
            <Select labelledBy="composer-service-label" value={serviceId === null ? null : String(serviceId)} onChange={(v) => setServiceId(Number(v))}
              disabled={!isAdmin || accountId === null} placeholder={accountId === null ? 'Сначала клиент' : detail.isLoading ? 'Загрузка…' : 'Выберите услугу'}
              invalid={!!err('serviceId')} testId="composer-service" options={serviceOptions} />
            {err('serviceId') ? <span className="field-error">{err('serviceId')}</span> : <span className="field-hint">удалённые услуги недоступны</span>}
          </label>
        )}

        {type?.fields.map((f) => (
          <FieldInput key={f.name} f={f} value={values[f.name] ?? ''} disabled={!isAdmin} error={err(`data.${f.name}`)}
            onChange={(v) => setValues((s) => ({ ...s, [f.name]: v }))} />
        ))}
      </div>

      {payload && (
        <details className="small">
          <summary>Что уйдёт в поток</summary>
          <pre className="code" data-testid="composer-preview">{JSON.stringify(payload, null, 2)}</pre>
          <span className="caption muted">Симулятор добавит eventId, время snapshot системы и следующий номер события аккаунта.</span>
        </details>
      )}

      {errors._ && <Banner tone="danger">{errors._.join(' ')}</Banner>}
      {sent && <Banner tone="info" testId="composer-sent">Событие №{sent.id} «{title(sent.type)}» в очереди симулятора — итог появится в списке ниже.</Banner>}

      {isAdmin && (
        <div className="toolbar">
          <Button variant="primary" disabled={!ready || !eventsOnly} loading={busy} onClick={() => void submit()} testId="composer-submit">Отправить в поток</Button>
          <Button variant="ghost" disabled={busy || (!typeKey && accountId === null)} onClick={() => { setTypeKey(null); setAccountId(null); setSearch(''); setErrors({}); setSent(null); }}>
            Очистить
          </Button>
        </div>
      )}

      <h3 className="small">Последние собранные события</h3>
      {recent.data && recent.data.length === 0 && <p className="small secondary" data-testid="composer-empty">Пока ничего не отправляли.</p>}
      {recent.data && recent.data.length > 0 && (
        <div className="table-wrap">
          <table className="data" data-testid="composer-list">
            <thead><tr><th>№</th><th>Время</th><th>Событие</th><th>Клиент</th><th>Итог</th></tr></thead>
            <tbody>
              {recent.data.slice(0, 10).map((e) => {
                const [label, tone] = statusText[e.status];
                return (
                  <tr key={e.id} data-testid={`composed-${e.id}`} data-status={e.status}>
                    <td className="num">{e.id}</td>
                    <td className="num">{localTime(e.createdAt)}</td>
                    <td>{title(e.type)}{e.serviceId ? <span className="secondary"> · услуга #{e.serviceId}</span> : null}</td>
                    <td className="num">#{e.accountId}</td>
                    <td>
                      <Tag tone={tone}>{label}</Tag>
                      {e.message && <div className="caption secondary">{e.message}</div>}
                      {e.status === 'applied' && e.eventIds.length > 0 && <div className="caption muted ellipsis" title={e.eventIds.join(', ')}>{e.eventIds[0]}</div>}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}

function FieldInput({ f, value, disabled, error, onChange }: { f: ComposerField; value: string | boolean; disabled: boolean; error?: string | undefined; onChange: (v: string | boolean) => void }) {
  const id = `composer-${f.name}`;
  if (f.kind === 'bool')
    return (
      <div className="field" style={{ justifyContent: 'center' }}>
        <Checkbox checked={value === true} onChange={onChange} label={f.label} disabled={disabled} invalid={!!error} testId={id} />
        {error && <span className="field-error">{error}</span>}
      </div>
    );
  if (f.kind === 'enum')
    return (
      <label className="field">
        <span className="field-label" id={`${id}-label`}>{f.label}</span>
        <Select labelledBy={`${id}-label`} value={String(value)} onChange={onChange} disabled={disabled} invalid={!!error} testId={id}
          options={(f.options ?? []).map((o) => ({ value: o.value, label: o.label }))} />
        {error && <span className="field-error">{error}</span>}
      </label>
    );
  const range = f.min != null && f.max != null ? `${f.min}–${f.max.toLocaleString('ru-RU')}` : '';
  return (
    <label className="field">
      <span className="field-label">{f.label}</span>
      <input className="input num" type="number" inputMode="decimal" min={f.min ?? undefined} max={f.max ?? undefined} step={f.kind === 'money' ? 0.01 : 1}
        value={String(value)} disabled={disabled} aria-invalid={!!error} data-testid={id} onChange={(e) => onChange(e.target.value)} />
      {error ? <span className="field-error">{error}</span> : <span className="field-hint">{f.kind === 'money' ? `в евро${range ? `, ${range}` : ''}` : range}</span>}
    </label>
  );
}
