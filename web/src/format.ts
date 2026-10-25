// Форматирование для UI. Деньги приходят целыми еврокопейками — форматируем без плавающей арифметики.

/** Неразрывный пробел: сумма не переносится между «€» и цифрами и внутри разрядов. */
export const NBSP = '\u00a0';
const group = (s: string) => s.replace(/\B(?=(\d{3})+(?!\d))/g, NBSP);

export function eur(minor: number | null | undefined): string {
  if (minor === null || minor === undefined) return '—';
  const neg = minor < 0;
  const abs = Math.abs(minor);
  const whole = Math.trunc(abs / 100);
  const cents = abs % 100;
  return `${neg ? '−' : ''}€${NBSP}${group(String(whole))},${String(cents).padStart(2, '0')}`;
}

export function score(v: number | null | undefined): string {
  if (v === null || v === undefined) return '—';
  return Number.isInteger(v) ? String(v) : (Math.round(v * 10) / 10).toFixed(1).replace('.', ',');
}

const dtf = new Intl.DateTimeFormat('ru-RU', { day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit', timeZone: 'UTC' });
const df = new Intl.DateTimeFormat('ru-RU', { day: '2-digit', month: '2-digit', year: 'numeric', timeZone: 'UTC' });
const tf = new Intl.DateTimeFormat('ru-RU', { hour: '2-digit', minute: '2-digit', second: '2-digit' });

export const dateTime = (iso: string | null | undefined) => (iso ? `${dtf.format(new Date(iso))} UTC` : '—');
export const date = (iso: string | null | undefined) => (iso ? df.format(new Date(iso)) : '—');
export const localTime = (iso: string | null | undefined) => (iso ? tf.format(new Date(iso)) : '—');

export function age(seconds: number): string {
  if (seconds < 3600) return `${Math.max(1, Math.round(seconds / 60))} мин`;
  if (seconds < 172800) return `${Math.round(seconds / 3600)} ч`;
  return `${Math.round(seconds / 86400)} дн.`;
}

export const levelLabel: Record<string, string> = { high: 'Высокий', medium: 'Средний', low: 'Низкий', unknown: 'Нет оценки' };
export const levelGlyph: Record<string, string> = { high: '▲', medium: '●', low: '▼', unknown: '?' };
export const stateLabel: Record<string, string> = {
  complete: 'Оценка полная', partial: 'Частичная оценка', no_renewable_services: 'Нет продлеваемых услуг', insufficient_data: 'Недостаточно данных',
};
export const flagLabel: Record<string, string> = {
  PROLONG_WAIT_BEYOND_GRACE: 'prolong_wait дольше 5 дней',
  EXPIRY_PASSED_STATUS_NOT_UPDATED: 'срок прошёл, статус не обновлён',
  BONUS_STATE_UNKNOWN: 'bonusState неизвестен',
  FUNDING_INCOMPLETE: 'покрытие неполное',
  UNKNOWN_STATUS: 'неизвестный статус',
  UNKNOWN_PAYMENT_TERM: 'неизвестный период',
  MISSING_PRICE: 'нет цены',
  MISSING_EXPIRES_AT: 'нет срока',
  MISSING_AUTOPROLONG: 'нет autoProlong',
  MISSING_BALANCE: 'нет баланса',
  NEGATIVE_BALANCE: 'отрицательный баланс',
  BEHAVIOR_DATA_MISSING: 'нет данных поведения (признак с весом не учтён)',
};
export const statusLabel: Record<string, string> = {
  active: 'active · активна', suspended: 'suspended · выключена', prolong_wait: 'prolong_wait · ждёт оплаты', deleted: 'deleted · удалена', blocked: 'blocked · заблокирована',
};
export const termLabel: Record<string, string> = { hour: 'час', week: 'неделя', month: 'месяц', quarter_year: 'квартал', half_year: 'полгода', year: 'год' };

const ldtf = new Intl.DateTimeFormat('ru-RU', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit' });
/** Время получения события в часовом поясе пользователя: «24.10, 15:04:58» */
export const localDateTime = (iso: string | null | undefined) => (iso ? ldtf.format(new Date(iso)) : '—');
