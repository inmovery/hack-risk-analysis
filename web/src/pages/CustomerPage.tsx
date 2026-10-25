import { Fragment, useState } from 'react';
import { Link, useParams } from 'react-router';
import { useCustomer, useCustomerEvents } from '../api/queries';
import { Topbar } from '../layout/AppShell';
import { Banner, ChurnChip, ErrorBox, FullChurnChip, RiskBadge, Skeleton, Tag } from '../components/ui';
import { BehaviorCard } from '../components/BehaviorCard';
import { MlShadowCard } from '../components/MlCard';
import { dateTime, eur, flagLabel, levelLabel, localDateTime, score, stateLabel, statusLabel, termLabel } from '../format';
import { describeEvent, eventTone, kindLabel, outcomeLabel, paymentShort } from '../journal';
import type { ChurnDetail, CustomerDetail } from '../api/types';

export function CustomerPage() {
  const id = Number(useParams().id);
  const { data, error, isLoading } = useCustomer(id);
  return (
    <>
      <Topbar title={<><Link to="/customers" aria-label="Назад к рейтингу">←</Link> Клиент #{id}</>} />
      <main id="main" className="content">
        {isLoading && <Skeleton lines={8} />}
        {error && !data && ((error as { status?: number }).status === 404
          ? <Banner tone="warning">Клиента #{id} нет в текущих данных источника.</Banner>
          : <ErrorBox error={error} />)}
        {data && <Detail d={data.detail} horizon={data.generation.horizonDays} generationId={data.generation.id} />}
      </main>
    </>
  );
}

function Detail({ d, horizon, generationId }: { d: CustomerDetail; horizon: number; generationId: number }) {
  const c = d.customer;
  const worst = d.services.filter((s) => s.scored).sort((a, b) => (b.score ?? 0) - (a.score ?? 0) || a.id - b.id)[0];
  return (
    <>
      <section className="card row-wrap" style={{ alignItems: 'center', justifyContent: 'space-between' }} aria-label="Клиент">
        <div className="stack" style={{ minWidth: 260, flex: 1 }}>
          <h2 style={{ fontSize: 22, lineHeight: '28px' }} data-testid="customer-email">{c.email}</h2>
          <span className="small secondary">ID {c.id} · услуг {d.serviceCounts.total} (продлеваемых {d.serviceCounts.renewable}, оценено {d.serviceCounts.scored}) · bonusState: {c.bonusState ?? 'нет данных'}</span>
          <div className="row-wrap" style={{ gap: 6 }}>
            <Tag tone={d.scoreState === 'complete' ? 'neutral' : 'warning'}>{stateLabel[d.scoreState]}</Tag>
            {d.flags.map((f) => <Tag key={f} tone="warning">{flagLabel[f] ?? f}</Tag>)}
          </div>
          {d.churn && <ChurnChips d={d} churn={d.churn} />}
        </div>
        {[['Баланс', eur(c.balance)], [`Бонусы (${c.bonusState === 'unlocked' ? 'можно тратить' : c.bonusState === 'locked' ? 'заблокированы' : 'неизвестно'})`, eur(c.bonusBalance)],
          ['Withdraw (не тратится)', eur(c.withdrawBalance)], ['Пополнено всего', eur(c.totalReplenished)]].map(([k, v]) => (
          <div key={k} className="kv"><span className="kv-label">{k}</span><span className="num">{v}</span></div>
        ))}
        <div className="stack" style={{ alignItems: 'flex-end' }}>
          <span className="kpi-value" style={{ color: d.riskLevel === 'high' ? 'var(--color-risk-high-fg)' : undefined }} data-testid="customer-score">{score(d.riskScore)}</span>
          <RiskBadge level={d.riskLevel} />
          <span className="caption muted">индекс 0–100, не вероятность</span>
        </div>
      </section>

      {d.riskScore === null && <Banner tone="info">Оценки нет: {d.primaryReason.text}. Клиент не входит в денежные показатели и учитывается как «нет оценки».</Banner>}

      <div className="grid-2-wide">
        <section className="card" aria-labelledby="why-title">
          <h2 id="why-title">Почему такой риск</h2>
          <p className="caption secondary" style={{ margin: '4px 0 8px' }}>
            Score клиента = максимум по услугам; score услуги = min(100, Σ вес × сила признака).
            {worst && ` Худшая услуга: ${worst.name} (${score(worst.score)}${worst.capped ? `, сумма ${score(worst.rawScore)} ограничена 100` : ''}).`}
          </p>
          {d.reasons.length === 0 ? (
            <p className="small secondary">{d.primaryReason.text}</p>
          ) : (
            <>
              {d.reasons.filter((r) => r.serviceId === worst?.id).map((r, i) => <ReasonItem key={`w${i}`} r={r} />)}
              {d.reasons.some((r) => r.serviceId !== worst?.id) && (
                <h3 className="small secondary" style={{ marginTop: 12 }}>Другие услуги клиента (не определяют score клиента, но влияют на их деньги)</h3>
              )}
              {d.reasons.filter((r) => r.serviceId !== worst?.id).map((r, i) => <ReasonItem key={`o${i}`} r={r} />)}
            </>
          )}
        </section>

        <section className="card" aria-labelledby="money-title">
          <h2 id="money-title">Деньги · горизонт {horizon} дн.</h2>
          <div style={{ marginTop: 8 }}>
            <MoneyLine op="" label={`Плановые продления D (${d.services.reduce((n, s) => n + s.renewalsInHorizon, 0)} оплат)`} value={eur(d.money.plannedRenewalsMinor)} />
            <MoneyLine op="−" label="Покрыто основным балансом" value={eur(d.funding.balanceUsedMinor)} />
            <MoneyLine op="−" label={`Покрыто бонусами${d.funding.bonusEligible ? '' : ' (bonusState ≠ unlocked → 0)'}`} value={eur(d.funding.bonusUsedMinor)} />
            <MoneyLine op="=" label="Не покрыто балансом U" value={eur(d.money.unfundedMinor)} total />
            <MoneyLine op="×" label="Score услуги / 100 — по каждой услуге" value="см. таблицу" />
            <MoneyLine op="=" label="Выручка под риском G" value={eur(d.money.atRiskMinor)} total testId="customer-at-risk" />
          </div>
          <p className="caption muted" style={{ marginTop: 8 }}>
            {d.moneyFormula}
            {d.funding.firstUnfundedDue && ` Средств хватит до ${dateTime(d.funding.firstUnfundedDue)}.`}
            {d.funding.withdrawExcludedMinor > 0 && ` Withdraw-баланс ${eur(d.funding.withdrawExcludedMinor)} клиент может перенести сам — автоматически не учитывается.`}
          </p>
        </section>
      </div>

      <div className="grid-2-wide">
        <SourceTimeline id={c.id} />
        {d.churn && <ChurnTypesCard d={d} churn={d.churn} />}
      </div>

      <BehaviorCard id={c.id} />

      {d.mlShadow && <MlShadowCard shadow={d.mlShadow} services={d.services} />}

      <section className="card card-flush" aria-labelledby="svc-title">
        <h2 id="svc-title" style={{ padding: '16px 20px 8px' }}>Услуги</h2>
        <div className="table-wrap">
          <table className="data">
            <thead>
              <tr>
                <th>Услуга</th><th>Статус</th><th>Период</th><th className="r">Цена</th><th>Срок (expiresAt)</th><th>Автопродление</th>
                <th className="r">Оплат в горизонте</th><th className="r">D</th><th className="r">Не покрыто</th><th className="r">Score</th><th className="r">Под риском</th><th />
              </tr>
            </thead>
            <tbody>
              {d.services.map((s) => <ServiceRow key={s.id} s={s} />)}
            </tbody>
          </table>
        </div>
        {d.unscoredPlannedMinor > 0 && <p className="caption muted" style={{ padding: '0 20px 12px' }}>Неоценённые услуги: продления {eur(d.unscoredPlannedMinor)} не входят в «под риском».</p>}
      </section>

      <section className="card card-flush" aria-labelledby="tx-title">
        <h2 id="tx-title" style={{ padding: '16px 20px 8px' }}>Транзакции (последние {d.transactions.length})</h2>
        <div className="table-wrap">
          <table className="data">
            <thead><tr><th>ID</th><th>Услуга</th><th>Тип</th><th>Статус</th><th className="r">Сумма</th><th>Создана</th><th>Проведена</th></tr></thead>
            <tbody>
              {d.transactions.map((t) => (
                <tr key={t.id}>
                  <td className="num">{t.id}</td><td>#{t.serviceId}</td><td>{t.type === 'prolong' ? 'продление' : t.type === 'order' ? 'заказ' : t.type ?? '—'}</td>
                  <td>{t.status === 'not_performed' ? <Tag tone="warning">не проведена</Tag> : t.status === 'performed' ? 'проведена' : t.status ?? '—'}</td>
                  <td className="r">{eur(t.amount)}</td><td>{dateTime(t.createdAt)}</td><td>{dateTime(t.performedAt)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <p className="caption muted" style={{ padding: '0 20px 12px' }}>Суммы транзакций в наборе организаторов не связаны с ценой услуги и не используются в денежных KPI. Расчёт №{generationId}.</p>
      </section>
    </>
  );
}

function ReasonItem({ r }: { r: CustomerDetail['reasons'][number] }) {
  return (
    <div className="reason" data-testid="reason">
      <div className="reason-top">
        <strong>{r.title}</strong>
        <span className="reason-points">+{score(r.contribution)}</span>
      </div>
      <div className="track" role="img" aria-label={`Вклад ${score(r.contribution)} из 100`}>
        <div className="bar" style={{ width: `${Math.min(100, r.contribution)}%` }} />
      </div>
      <span className="caption secondary">{r.text} · вес {score(r.weight)} × сила {score(r.value)}</span>
    </div>
  );
}

function MoneyLine({ op, label, value, total, testId }: { op: string; label: string; value: string; total?: boolean; testId?: string }) {
  return (
    <div className={`money-line${total ? ' total' : ''}`}>
      <span className="money-op">{op}</span><span>{label}</span><span className="num" data-testid={testId}>{value}</span>
    </div>
  );
}

function ServiceRow({ s }: { s: CustomerDetail['services'][number] }) {
  const [open, setOpen] = useState(false);
  return (
    <Fragment>
      <tr>
        <td>{s.name} <span className="caption muted">#{s.id}</span></td>
        <td>{s.status ? statusLabel[s.status] ?? s.status : '—'}</td>
        <td>{s.paymentTerm ? termLabel[s.paymentTerm] ?? s.paymentTerm : '—'}</td>
        <td className="r">{eur(s.price)}</td>
        <td>{dateTime(s.expiresAt)}</td>
        <td>{s.autoProlong === null ? '—' : s.autoProlong ? 'вкл.' : 'выкл.'}</td>
        <td className="r">{s.renewable ? s.renewalsInHorizon : '—'}</td>
        <td className="r">{s.renewable ? eur(s.money.plannedRenewalsMinor) : '—'}</td>
        <td className="r">{s.renewable ? eur(s.money.unfundedMinor) : '—'}</td>
        <td className="r">{s.renewable ? (s.scored ? score(s.score) : <Tag tone="warning">не оценена</Tag>) : <span className="muted">не продлевается</span>}</td>
        <td className="r">{s.scored ? eur(s.money.atRiskMinor) : '—'}</td>
        <td>{s.renewable && <button className="btn btn-ghost btn-sm" aria-expanded={open} onClick={() => setOpen(!open)}>{open ? 'Скрыть' : 'Признаки'}</button>}</td>
      </tr>
      {open && (
        <tr>
          <td colSpan={12} style={{ background: 'var(--color-bg-subtle)' }}>
            <table className="data">
              <thead><tr><th>Признак</th><th className="r">Сила</th><th className="r">Вес</th><th className="r">Вклад</th><th>Объяснение</th></tr></thead>
              <tbody>
                {s.features.map((f) => (
                  <tr key={f.key}>
                    <td>{f.title}</td>
                    <td className="r">{f.value === null ? <Tag tone="warning">нет данных: {f.missingInputs.join(', ')}</Tag> : score(f.value)}</td>
                    <td className="r">{score(f.weight)}</td><td className="r">{score(f.contribution)}</td><td className="small secondary">{f.text}</td>
                  </tr>
                ))}
              </tbody>
            </table>
            {s.flags.length > 0 && <p className="caption" style={{ marginTop: 8 }}>Флаги: {s.flags.map((f) => flagLabel[f] ?? f).join(', ')}</p>}
          </td>
        </tr>
      )}
    </Fragment>
  );
}

function paymentTone(state: ChurnDetail['paymentTrend']['state']) {
  return state === 'decline' ? 'decline' : state === 'growth' ? 'growth' : state === 'stable' ? 'low' : 'unknown';
}

/** Три типа оттока одной строкой чипов */
function ChurnChips({ d, churn }: { d: CustomerDetail; churn: ChurnDetail }) {
  const lvl = d.riskLevel;
  return (
    <div className="row-wrap" style={{ gap: 6 }} data-testid="churn-chips">
      <ChurnChip tone={lvl === 'unknown' ? 'unknown' : lvl}>Непродление {d.riskScore === null ? 'нет оценки' : `${score(d.riskScore)} · ${levelLabel[lvl]}`}</ChurnChip>
      <FullChurnChip fc={churn.fullChurn} prefix="Уход целиком" />
      <ChurnChip tone={paymentTone(churn.paymentTrend.state)}>Платежи {paymentShort(churn.paymentTrend.state, churn.paymentTrend.changePct)}</ChurnChip>
    </div>
  );
}

function ChurnTypesCard({ d, churn }: { d: CustomerDetail; churn: ChurnDetail }) {
  return (
    <section className="card stack" aria-labelledby="types-title" data-testid="churn-types-card">
      <h2 id="types-title">Типы оттока</h2>
      <div className="stack" style={{ gap: 4 }}>
        <ChurnChip tone={d.riskLevel === 'unknown' ? 'unknown' : d.riskLevel}>Непродление {d.riskScore === null ? 'нет оценки' : `${score(d.riskScore)} · ${levelLabel[d.riskLevel]}`}</ChurnChip>
        <span className="small secondary">{d.primaryReason.text}. Индекс, не вероятность.</span>
      </div>
      <div className="stack" style={{ gap: 4 }}>
        <FullChurnChip fc={churn.fullChurn} prefix="Уход целиком" />
        <span className="small secondary">{churn.fullChurn.text}</span>
      </div>
      <div className="stack" style={{ gap: 4 }}>
        <ChurnChip tone={paymentTone(churn.paymentTrend.state)}>Платежи {paymentShort(churn.paymentTrend.state, churn.paymentTrend.changePct)}</ChurnChip>
        <span className="small secondary">{churn.paymentTrend.text}</span>
      </div>
    </section>
  );
}

/** Журнал источника по клиенту: что и когда изменилось (время получения); состояние записей не показывается */
function SourceTimeline({ id }: { id: number }) {
  const { data } = useCustomerEvents(id);
  return (
    <section className="card stack" aria-labelledby="tl-title" data-testid="source-timeline">
      <div className="card-head" style={{ marginBottom: 0 }}>
        <h2 id="tl-title">Журнал источника</h2>
        <span className="caption muted">
          {data?.coverage.streamStartedAt ? `поток с ${dateTime(data.coverage.streamStartedAt)} · ${data.items.length} событ.` : 'поток не подключён'}
        </span>
      </div>
      {!data || data.items.length === 0 ? (
        <p className="small secondary">Событий по клиенту нет — сведения из тестовых данных.</p>
      ) : (
        <ul className="timeline">
          {data.items.map((e) => (
            <li key={e.seq} data-testid="timeline-item">
              <span className={`tl-dot tl-${eventTone(e)}`} aria-hidden="true" />
              <div className="stack" style={{ gap: 2 }}>
                <span className="caption muted">{localDateTime(e.receivedAt)}</span>
                <span className="small">{kindLabel[e.kind]} #{e.entityId}: {describeEvent(e)}</span>
                <span className="caption secondary">{e.origin}{e.synthetic ? ' · симулятор' : ''} · {outcomeLabel[e.outcome]}</span>
              </div>
            </li>
          ))}
        </ul>
      )}
      <p className="caption muted">Время — момент получения события. Email и полные состояния записей здесь не показываются — только изменения.</p>
    </section>
  );
}
