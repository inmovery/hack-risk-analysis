import { useEffect, useState } from 'react';
import { useCustomerActivity } from '../api/queries';
import { Sparkline } from './Sparkline';
import { Tag } from './ui';
import { dateTime, eur, localDateTime } from '../format';
import type { ActivityDay, CustomerActivity, EventDomain } from '../api/types';

const domains: { key: '' | EventDomain; label: string }[] = [
  { key: '', label: 'Все' }, { key: 'money', label: 'Деньги' }, { key: 'service', label: 'Услуги' }, { key: 'usage', label: 'Нагрузка и аварии' },
  { key: 'support', label: 'Поддержка' }, { key: 'account', label: 'Аккаунт и входы' }, { key: 'retention', label: 'Удержание' },
];
const domainLabel: Record<EventDomain, string> = {
  account: 'аккаунт', money: 'деньги', service: 'услуга', usage: 'нагрузка', support: 'поддержка', retention: 'удержание', other: 'другое',
};

function series(a: CustomerActivity): ActivityDay[] {
  const byDate = new Map(a.days.map((d) => [d.date, d]));
  const out: ActivityDay[] = [];
  for (let t = Date.parse(`${a.from}T00:00:00Z`); t <= Date.parse(`${a.to}T00:00:00Z`); t += 86_400_000) {
    const key = new Date(t).toISOString().slice(0, 10);
    out.push(byDate.get(key) ?? {
      date: key, logins: 0, cpuAvgPct: null, usageSamples: 0, trafficOutGb: 0, incidentMinutes: 0, ticketsOpened: 0, ticketsUnsolved: 0,
      paymentsCount: 0, paymentsMinor: 0, paymentsFailed: 0, topUpsMinor: 0, events: 0,
    });
  }
  return out;
}

const avg = (xs: (number | null)[]) => {
  const v = xs.filter((x): x is number => x !== null);
  return v.length ? v.reduce((s, x) => s + x, 0) / v.length : null;
};

/** Поведение клиента: спарклайны за 90 дней, итоги 30 дней и лента событий */
export function BehaviorCard({ id }: { id: number }) {
  const [domain, setDomain] = useState<'' | EventDomain>('');
  const [pages, setPages] = useState<(number | null)[]>([null]);
  useEffect(() => setPages([null]), [domain, id]);
  const first = useCustomerActivity(id, domain, null);
  const a = first.data;
  if (!a) return <section className="card" aria-label="Поведение клиента">Загрузка…</section>;
  if (a.knownSince === null) {
    return (
      <section className="card stack" aria-labelledby="bh-title" data-testid="behavior-card" data-empty="true">
        <h2 id="bh-title">Поведение клиента</h2>
        <p className="small secondary" data-testid="behavior-empty">
          Поведенческих данных нет: источник присылает только состояние записей (формат v1). Признаки поведения — «нет данных», а не ноль, и на балл не влияют.
        </p>
      </section>
    );
  }
  const days = series(a);
  const last30 = days.slice(-30);
  const cpu = days.map((d) => d.cpuAvgPct);
  const cpu7 = avg(cpu.slice(-7));
  const cpuBase = avg(cpu.slice(-37, -7));
  const sum = (f: (d: ActivityDay) => number) => last30.reduce((s, d) => s + f(d), 0);
  const tiles: [string, string, string][] = [
    ['Входов за 30 дн.', String(sum((d) => d.logins)), 'tile-logins'],
    ['CPU 7 дн. / 30 дн. до', `${cpu7 === null ? '—' : `${cpu7.toFixed(1)}%`} / ${cpuBase === null ? '—' : `${cpuBase.toFixed(1)}%`}`, 'tile-cpu'],
    ['Неудачных списаний', String(sum((d) => d.paymentsFailed)), 'tile-failed'],
    ['Обращений без решения', String(sum((d) => d.ticketsUnsolved)), 'tile-unsolved'],
    ['Аварии, мин', String(sum((d) => d.incidentMinutes)), 'tile-incidents'],
    ['Пополнения', eur(sum((d) => d.topUpsMinor)), 'tile-topups'],
  ];
  return (
    <section className="card stack" aria-labelledby="bh-title" data-testid="behavior-card">
      <div className="card-head" style={{ marginBottom: 0 }}>
        <h2 id="bh-title">Поведение клиента</h2>
        <span className="caption muted">события v2 с {dateTime(`${a.knownSince}T00:00:00Z`).slice(0, 10)} · за 90 дн. · по времени события</span>
      </div>
      <div className="grid-2">
        <div className="stack" style={{ gap: 4 }}>
          <span className="small secondary">Нагрузка CPU по дням, %</span>
          <Sparkline values={cpu} label={`Средняя загрузка CPU по дням за 90 дней; за последние 7 дней ${cpu7 === null ? 'нет данных' : `${cpu7.toFixed(1)}%`}`} testId="spark-cpu" />
        </div>
        <div className="stack" style={{ gap: 4 }}>
          <span className="small secondary">Входы в панель по дням</span>
          <Sparkline values={days.map((d) => d.logins)} kind="bars" label={`Входы по дням за 90 дней; за 30 дней ${sum((d) => d.logins)}`} testId="spark-logins" />
        </div>
      </div>
      <div className="row-wrap" style={{ gap: 16 }}>
        {tiles.map(([k, v, t]) => (
          <div key={k} className="kv" data-testid={t}><span className="kv-label">{k}</span><span className="num">{v}</span></div>
        ))}
      </div>
      <div className="toolbar" role="group" aria-label="Фильтр ленты событий">
        {domains.map((d) => (
          <button key={d.key || 'all'} className="chip" aria-pressed={domain === d.key} onClick={() => setDomain(d.key)} data-testid={`feed-filter-${d.key || 'all'}`}>
            {d.label}
          </button>
        ))}
      </div>
      <ul className="timeline" data-testid="behavior-feed">
        {pages.map((before, i) => (
          <FeedPage key={before ?? 'first'} id={id} domain={domain} before={before} last={i === pages.length - 1}
            onNext={(next) => i === pages.length - 1 && next !== null && setPages((p) => [...p, next])} />
        ))}
      </ul>
      <p className="caption muted">Лента — подписи из известных полей событий, без PII. Время — когда событие произошло в источнике.</p>
    </section>
  );
}

function FeedPage({ id, domain, before, last, onNext }: { id: number; domain: string; before: number | null; last: boolean; onNext: (n: number | null) => void }) {
  const page = useCustomerActivity(id, domain, before);
  const items = page.data?.events ?? [];
  if (page.data && items.length === 0 && before === null) return <li className="small secondary">Событий этого вида нет.</li>;
  return (
    <>
      {items.map((e) => (
        <li key={e.seq} data-testid="feed-item" data-domain={e.domain}>
          <span className={`tl-dot tl-${e.domain === 'money' || e.domain === 'support' ? 'warning' : 'neutral'}`} aria-hidden="true" />
          <div className="stack" style={{ gap: 2 }}>
            <span className="caption muted" title={`Получено ${localDateTime(e.receivedAt)}`}>{localDateTime(e.occurredAt)} · {domainLabel[e.domain]}</span>
            <span className="small">{e.text}{e.serviceId ? ` · услуга #${e.serviceId}` : ''}</span>
            <span className="caption secondary">
              {e.type}{e.synthetic ? ' · симулятор' : ''}{!e.knownType && <> · <Tag tone="warning">тип не из каталога</Tag></>}
            </span>
          </div>
        </li>
      ))}
      {last && page.data?.nextBeforeSeq != null && (
        <li><button className="btn btn-ghost btn-sm" onClick={() => onNext(page.data!.nextBeforeSeq)} data-testid="feed-more">Загрузить ещё</button></li>
      )}
    </>
  );
}
