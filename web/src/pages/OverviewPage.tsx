import { useState } from 'react';
import { Link, useNavigate } from 'react-router';
import { useHistory, useSettings, useSummary } from '../api/queries';
import { Topbar } from '../layout/AppShell';
import { Banner, ErrorBox, KpiCard, RiskBadge, Skeleton, Tag } from '../components/ui';
import { ForecastChart } from '../components/ForecastChart';
import { FactChart, FactLegend } from '../components/FactChart';
import { age, dateTime, eur, flagLabel, score } from '../format';
import type { HistoryResponse, RiskLevel, SummaryResponse } from '../api/types';

export function OverviewPage() {
  const { data, error, isLoading } = useSummary();
  const history = useHistory(30);
  const settings = useSettings();
  const [tab, setTab] = useState<'forecast' | 'fact'>('forecast');
  const navigate = useNavigate();
  return (
    <>
      <Topbar title="Обзор" />
      <main id="main" className="content">
        {isLoading && <Skeleton lines={6} />}
        {error && !data && <ErrorBox error={error} />}
        {data && (
          <>
            {data.generation.sourceKind === 'stream' && data.generation.synthetic && (
              <Banner tone="info">
                Источник: тестовые данные на {dateTime(data.generation.sourceAsOf)} + поток событий{history.data?.coverage.streamStartedAt ? ` с ${dateTime(history.data.coverage.streamStartedAt)}` : ''}.
                Среди данных есть синтетика (тестовые данные или события симулятора): факты и исходы — демонстрационные.
              </Banner>
            )}
            {data.generation.sourceKind === 'snapshot' && data.generation.sourceAgeSeconds > 24 * 3600 && (
              <Banner tone="warning">
                Тестовые данные на {dateTime(data.generation.sourceAsOf)} — {age(data.generation.sourceAgeSeconds)} назад.{' '}
                {data.generation.clockMode === 'live'
                  ? 'Оценка на текущее время (режим live); статусы услуг — по последним данным источника.'
                  : 'Оценка выполнена на момент выгрузки данных (режим «на момент выгрузки данных»).'}
                {data.generation.synthetic && ' Тестовые данные помечены как синтетические (демо).'}
              </Banner>
            )}
            <section className="grid-kpi" aria-label="Ключевые показатели">
              <KpiCard primary testId="kpi-at-risk" label={`Выручка под риском · ${data.money.horizonDays} дн.`} value={eur(data.money.atRiskMinor)}
                caption="Σ продлений × score/100. Индекс, не вероятность." to="/customers?sort=atRisk:desc" />
              <KpiCard testId="kpi-planned" label={`Плановые продления · ${data.money.horizonDays} дн.`} value={eur(data.money.plannedRenewalsMinor)}
                caption="active / suspended / prolong_wait при текущих ценах" />
              <KpiCard testId="kpi-unfunded" label={`Не покрыто балансом · ${data.money.horizonDays} дн.`} value={eur(data.money.unfundedMinor)}
                caption="баланс и разрешённые бонусы тратятся один раз" />
              <div className="card stack" aria-label="Клиенты по уровням">
                <span className="kpi-label">Клиенты по уровням · всего {data.customers.total}</span>
                {(['high', 'medium', 'low', 'unknown'] as RiskLevel[]).map((l) => (
                  <Link key={l} to={`/customers?level=${l}`} className="row" style={{ justifyContent: 'space-between', alignItems: 'center', color: 'inherit' }}
                    data-testid={`level-count-${l}`}>
                    <RiskBadge level={l} />
                    <strong className="num">{data.customers.byLevel[l]}</strong>
                  </Link>
                ))}
              </div>
            </section>

            <ChurnTypes data={data} history={history.data} declinePct={settings.data?.latest.settings.churn?.paymentDeclinePct ?? 30} />

            <details className="card">
              <summary className="small secondary" style={{ cursor: 'pointer' }}>Что означают эти суммы</summary>
              <ul className="small secondary">
                <li><strong>Выручка под риском.</strong> {data.definitions.atRisk}</li>
                <li><strong>Плановые продления.</strong> {data.definitions.planned}</li>
                <li><strong>Не покрыто балансом.</strong> {data.definitions.unfunded}</li>
                <li><strong>Score.</strong> {data.definitions.score}</li>
                {data.definitions.fullChurn && <li><strong>Уход целиком.</strong> {data.definitions.fullChurn}</li>}
                {data.definitions.paymentTrend && <li><strong>Снижение платежей.</strong> {data.definitions.paymentTrend}</li>}
                {data.definitions.expiryOutcomes && <li><strong>Исходы сроков.</strong> {data.definitions.expiryOutcomes}</li>}
              </ul>
            </details>

            <section className="grid-2">
              <div className="card stack">
                <div className="tabs" role="tablist" aria-label="Прогноз или факт">
                  <button className="tab" role="tab" aria-selected={tab === 'forecast'} aria-controls="chart-panel" onClick={() => setTab('forecast')} data-testid="tab-forecast">Прогноз</button>
                  <button className="tab" role="tab" aria-selected={tab === 'fact'} aria-controls="chart-panel" onClick={() => setTab('fact')} data-testid="tab-fact">Факт (журнал)</button>
                </div>
                <div id="chart-panel" role="tabpanel">
                  {tab === 'forecast' ? (
                    <>
                      <div className="card-head">
                        <h2>Прогноз продлений по неделям · сценарий</h2>
                        <span className="legend">
                          <span className="swatch" style={{ background: 'var(--color-chart-planned)' }} /> План (прогноз)
                          <span className="swatch" style={{ background: 'var(--color-chart-at-risk)' }} /> Под риском (прогноз)
                        </span>
                      </div>
                      <ForecastChart points={data.forecast} />
                      <p className="caption muted">Условные продления при сохранении услуг и текущих цен; почасовые услуги дают до 24 оплат в день.</p>
                    </>
                  ) : (
                    <>
                      <div className="card-head">
                        <h2>События по дням получения · факт</h2>
                        <FactLegend />
                      </div>
                      {history.data && history.data.days.length > 0 ? <FactChart days={history.data.days} /> : (
                        <div className="empty secondary" data-testid="fact-empty">
                          <strong>Факта пока нет</strong>
                          <span className="small">Журнал заполняется с первого события источника; историю до подключения потока не восстанавливаем.</span>
                        </div>
                      )}
                      <p className="caption muted">{history.data?.note}</p>
                    </>
                  )}
                </div>
              </div>
              <FactPanel history={history.data} summary={data} />
            </section>

            <section className="card card-flush" aria-labelledby="top-title">
              <div className="card-head" style={{ padding: '16px 20px 0' }}>
                <h2 id="top-title">Топ под риском</h2>
                <Link to="/customers?sort=atRisk:desc">Все клиенты →</Link>
              </div>
              {data.topAtRisk.length === 0 ? (
                <div className="empty secondary">Нет клиентов с выручкой под риском.</div>
              ) : (
                <div className="table-wrap">
                  <table className="data">
                    <thead>
                      <tr><th>ID</th><th>Email</th><th className="r">Score</th><th>Уровень</th><th>Главная причина</th><th className="r">Под риском</th></tr>
                    </thead>
                    <tbody>
                      {data.topAtRisk.map((c) => (
                        <tr key={c.id} className="clickable" onClick={() => navigate(`/customers/${c.id}`)}>
                          <td className="num">{c.id}</td>
                          <td><Link to={`/customers/${c.id}`} onClick={(e) => e.stopPropagation()}>{c.email}</Link></td>
                          <td className="r"><span className="score">{score(c.riskScore)}</span></td>
                          <td><RiskBadge level={c.riskLevel} /></td>
                          <td><span className="ellipsis small secondary" title={c.primaryReasonText}>{c.primaryReasonText}</span></td>
                          <td className="r" style={{ color: 'var(--color-risk-high-fg)' }}>{eur(c.atRiskMinor)}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </section>

            {Object.keys(data.qualityFlags).length > 0 && (
              <section className="card stack" aria-label="Качество данных">
                <h2>Качество данных</h2>
                <div className="row-wrap">
                  {Object.entries(data.qualityFlags).map(([f, n]) => (
                    <Link key={f} to={`/customers?flag=${f}`}>
                      <Tag tone="warning">{flagLabel[f] ?? f}: {n} клиент(ов)</Tag>
                    </Link>
                  ))}
                </div>
                <p className="caption muted">
                  Аналитик не переписывает статусы источника: например, prolong_wait дольше 5 дней остаётся prolong_wait, но отмечается флагом.
                  {data.services.unscored > 0 && ` Не оценено услуг: ${data.services.unscored} (их продления ${eur(data.money.unscoredPlannedMinor)} не входят в «под риском»).`}
                  {data.money.unfundedUnknownPlannedMinor > 0 && ` У части клиентов баланс неизвестен: покрытие продлений на ${eur(data.money.unfundedUnknownPlannedMinor)} не оценивается и не входит в «не покрыто».`}
                </p>
              </section>
            )}
          </>
        )}
      </main>
    </>
  );
}

/** Три типа оттока и исходы сроков: каждая карточка ведёт в рейтинг с фильтром */
function ChurnTypes({ data, history, declinePct }: { data: SummaryResponse; history: HistoryResponse | undefined; declinePct: number }) {
  const fc = data.churn?.fullChurn;
  const pt = data.churn?.paymentTrend;
  const o = history?.outcomes;
  return (
    <section className="grid-churn" aria-label="Типы оттока" data-testid="churn-types">
      <Link className="card churn-card" to="/customers?level=high,medium">
        <span className="kpi-label">Непродление услуг · {data.money.horizonDays} дн.</span>
        <span className="churn-value">{data.customers.byLevel.high} высокий · {data.customers.byLevel.medium} средний</span>
        <span className="small secondary">Индекс риска услуг (score), не вероятность</span>
        <span className="small" style={{ color: 'var(--color-accent-default)' }}>Открыть рейтинг →</span>
      </Link>
      <Link className="card churn-card" to="/customers?fc=high,medium&sort=fullChurnRisk:desc" data-testid="churn-full">
        <span className="kpi-label">Уход клиента целиком</span>
        <span className="churn-value">{fc ? `${fc.byLevel.high} высокий · ${fc.byLevel.medium} средний` : '—'}</span>
        <span className="small secondary">Уже ушли: {fc?.byState.churned ?? 0} — все услуги удалены</span>
        <span className="small" style={{ color: 'var(--color-accent-default)' }}>Кандидаты на возврат →</span>
      </Link>
      <Link className="card churn-card" to="/customers?pay=decline&sort=paymentChange:asc" data-testid="churn-payments">
        <span className="kpi-label">Снижение платежей · {pt?.windowDays ?? 30} дн.</span>
        <span className="churn-value">{pt ? `${pt.byState.decline} клиент(ов) ▼ ≥ ${String(declinePct).replace('.', ',')} %` : '—'}</span>
        <span className="small secondary">Мало истории: {pt?.byState.insufficient_history ?? 0} · без платежей: {pt?.byState.no_payments ?? 0}</span>
        <span className="small" style={{ color: 'var(--color-accent-default)' }}>Открыть список →</span>
      </Link>
      <Link className="card churn-card" to="/source" data-testid="churn-outcomes">
        <span className="kpi-label">Исходы сроков · N = {o?.graceDays ?? '—'} дн.</span>
        <span className="churn-value">продлено {o?.renewed ?? 0} · не продлено {o?.notRenewed ?? 0}</span>
        <span className="small secondary">
          {o?.notRenewedRatePct !== null && o?.notRenewedRatePct !== undefined ? `Доля непродления ${String(o.notRenewedRatePct).replace('.', ',')} %` : 'Доля непродления — нет исходов'} · ждут исхода {o?.pending ?? 0}
        </span>
        <span className="small" style={{ color: 'var(--color-accent-default)' }}>Журнал источника →</span>
      </Link>
    </section>
  );
}

/** Сводка фактов журнала за 7 дней: только наблюдённое после подключения потока */
function FactPanel({ history, summary }: { history: HistoryResponse | undefined; summary: SummaryResponse }) {
  const last = (history?.days ?? []).slice(-7);
  const sum = (f: (d: (typeof last)[number]) => number) => last.reduce((a, d) => a + f(d), 0);
  const o = history?.outcomes;
  return (
    <div className="card stack" data-testid="fact-panel">
      <h2>Факт · журнал событий, 7 дн.</h2>
      {!history?.coverage.streamStartedAt ? (
        <p className="small secondary">{history?.note ?? 'Журнал пуст.'}</p>
      ) : (
        <div className="fact-list small">
          <div><span className="secondary">Продлено сроков</span><strong className="num" style={{ color: 'var(--color-status-success-fg)' }}>{sum((d) => d.renewed)}</strong></div>
          <div><span className="secondary">Не продлено за {o?.graceDays ?? 5} дн. (всего)</span><strong className="num" style={{ color: 'var(--color-status-danger-fg)' }}>{o?.notRenewed ?? 0}</strong></div>
          <div><span className="secondary">Удалено услуг</span><strong className="num" style={{ color: 'var(--color-status-danger-fg)' }}>{sum((d) => d.deleted)}</strong></div>
          <div><span className="secondary">Ушли целиком</span><strong className="num" style={{ color: 'var(--color-status-danger-fg)' }}>{sum((d) => d.customersLostAll)}</strong></div>
          <div><span className="secondary">Перешли в ожидание оплаты</span><strong className="num" style={{ color: 'var(--color-status-warning-fg)' }}>{sum((d) => d.toProlongWait)}</strong></div>
          <div><span className="secondary">Платежи</span><strong className="num">{eur(sum((d) => d.paymentsMinor))} · {sum((d) => d.payments)}</strong></div>
          <div><span className="secondary">Пополнения</span><strong className="num">{eur(sum((d) => d.topUpsMinor))} · {sum((d) => d.topUps)}</strong></div>
        </div>
      )}
      <p className="caption muted">Только события, полученные после подключения потока (время = время получения). «Не продлено» — только при свежем источнике.</p>
      <div className="row-wrap" style={{ gap: 6 }}>
        {history?.coverage.streamStartedAt && <Tag>журнал с {dateTime(history.coverage.streamStartedAt)}{history.coverage.synthetic ? ' · синтетика' : ''}</Tag>}
        <Tag>Расхождения между загрузками тестовых данных: {summary.history.eventsTotal}</Tag>
      </div>
    </div>
  );
}
