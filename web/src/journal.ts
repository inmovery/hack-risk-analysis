// Человекочитаемое описание событий журнала источника (коды изменений и значения «от → до» без PII).
import type { JournalItem, PaymentTrendState, FullChurnState } from './api/types';
import { date, eur } from './format';

export const outcomeLabel: Record<string, string> = { applied: 'применено', no_change: 'без изменений', stale: 'устаревшее' };
export const outcomeTone: Record<string, 'info' | 'neutral' | 'warning'> = { applied: 'info', no_change: 'neutral', stale: 'warning' };

export const staleReasonLabel: Record<string, string> = {
  deleted_is_terminal: 'удалённая услуга не восстанавливается',
  expires_regressed: 'срок не может уменьшиться',
  total_replenished_regressed: 'сумма пополнений не может уменьшиться',
  performed_is_terminal: 'проведённая транзакция не откатывается',
};

export const kindLabel: Record<string, string> = { account: 'Аккаунт', service: 'Услуга', transaction: 'Транзакция' };

const n = (v: unknown) => (typeof v === 'number' ? v : null);
const s = (v: unknown) => (typeof v === 'string' ? v : null);
const b = (v: unknown) => (typeof v === 'boolean' ? v : null);

/** Что изменилось — одной строкой; для stale — почему отброшено */
export function describeEvent(e: JournalItem): string {
  const x = e.effect;
  if (e.outcome === 'stale') return `отброшено: ${staleReasonLabel[e.staleReason ?? ''] ?? e.staleReason ?? 'устаревшее'}`;
  if (e.outcome === 'no_change') return 'то же состояние (повтор)';
  const parts: string[] = [];
  const c = new Set(e.changes);
  if (e.kind === 'service') {
    if (c.has('service_created')) parts.push(`создана · ${s(x.statusTo) ?? '—'}${s(x.termTo) ? ` · ${s(x.termTo)}` : ''}${n(x.priceTo) !== null ? ` · ${eur(n(x.priceTo))}` : ''}`);
    if (c.has('status_changed')) parts.push(`${s(x.statusFrom) ?? '—'} → ${s(x.statusTo) ?? '—'}`);
    if (c.has('renewed') || (x.expiresFrom !== undefined && x.expiresTo !== undefined && !c.has('service_created')))
      parts.push(`срок ${date(s(x.expiresFrom))} → ${date(s(x.expiresTo))}`);
    if (c.has('price_changed')) parts.push(`цена ${eur(n(x.priceFrom))} → ${eur(n(x.priceTo))}`);
    if (c.has('autoprolong_changed')) parts.push(`автопродление ${b(x.autoProlongTo) ? 'включено' : 'выключено'}`);
    if (c.has('term_changed')) parts.push(`период ${s(x.termFrom) ?? '—'} → ${s(x.termTo) ?? '—'}`);
    if (c.has('owner_unknown')) parts.push('аккаунт владельца неизвестен');
    if (c.has('customer_lost_all_services')) parts.push('клиент ушёл целиком');
  } else if (e.kind === 'account') {
    if (c.has('account_created')) parts.push('новый аккаунт');
    if (c.has('topped_up')) parts.push(`пополнение +${eur(n(x.toppedUpMinor))}`);
    if (c.has('balance_changed')) parts.push(`баланс ${eur(n(x.balanceFrom))} → ${eur(n(x.balanceTo))}`);
    if (c.has('bonus_state_changed')) parts.push(`bonusState ${s(x.bonusStateFrom) ?? '—'} → ${s(x.bonusStateTo) ?? '—'}`);
    if (c.has('bonus_changed')) parts.push(`бонусы ${eur(n(x.bonusFrom))} → ${eur(n(x.bonusTo))}`);
    if (c.has('withdraw_changed')) parts.push(`withdraw ${eur(n(x.withdrawFrom))} → ${eur(n(x.withdrawTo))}`);
    if (c.has('email_changed')) parts.push('email изменён');
  } else {
    const kind = s(x.type) === 'prolong' ? 'продление' : s(x.type) === 'order' ? 'заказ' : 'транзакция';
    if (c.has('payment_performed')) parts.push(`платёж ${eur(n(x.amount))} (${kind}) проведён`);
    else if (c.has('transaction_created')) parts.push(`${kind} ${eur(n(x.amount))}, ${s(x.status) === 'not_performed' ? 'не проведена' : s(x.status) ?? '—'}`);
    else if (c.has('transaction_updated')) parts.push(`${kind} обновлена: ${s(x.statusFrom) ?? '—'} → ${s(x.status) ?? '—'}`);
    if (c.has('service_unknown')) parts.push('услуга неизвестна');
  }
  return parts.join(' · ') || e.changes.join(', ') || 'изменение';
}

/** Тон точки на ленте карточки клиента */
export function eventTone(e: JournalItem): 'success' | 'warning' | 'danger' | 'neutral' {
  const c = new Set(e.changes);
  if (e.outcome === 'stale') return 'warning';
  if (c.has('deleted') || c.has('customer_lost_all_services')) return 'danger';
  if (c.has('to_prolong_wait') || c.has('blocked')) return 'warning';
  if (c.has('renewed') || c.has('payment_performed') || c.has('topped_up')) return 'success';
  return 'neutral';
}

export const fullChurnStateLabel: Record<FullChurnState, string> = {
  scored: 'оценён', partial: 'частично', churned: 'Ушёл', blocked: 'заблокирован', no_services: 'нет услуг', insufficient_data: 'нет данных',
};

export const paymentTrendLabel: Record<PaymentTrendState, string> = {
  decline: 'снижение', stable: 'стабильно', growth: 'рост', no_payments: 'нет платежей', insufficient_history: 'мало истории',
};

/** Короткий текст тренда платежей для таблицы: «▼ −45 %», «— стабильно», «мало истории» */
export function paymentShort(state: PaymentTrendState, pct: number | null): string {
  const p = pct === null ? '' : ` ${pct > 0 ? '+' : pct < 0 ? '−' : ''}${Math.abs(pct).toString().replace('.', ',')} %`;
  switch (state) {
    case 'decline': return `▼${p}`;
    case 'growth': return pct === null ? '▲ новые' : `▲${p}`;
    case 'stable': return pct ? `≈${p}` : '— 0 %';
    case 'no_payments': return 'нет платежей';
    default: return '? мало истории';
  }
}
