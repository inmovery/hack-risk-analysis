import { useRef, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { api, ApiError } from '../api/client';
import type { MlMetric, MlShadowDetail, MlStatus } from '../api/types';
import { dateTime } from '../format';
import { Banner, Tag } from './ui';

/** Названия признаков модели для людей */
export const mlFeatureLabel: Record<string, string> = {
  term_week: 'оплата понедельно', term_month: 'оплата помесячно', term_quarter: 'оплата поквартально', term_half_year: 'оплата раз в полгода', term_year: 'оплата раз в год',
  price_month_eur: 'цена в месяц', auto_prolong: 'автопродление включено', status_suspended: 'сервер выключен (suspended)', status_prolong_wait: 'ожидает оплаты',
  days_to_expiry: 'дней до срока', service_age_days_log: 'возраст услуги', funds_cover: 'баланс покрывает продление', balance_eur_log: 'баланс',
  bonus_unlocked: 'бонусы доступны', withdraw_eur_log: 'withdraw-баланс', live_services: 'услуг у клиента', tenure_days_log: 'стаж клиента',
  has_behavior: 'есть данные поведения', logins_7: 'входы за 7 дн.', logins_30: 'входы за 30 дн.', days_since_login: 'дней без входа',
  cpu_7: 'нагрузка CPU за 7 дн.', cpu_base: 'нагрузка CPU раньше', cpu_ratio: 'тренд нагрузки', usage_days_7: 'дней телеметрии за 7 дн.',
  failed_payments_30: 'неудачные платежи', payments_30_eur_log: 'платежи за 30 дн.', topups_30: 'пополнения за 30 дн.', topups_90_eur_log: 'пополнения за 90 дн.',
  tickets_30: 'обращения', unsolved_30: 'нерешённые обращения', csat_min_30: 'худшая оценка поддержки', nps_min_90: 'NPS', incident_min_30_log: 'минуты аварий',
  downgrades_60: 'даунгрейды', upgrades_60: 'апгрейды', autoprolong_off_30: 'выключал автопродление', suspends_30: 'выключал сервер', backups_14: 'бэкапы',
  withdrawals_30_eur_log: 'вывод средств', bonus_expired_45: 'сгорели бонусы (конец промо)', deletions_90: 'удалял услуги', renewals_90: 'продления за 90 дн.',
};

const kindLabel: Record<string, string> = { logistic: 'логистическая регрессия', fasttree: 'градиентный бустинг (FastTree)' };

const n3 = (v: number | null | undefined) => (v == null ? '—' : v.toLocaleString('ru-RU', { minimumFractionDigits: 2, maximumFractionDigits: 2 }));
const ci = (v: number[] | null | undefined) => (v && v.length === 2 ? `[${n3(v[0])}; ${n3(v[1])}]` : '');

function MetricRow({ label, m, strong }: { label: string; m: MlMetric | null; strong?: boolean }) {
  if (!m) return null;
  return (
    <tr>
      <td>{strong ? <strong>{label}</strong> : label}</td>
      <td className="r num">{n3(m.prAuc)}</td>
      <td className="r num">{n3(m.precisionAtK)}</td>
      <td className="r num">{n3(m.rocAuc)}</td>
    </tr>
  );
}

/** Модель на фактах: статус, сравнение с правилами, тень и проверка на исходах */
export function MlCard({ ml, isAdmin }: { ml: MlStatus; isAdmin: boolean }) {
  const qc = useQueryClient();
  const input = useRef<HTMLInputElement>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const s = ml.shadow;

  async function run(action: () => Promise<unknown>) {
    setBusy(true);
    setError(null);
    try {
      await action();
      await qc.invalidateQueries({ queryKey: ['source', 'ml'] });
    } catch (e) {
      setError(e instanceof ApiError ? `${e.message}${e.code ? ` (${e.code})` : ''}` : (e as Error).message);
    } finally {
      setBusy(false);
      if (input.current) input.current.value = '';
    }
  }

  const upload = (file: File) => run(() => api('/api/v1/ml/model', { method: 'PUT', rawBody: file, headers: { 'Content-Type': 'application/zip' } }));
  const off = () => run(() => api('/api/v1/ml/model', { method: 'DELETE' }));

  return (
    <section className="card stack" aria-labelledby="ml-title" data-testid="ml-status" data-status={ml.status}>
      <div className="row" style={{ justifyContent: 'space-between', alignItems: 'baseline' }}>
        <h2 id="ml-title">Модель на фактах (ML)</h2>
        {s ? <Tag tone="info" title="Прогнозы рядом с правилами; баллы, уровни, сортировки и деньги считают правила">в тени</Tag> : <Tag>не загружена</Tag>}
      </div>

      {!s && (
        <>
          <div className="row" style={{ justifyContent: 'space-between' }}><span className="small secondary">Исходов созрело (продлено / не продлено)</span><strong className="num">{ml.labels.renewed} / {ml.labels.notRenewed}</strong></div>
          <div className="row" style={{ justifyContent: 'space-between' }}><span className="small secondary">Нужно каждого класса</span><strong className="num">≥ {ml.labels.requiredPerClass}</strong></div>
          <div className="row" style={{ justifyContent: 'space-between' }}><span className="small secondary">Ждут исхода</span><strong className="num">{ml.labels.pending}</strong></div>
          <p className="caption muted">{ml.reason} Обучение — офлайн на журнале событий; модель включается только в тени и заменит правила, лишь если выиграет на реальных исходах.</p>
        </>
      )}

      {s && (
        <>
          {s.model.synthetic && <Banner tone="warning">Модель обучена на синтетике симулятора: цифры ниже проверяют конвейер, а не качество на реальных клиентах.</Banner>}
          <p className="small" style={{ margin: 0 }}>
            <strong>{s.model.version}</strong><br />
            <span className="secondary">{kindLabel[s.model.kind] ?? s.model.kind} · загружена {dateTime(s.model.uploadedAt)} · {s.model.uploadedBy}</span>
          </p>

          <h3 className="small secondary">Отложенный период {s.offline.testFrom ? `с ${dateTime(s.offline.testFrom)}` : ''} · {s.offline.testRows} решений, «не продлено» {s.offline.testPositives}</h3>
          <div className="table-wrap">
            <table className="data" data-testid="ml-offline">
              <thead><tr><th>Ранжир</th><th className="r" title="Площадь под кривой точность–полнота">PR-AUC</th><th className="r" title="Доля «не продлено» среди 5% решений с наибольшим риском">P@5%</th><th className="r">ROC-AUC</th></tr></thead>
              <tbody>
                <MetricRow label="Правила (панель)" m={s.offline.rules} />
                <MetricRow label="Правила, окно 30 дн." m={s.offline.rulesWide} />
                <MetricRow label="Модель" m={s.offline.modelMetrics} strong />
              </tbody>
            </table>
          </div>
          <p className="caption secondary">
            PR-AUC модели {ci(s.offline.prAucCi95)}; разница с лучшими правилами {ci(s.offline.prAucMinusRulesCi95)}, по P@5% {ci(s.offline.precisionAtKMinusRulesCi95)} (95%, bootstrap по клиентам).
            {s.offline.shuffledLabelsRocAuc != null && ` На перемешанных метках ROC-AUC ${n3(s.offline.shuffledLabelsRocAuc)} — утечки через конвейер не видно.`}
          </p>

          {s.offline.topFeatures.length > 0 && (
            <>
              <h3 className="small secondary" title="На сколько падает PR-AUC, если перемешать признак между решениями">Сильнее всего влияют на ранжир (вклад в PR-AUC)</h3>
              <ol className="small" style={{ margin: 0, paddingLeft: 18 }} data-testid="ml-top-features">
                {s.offline.topFeatures.slice(0, 5).map((f) => <li key={f.name}>{mlFeatureLabel[f.name] ?? f.name} <span className="secondary num">· {n3(f.prAucDrop)}</span></li>)}
              </ol>
            </>
          )}

          <h3 className="small secondary">В текущем расчёте</h3>
          {!s.generation && <p className="small secondary" role="status">Пересчёт с моделью ещё идёт…</p>}
          {s.generation?.state === 'failed' && <Banner tone="danger">Модель не выполнилась ({s.generation.errorCode}). Баллы и деньги посчитаны правилами, как обычно.</Banner>}
          {s.generation?.state === 'scored' && (
            <p className="small" style={{ margin: 0 }} data-testid="ml-generation">
              Услуг в окне модели: <strong className="num">{s.generation.servicesInScope}</strong> (срок через ≤ 14 дн., неделя — ≤ 3 дн.), клиентов: <strong className="num">{s.generation.customersScored}</strong>.
              {s.generation.topK > 0 && <> Совпадение с правилами в топ-5%: {s.generation.topKOverlap} из {s.generation.topK}.</>}
            </p>
          )}
          <p className="small" style={{ margin: 0 }} data-testid="ml-online">
            {s.online.model && s.online.rules
              ? <>На реальных исходах ({s.online.matured}, «не продлено» {s.online.notRenewed}): PR-AUC модели <strong className="num">{n3(s.online.model.prAuc)}</strong>, правил <strong className="num">{n3(s.online.rules.prAuc)}</strong>.</>
              : <>Прогнозов в точках решения: <strong className="num">{s.online.predictions}</strong>, исход известен у {s.online.matured}. Качество на реальных исходах покажем от 30 исходов каждого класса.</>}
          </p>
          <p className="caption muted">{s.model.synthetic ? 'Прогнозы в тени не меняют баллы, уровни, сортировки и деньги.' : s.note}</p>
        </>
      )}

      {isAdmin && (
        <div className="toolbar">
          <input ref={input} type="file" accept=".zip" aria-label="Пакет модели (model-bundle.zip)" data-testid="ml-file" hidden
            onChange={(e) => { const f = e.target.files?.[0]; if (f) void upload(f); }} />
          <button className="btn btn-sm" disabled={busy} onClick={() => input.current?.click()} data-testid="ml-upload">{s ? 'Заменить модель' : 'Загрузить модель (.zip)'}</button>
          {s && <button className="btn btn-ghost btn-sm" disabled={busy} onClick={() => void off()} data-testid="ml-off">Выключить</button>}
        </div>
      )}
      {error && <Banner tone="danger" role="alert">{error}</Banner>}
    </section>
  );
}

/** Теневая модель в карточке клиента: не влияет на score */
export function MlShadowCard({ shadow, services }: { shadow: MlShadowDetail; services: { id: number; name: string }[] }) {
  const name = (id: number | null) => services.find((s) => s.id === id)?.name ?? `#${id}`;
  const pct = (p: number) => `${Math.round(p * 100)}%`;
  return (
    <section className="card stack" aria-labelledby="mls-title" data-testid="ml-shadow" data-state={shadow.state}>
      <div className="row" style={{ justifyContent: 'space-between', alignItems: 'baseline' }}>
        <h2 id="mls-title">Модель в тени</h2>
        <Tag tone="info" title={`Модель ${shadow.modelVersion}`}>не влияет на score</Tag>
      </div>
      {shadow.state === 'scored' && shadow.probability != null && (
        <>
          <p className="small" style={{ margin: 0 }}>
            Вероятность «не продлено» по модели: <strong className="num">{pct(shadow.probability)}</strong> — {name(shadow.serviceId)}.
          </p>
          {shadow.services.length > 1 && (
            <ul className="small" style={{ margin: 0, paddingLeft: 16 }}>
              {shadow.services.map((s) => (
                <li key={s.serviceId}>{name(s.serviceId)}: {pct(s.probability)} · срок через {s.daysToExpiry.toLocaleString('ru-RU', { maximumFractionDigits: 1 })} дн. · score правил {s.rulesScore ?? '—'}</li>
              ))}
            </ul>
          )}
        </>
      )}
      {shadow.state === 'out_of_scope' && <p className="small secondary" style={{ margin: 0 }}>Нет услуг со сроком в окне модели: за 14 дней и ближе (неделя — за 3 дня), active или suspended.</p>}
      {shadow.state === 'failed' && <p className="small secondary" style={{ margin: 0 }}>Модель не выполнилась в этом расчёте; score и деньги посчитаны правилами.</p>}
      <p className="caption muted">Прогноз сверяется с исходами сроков и не меняет балл, уровень, сортировку и деньги. Как модель обучена и проверена — на странице «Модель».</p>
    </section>
  );
}
