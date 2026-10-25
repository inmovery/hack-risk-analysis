import { useEffect, useMemo, useRef, useState } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router';
import { exportUrl, useCustomers, useSummary, type CustomerQuery } from '../api/queries';
import { Topbar } from '../layout/AppShell';
import { ErrorBox, FullChurnChip, RiskBadge, Skeleton, Tag } from '../components/ui';
import { eur, flagLabel, levelGlyph, levelLabel, score, stateLabel } from '../format';
import { paymentShort, paymentTrendLabel } from '../journal';
import type { CustomerListItem, PaymentTrendState, RiskLevel } from '../api/types';

const SORTS = ['id', 'email', 'balance', 'totalReplenished', 'riskScore', 'atRisk', 'fullChurnRisk', 'paymentChange'] as const;
type SortField = (typeof SORTS)[number];
/** Первый клик: числа — по убыванию, текст и ID — по возрастанию; платежи — сначала самое сильное снижение. */
const sortLabel: Record<SortField, string> = {
  id: 'ID', email: 'Email', balance: 'Баланс', totalReplenished: 'Пополнено всего', riskScore: 'Score', atRisk: 'Под риском',
  fullChurnRisk: 'Уход целиком', paymentChange: 'Платежи',
};
const firstDir: Record<SortField, 'asc' | 'desc'> = {
  id: 'asc', email: 'asc', balance: 'desc', totalReplenished: 'desc', riskScore: 'desc', atRisk: 'desc', fullChurnRisk: 'desc', paymentChange: 'asc',
};
const LEVELS = ['high', 'medium', 'low', 'unknown'];
const PAY: PaymentTrendState[] = ['decline', 'stable', 'growth', 'no_payments', 'insufficient_history'];
const list = (v: string | null, allowed: readonly string[]) => (v ?? '').split(',').filter((x) => allowed.includes(x));

function readQuery(p: URLSearchParams): CustomerQuery {
  const [f, d] = (p.get('sort') ?? 'riskScore:desc').split(':');
  const sortBy = (SORTS as readonly string[]).includes(f ?? '') ? (f as SortField) : 'riskScore';
  return {
    sortBy, sortDirection: d === 'asc' ? 'asc' : 'desc',
    levels: list(p.get('level'), LEVELS),
    state: p.get('state') ?? '', flag: p.get('flag') ?? '', search: p.get('q') ?? '',
    page: Math.max(1, Number(p.get('page') ?? '1') || 1), pageSize: 50,
    fullChurnLevels: list(p.get('fc'), LEVELS),
    fullChurnState: p.get('fcstate') === 'churned' ? 'churned' : '',
    paymentTrends: list(p.get('pay'), PAY),
  };
}

export function CustomersPage() {
  const [params, setParams] = useSearchParams();
  const q = useMemo(() => readQuery(params), [params]);
  const { data, error, isLoading, isFetching } = useCustomers(q);
  const summary = useSummary();
  const navigate = useNavigate();
  const [search, setSearch] = useState(q.search);
  const changed = useChangedRows(data?.items, data?.generation.id);

  useEffect(() => setSearch(q.search), [q.search]);
  useEffect(() => {
    if (search === q.search) return;
    const t = window.setTimeout(() => update({ q: search || null, page: null }), 300);
    return () => window.clearTimeout(t);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [search]);

  function update(patch: Record<string, string | null>) {
    const next = new URLSearchParams(params);
    for (const [k, v] of Object.entries(patch)) if (v === null || v === '') next.delete(k); else next.set(k, v);
    setParams(next, { replace: false });
  }
  function toggleLevel(l: string) {
    const set = new Set(q.levels);
    if (set.has(l)) set.delete(l); else set.add(l);
    update({ level: [...set].join(',') || null, page: null });
  }
  function toggleList(key: 'fc' | 'pay', current: string[], v: string) {
    const set = new Set(current);
    if (set.has(v)) set.delete(v); else set.add(v);
    update({ [key]: [...set].join(',') || null, page: null });
  }
  function sortBy(f: SortField) {
    const dir = q.sortBy === f ? (q.sortDirection === 'asc' ? 'desc' : 'asc') : firstDir[f];
    update({ sort: `${f}:${dir}`, page: null });
  }

  const counts = summary.data?.customers.byLevel;
  const totalPages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1;
  const hasFilters = q.levels.length > 0 || !!q.state || !!q.flag || !!q.search || q.fullChurnLevels.length > 0 || !!q.fullChurnState || q.paymentTrends.length > 0;
  const fcCounts = summary.data?.churn?.fullChurn;
  const payCounts = summary.data?.churn?.paymentTrend.byState;

  const SortTh = ({ field, label, right, extra, hint }: { field: SortField; label: string; right?: boolean; extra?: string; hint?: string }) => {
    const active = q.sortBy === field;
    return (
      <th className={[right ? 'r' : '', extra ?? ''].join(' ').trim() || undefined} aria-sort={active ? (q.sortDirection === 'asc' ? 'ascending' : 'descending') : 'none'} scope="col">
        <button className="sort-btn" data-active={active} data-testid={`sort-${field}`} onClick={() => sortBy(field)} title={hint}
          aria-label={`Сортировать по «${hint ?? label}»${active ? (q.sortDirection === 'asc' ? ', сейчас по возрастанию' : ', сейчас по убыванию') : ''}`}>
          {label} <span className="arrow" aria-hidden="true">{active ? (q.sortDirection === 'asc' ? '↑' : '↓') : '↕'}</span>
        </button>
      </th>
    );
  };

  return (
    <>
      <Topbar title="Клиенты под риском" />
      <main id="main" className="content">
        <div className="toolbar" role="group" aria-label="Фильтры">
          {(['high', 'medium', 'low', 'unknown'] as RiskLevel[]).map((l) => (
            <button key={l} className="chip" aria-pressed={q.levels.includes(l)} onClick={() => toggleLevel(l)} data-testid={`filter-${l}`}>
              <span aria-hidden="true">{levelGlyph[l]}</span> {levelLabel[l]}{counts ? ` · ${counts[l]}` : ''}
            </button>
          ))}
          <select className="input" aria-label="Состояние оценки" value={q.state} onChange={(e) => update({ state: e.target.value || null, page: null })}>
            <option value="">Любое состояние оценки</option>
            {Object.entries(stateLabel).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
          </select>
          {q.flag && <Tag tone="warning">Флаг: {flagLabel[q.flag] ?? q.flag}</Tag>}
          <span className="spacer" />
          <input className="input" type="search" placeholder="Поиск по ID или email" aria-label="Поиск по ID или email" value={search}
            onChange={(e) => setSearch(e.target.value)} style={{ width: 280 }} data-testid="search" />
          {hasFilters && <button className="btn btn-ghost" onClick={() => setParams(new URLSearchParams(q.sortBy !== 'riskScore' || q.sortDirection !== 'desc' ? { sort: `${q.sortBy}:${q.sortDirection}` } : {}))}>Сбросить</button>}
          <a className="btn btn-secondary" href={exportUrl(q)} download data-testid="export-csv"
            title={`Выгрузить ${data?.total ?? ''} клиентов под текущими фильтрами (расчёт №${data?.generation.id ?? '—'}) — для сервиса возврата пользователей`}>
            ⤓ Экспорт CSV{data ? ` · ${data.total}` : ''}
          </a>
        </div>
        <div className="toolbar" role="group" aria-label="Фильтры типов оттока">
          <span className="caption secondary">Уход целиком:</span>
          {(['high', 'medium'] as RiskLevel[]).map((l) => (
            <button key={l} className="chip" aria-pressed={q.fullChurnLevels.includes(l)} onClick={() => toggleList('fc', q.fullChurnLevels, l)} data-testid={`filter-fc-${l}`}>
              <span aria-hidden="true">{levelGlyph[l]}</span> {levelLabel[l]}{fcCounts ? ` · ${fcCounts.byLevel[l]}` : ''}
            </button>
          ))}
          <button className="chip" aria-pressed={q.fullChurnState === 'churned'} onClick={() => update({ fcstate: q.fullChurnState ? null : 'churned', page: null })} data-testid="filter-churned">
            <span aria-hidden="true">×</span> Уже ушли{fcCounts ? ` · ${fcCounts.byState.churned}` : ''}
          </button>
          <span className="caption secondary" style={{ marginLeft: 8 }}>Платежи:</span>
          {(['decline', 'insufficient_history'] as PaymentTrendState[]).map((t) => (
            <button key={t} className="chip" aria-pressed={q.paymentTrends.includes(t)} onClick={() => toggleList('pay', q.paymentTrends, t)} data-testid={`filter-pay-${t}`}>
              {t === 'decline' ? '▼ Снижение' : '? Мало истории'}{payCounts ? ` · ${payCounts[t]}` : ''}
            </button>
          ))}
        </div>

        {error && !data && <ErrorBox error={error} />}
        {isLoading && <div className="card"><Skeleton lines={8} /></div>}
        {data && (
          <section className="card card-flush" aria-labelledby="table-title" aria-busy={isFetching}>
            <h2 id="table-title" className="sr-only">Рейтинг клиентов</h2>
            {data.items.length === 0 ? (
              <div className="empty">
                <h3>Нет клиентов под выбранные условия</h3>
                <button className="btn btn-secondary" onClick={() => setParams(new URLSearchParams())}>Сбросить фильтры</button>
              </div>
            ) : (
              <>
                <div className="table-wrap desktop-table">
                  <table className="data customers-table" data-testid="customers-table">
                    <thead>
                      <tr>
                        <SortTh field="id" label="ID" />
                        <SortTh field="email" label="Email" />
                        <SortTh field="balance" label="Баланс" right />
                        <SortTh field="totalReplenished" label="Пополнено" hint="Пополнено всего" right />
                        <SortTh field="riskScore" label="Score" right />
                        <th scope="col">Уровень</th>
                        <th scope="col">Главная причина</th>
                        <SortTh field="atRisk" label="Под риском" right />
                        <SortTh field="fullChurnRisk" label="Уход целиком" extra="col-churn" />
                        <SortTh field="paymentChange" label="Платежи" extra="col-churn" />
                      </tr>
                    </thead>
                    <tbody>
                      {data.items.map((c) => (
                        <Row key={c.id} c={c} delta={changed.get(c.id)} onOpen={() => navigate(`/customers/${c.id}`)} />
                      ))}
                    </tbody>
                  </table>
                </div>
                <div className="mobile-list">
                  <select className="input" aria-label="Сортировка" value={`${q.sortBy}:${q.sortDirection}`} onChange={(e) => update({ sort: e.target.value, page: null })}>
                    {SORTS.flatMap((f) => (['desc', 'asc'] as const).map((d) => <option key={`${f}:${d}`} value={`${f}:${d}`}>{sortLabel[f]} {d === 'asc' ? '↑' : '↓'}</option>))}
                  </select>
                  {data.items.map((c) => (
                    <Link key={c.id} className="card stack" to={`/customers/${c.id}`} style={{ color: 'inherit' }}>
                      <div className="row" style={{ justifyContent: 'space-between' }}>
                        <span className="ellipsis">#{c.id} · {c.email}</span>
                        <span className="row" style={{ gap: 6 }}><span className="score">{score(c.riskScore)}</span><RiskBadge level={c.riskLevel} /></span>
                      </div>
                      <span className="caption secondary">{c.primaryReason.text} · баланс {eur(c.balance)}</span>
                      <span className="row-wrap" style={{ gap: 6 }}>
                        <FullChurnChip fc={c.churn.fullChurn} prefix="Уход целиком" />
                        <PaymentCell p={c.churn.paymentTrend} />
                      </span>
                    </Link>
                  ))}
                </div>
                <div className="pager">
                  <span className="caption secondary" data-testid="pager-info">
                    {(data.page - 1) * data.pageSize + 1}–{Math.min(data.page * data.pageSize, data.total)} из {data.total} · расчёт №{data.generation.id} · равенства по ID ↑ · «нет оценки» всегда в конце
                  </span>
                  <span className="row" style={{ gap: 8 }}>
                    <button className="btn btn-secondary btn-sm" disabled={data.page <= 1} onClick={() => update({ page: String(data.page - 1) })}>← Назад</button>
                    <button className="btn btn-secondary btn-sm" disabled={data.page >= totalPages} onClick={() => update({ page: String(data.page + 1) })}>Вперёд →</button>
                  </span>
                </div>
              </>
            )}
          </section>
        )}
      </main>
    </>
  );
}

function Row({ c, delta, onOpen }: { c: CustomerListItem; delta: number | undefined; onOpen: () => void }) {
  return (
    <tr className={`clickable${delta !== undefined ? ' changed' : ''}`} onClick={onOpen} data-testid={`row-${c.id}`} data-score={c.riskScore ?? ''}>
      <td className="num">{c.id}</td>
      <td><Link to={`/customers/${c.id}`} className="ellipsis" onClick={(e) => e.stopPropagation()} title={c.email}>{c.email}</Link></td>
      <td className="r">{eur(c.balance)}</td>
      <td className="r">{eur(c.totalReplenished)}</td>
      <td className="r">
        <span className="score">{score(c.riskScore)}</span>
        {delta !== undefined && delta !== 0 && <span className={delta > 0 ? 'delta-up' : 'delta-down'}> {delta > 0 ? '▲' : '▼'}{score(Math.abs(delta))}</span>}
      </td>
      <td>
        <span className="row" style={{ gap: 6 }}>
          <RiskBadge level={c.riskLevel} />
          {c.scoreState === 'partial' && <Tag tone="warning">частично</Tag>}
        </span>
      </td>
      <td><span className="ellipsis small secondary" title={c.primaryReason.text}>{c.primaryReason.text}</span></td>
      <td className="r" style={{ color: c.money.atRiskMinor > 0 ? 'var(--color-risk-high-fg)' : 'var(--color-text-muted)' }}>
        {c.riskScore === null ? '—' : eur(c.money.atRiskMinor)}
      </td>
      <td className="col-churn"><FullChurnChip fc={c.churn.fullChurn} testId={`fc-${c.id}`} /></td>
      <td className="col-churn"><PaymentCell p={c.churn.paymentTrend} /></td>
    </tr>
  );
}

function PaymentCell({ p }: { p: CustomerListItem['churn']['paymentTrend'] }) {
  const cls = p.state === 'decline' ? 'pay-decline' : p.state === 'growth' ? 'pay-growth' : p.state === 'stable' ? 'pay-stable' : 'pay-muted';
  return (
    <span className={`pay ${cls}`} title={`Платежи: ${paymentTrendLabel[p.state]} · ${eur(p.currentMinor)} против ${eur(p.previousMinor)}`} data-state={p.state}>
      {paymentShort(p.state, p.changePct)}
    </span>
  );
}

/** Подсветка строк, у которых score изменился после нового поколения (3 с). */
function useChangedRows(items: CustomerListItem[] | undefined, generationId: number | undefined) {
  const prev = useRef<{ gen: number; scores: Map<number, number | null> } | null>(null);
  const [changed, setChanged] = useState<Map<number, number>>(new Map());
  useEffect(() => {
    if (!items || generationId === undefined) return;
    const scores = new Map(items.map((i) => [i.id, i.riskScore] as const));
    const p = prev.current;
    if (p && p.gen !== generationId) {
      const diff = new Map<number, number>();
      for (const [id, s] of scores) {
        if (!p.scores.has(id)) continue;
        const before = p.scores.get(id) ?? null;
        // Подсвечиваем заметные изменения (≥ 0,05 балла) и появление/пропажу оценки; дрейф от часов на сотые не мигает.
        if ((before === null) !== (s === null) || Math.abs((s ?? 0) - (before ?? 0)) >= 0.05) diff.set(id, (s ?? 0) - (before ?? 0));
      }
      if (diff.size > 0) {
        setChanged(diff);
        const t = window.setTimeout(() => setChanged(new Map()), 3000);
        prev.current = { gen: generationId, scores };
        return () => window.clearTimeout(t);
      }
    }
    prev.current = { gen: generationId, scores };
  }, [items, generationId]);
  return changed;
}
