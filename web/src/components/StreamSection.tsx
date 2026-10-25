import { useEffect, useRef, useState } from 'react';
import { Link } from 'react-router';
import { useJournal, useStream } from '../api/queries';
import { Banner, Tag } from './ui';
import { ImportPanel, ImportProgress } from './ImportPanel';
import { age, dateTime, localDateTime } from '../format';
import { describeEvent, kindLabel, outcomeLabel, outcomeTone } from '../journal';
import type { Connector, JournalItem, StreamStatus } from '../api/types';
import { Select } from './controls';

const secondsSince = (iso: string | null) => (iso ? Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000) : 0);

/** Источник как поток: входящие коннекторы, качество, журнал событий */
export function StreamSection({ isAdmin }: { isAdmin: boolean }) {
  const stream = useStream();
  const s = stream.data;
  const q = s?.quality;
  return (
    <>
      {s && s.silent && (
        <Banner tone="warning">
          Поток молчит: последнее событие {dateTime(s.lastEventAt)} ({age(secondsSince(s.lastEventAt))} назад, порог {s.silentAfterMinutes} мин).
          Расчёт продолжает учитывать время, но новые изменения источника не видны.
        </Banner>
      )}
      {s && s.orphanServices > 0 && (
        <Banner tone="warning">
          Услуг без аккаунта: {s.orphanServices}{q?.oldestOrphanAt ? ` (самая давняя ждёт с ${dateTime(q.oldestOrphanAt)})` : ''} — источник
          ещё не прислал их аккаунт. Пока аккаунта нет, услуги не оцениваются; сам анализатор за ним не обращается.
        </Banner>
      )}
      {q && q.accountsWithGaps > 0 && (
        <Banner tone="warning">
          Пропуски в потоке: у {q.accountsWithGaps} аккаунт(ов) не хватает событий по номеру accountSeq. Источнику нужно переотправить их —
          повтор с тем же eventId безопасен.
        </Banner>
      )}
      <section className="grid-connectors" aria-label="Коннекторы источника" data-testid="connectors">
        {s ? s.connectors.map((c) => <ConnectorCard key={c.key} c={c} s={s} />) : <div className="card">Загрузка…</div>}
      </section>
      {isAdmin && <ImportPanel />}
      <Journal />
    </>
  );
}

/** Строки состояния входящего коннектора: последнее событие, задержка и водяной знак */
function pushStatus(c: Connector): { status: string; lines: string[] } {
  const status = c.status === 'idle' ? 'событий пока нет'
    : c.status === 'silent' ? `молчит ${age(secondsSince(c.lastReceivedAt))}`
    : `работает · последнее событие ${age(secondsSince(c.lastReceivedAt))} назад`;
  const lines = [`Принято событий: ${c.events}`];
  if (c.lagSeconds !== null) lines.push(`Самое свежее событие произошло ${age(c.lagSeconds)} назад`);
  lines.push(c.watermark
    ? `Источник подтвердил: всё до ${dateTime(c.watermark)} отправлено`
    : 'Водяного знака нет: «не продлено» решается по последнему изменению источника');
  if (c.rejected || c.duplicates) lines.push(`Отклонено ${c.rejected} · повторов eventId ${c.duplicates}`);
  if (c.lastError) lines.push(`Последняя ошибка ${c.lastErrorAt ? dateTime(c.lastErrorAt) : ''}: ${c.lastError}`);
  return { status, lines };
}

function ConnectorCard({ c, s }: { c: Connector; s: StreamStatus }) {
  const dot = c.status === 'ok' ? 'dot-ok' : c.status === 'silent' ? 'dot-warn' : 'dot-off';
  let title: string, status: string, lines: string[], endpoint: string;
  const l24 = s.last24h;
  if (c.key === 'snapshot') {
    title = 'Тестовые данные (из файла)';
    status = s.base ? `ревизия r${s.base.revision} · данные на ${dateTime(s.base.sourceAsOf)}` : 'тестовые данные не загружены';
    lines = [
      s.base?.synthetic ? 'База — данные симулятора' : 'База — тестовый набор / данные источника',
      `Поверх базы применено событий: ${s.eventsSinceBase}`,
    ];
    endpoint = 'PUT /api/v1/source/snapshot · полная замена (ниже)';
  } else {
    ({ status, lines } = pushStatus(c));
    if (c.key === 'http-push') {
      title = 'Поток событий (HTTP push)';
      lines = [
        `За 24 ч: применено ${l24.applied ?? 0} · без изменений ${l24.no_change ?? 0} · устаревших ${l24.stale ?? 0}`,
        `Доменных событий v2 за 24 ч: ${s.quality.domainEvents24h}${s.quality.unknownTypes24h ? ` · неизвестных типов ${s.quality.unknownTypes24h}` : ''}`,
        ...lines,
        ...(s.synthetic ? ['Есть события симулятора'] : []),
      ];
      endpoint = c.enabled ? 'POST /api/v1/source/events · Bearer INGEST_TOKEN' : 'INGEST_TOKEN не задан: принимаются только пакеты администратора';
    } else if (c.key === 'batch-import') {
      title = 'Импорт истории (файл)';
      status = c.lastImport ? `последний импорт №${c.lastImport.id} · ${c.lastImport.status === 'succeeded' ? 'выполнен' : c.lastImport.status === 'failed' ? 'не выполнен' : 'выполняется'}` : 'импортов не было';
      endpoint = 'POST /api/v1/source/events/import · JSONL, можно gzip';
    } else if (c.key === 'panel') {
      title = 'Пакеты из панели';
      endpoint = 'POST /api/v1/source/events · вход администратора';
    } else {
      title = `Коннектор «${c.key}»`;
      endpoint = c.enabled ? 'POST /api/v1/source/events · свой Bearer-токен (Auth:IngestConnectors)' : 'токена в конфигурации больше нет — события прошлые';
    }
  }
  return (
    <div className="card stack" data-testid={`connector-${c.key}`} data-status={c.status}>
      <div className="row" style={{ gap: 8, alignItems: 'center' }}>
        <span className={`dot ${dot}`} aria-hidden="true" />
        <h2>{title}</h2>
      </div>
      <span className="small" style={{ fontWeight: 500, color: 'var(--color-text-secondary)' }}>{status}</span>
      {lines.map((l) => <span key={l} className="small secondary">{l}</span>)}
      {c.key === 'batch-import' && c.lastImport && <ImportProgress view={c.lastImport} />}
      <span className="caption muted">{endpoint}</span>
    </div>
  );
}

/** Журнал: новые сверху, фильтры, «загрузить ещё»; новые строки подсвечиваются на 3 с */
function Journal() {
  const [outcome, setOutcome] = useState('');
  const [kind, setKind] = useState('');
  const [pages, setPages] = useState<(number | null)[]>([null]);
  useEffect(() => setPages([null]), [outcome, kind]);
  const first = useJournal({ outcome, kind }, null);
  const seen = useRef<Set<number> | null>(null);
  const [fresh, setFresh] = useState<Set<number>>(new Set());
  useEffect(() => {
    const items = first.data?.items ?? [];
    if (seen.current) {
      const added = new Set(items.filter((i) => !seen.current!.has(i.seq)).map((i) => i.seq));
      if (added.size > 0) {
        setFresh(added);
        const t = window.setTimeout(() => setFresh(new Set()), 3000);
        seen.current = new Set(items.map((i) => i.seq));
        return () => window.clearTimeout(t);
      }
    }
    seen.current = new Set(items.map((i) => i.seq));
  }, [first.data]);

  return (
    <section className="card card-flush" aria-labelledby="journal-title" data-testid="journal">
      <div className="card-head" style={{ padding: '16px 20px 8px', marginBottom: 0 }}>
        <h2 id="journal-title">Журнал событий источника</h2>
        <div className="toolbar" role="group" aria-label="Фильтр журнала">
          {['', 'applied', 'no_change', 'stale'].map((o) => (
            <button key={o || 'all'} className="chip" aria-pressed={outcome === o} onClick={() => setOutcome(o)} data-testid={`journal-filter-${o || 'all'}`}>
              {o ? outcomeLabel[o] : 'Все'}
            </button>
          ))}
          <Select ariaLabel="Тип записи" value={kind} onChange={setKind} testId="journal-kind" width={180}
            options={[{ value: '', label: 'Все записи' }, { value: 'service', label: 'Услуги' }, { value: 'account', label: 'Аккаунты' }, { value: 'transaction', label: 'Транзакции' }]} />
        </div>
      </div>
      {first.data && first.data.items.length === 0 ? (
        <div className="empty secondary" data-testid="journal-empty">
          <strong>Событий пока нет</strong>
          <span className="small">Источник ещё не присылал события. Адрес приёма: POST /api/v1/source/events (Bearer INGEST_TOKEN). Историю до подключения потока не восстанавливаем.</span>
        </div>
      ) : (
        <div className="table-wrap">
          <table className="data" data-testid="journal-table">
            <thead>
              <tr><th>Время события</th><th>Запись</th><th>Клиент</th><th>Исход</th><th>Изменения</th><th>Источник</th></tr>
            </thead>
            <tbody>
              {pages.map((before, i) => (
                <JournalRows key={before ?? 'first'} filter={{ outcome, kind }} before={before} fresh={fresh}
                  onNext={(next) => i === pages.length - 1 && next !== null && setPages((p) => [...p, next])} last={i === pages.length - 1} />
              ))}
            </tbody>
          </table>
        </div>
      )}
      <p className="caption muted" style={{ padding: '8px 20px 12px' }}>
        Время — когда событие произошло в источнике (v2: occurredAt; v1 — момент получения). Состояние записей не показывается — только исход и изменения «от → до».
      </p>
    </section>
  );
}

function JournalRows({ filter, before, fresh, onNext, last }: {
  filter: { outcome: string; kind: string }; before: number | null; fresh: Set<number>; onNext: (next: number | null) => void; last: boolean;
}) {
  const page = useJournal(filter, before);
  const items: JournalItem[] = page.data?.items ?? [];
  return (
    <>
      {items.map((e) => (
        <tr key={e.seq} className={fresh.has(e.seq) ? 'row-new' : undefined} data-testid="journal-row" data-outcome={e.outcome}>
          <td className="num" title={`Получено ${localDateTime(e.receivedAt)}`}>{localDateTime(e.occurredAt)}</td>
          <td>
            {kindLabel[e.kind]} #{e.entityId}
            {e.eventType && <div className="caption secondary" data-testid="journal-event-type">{e.eventType}</div>}
          </td>
          <td>{e.ownerId ? <Link to={`/customers/${e.ownerId}`}>#{e.ownerId}</Link> : <span className="secondary">неизвестен</span>}</td>
          <td><Tag tone={outcomeTone[e.outcome]}>{outcomeLabel[e.outcome]}</Tag></td>
          <td className="small">{describeEvent(e)}</td>
          <td className="caption secondary">{e.origin}{e.synthetic ? ' · симулятор' : ''}</td>
        </tr>
      ))}
      {last && page.data?.nextBeforeSeq !== null && page.data?.nextBeforeSeq !== undefined && (
        <tr>
          <td colSpan={6} style={{ textAlign: 'center' }}>
            <button className="btn btn-ghost btn-sm" onClick={() => onNext(page.data!.nextBeforeSeq)} data-testid="journal-more">Загрузить ещё</button>
          </td>
        </tr>
      )}
    </>
  );
}
