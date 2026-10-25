import type { ReactNode } from 'react';
import { Link } from 'react-router';
import { levelGlyph, levelLabel } from '../format';
import type { RiskLevel } from '../api/types';
import type { LiveStatus } from '../live/LiveContext';

/** Уровень риска: цвет дублируется формой и текстом. */
export function RiskBadge({ level }: { level: RiskLevel }) {
  return (
    <span className={`badge badge-${level}`} data-level={level}>
      <span aria-hidden="true">{levelGlyph[level]}</span>
      {levelLabel[level]}
    </span>
  );
}

export function Tag({ tone = 'neutral', children, title }: { tone?: 'neutral' | 'info' | 'warning' | 'danger'; children: ReactNode; title?: string }) {
  return (
    <span className={`tag tag-${tone}`} title={title}>
      {children}
    </span>
  );
}

const bannerIcon = { info: 'ℹ', warning: '⚠', danger: '✕' } as const;
export function Banner({ tone, children, role }: { tone: 'info' | 'warning' | 'danger'; children: ReactNode; role?: 'status' | 'alert' }) {
  return (
    <div className={`banner banner-${tone}`} role={role ?? (tone === 'danger' ? 'alert' : 'status')}>
      <span className="banner-icon" aria-hidden="true">
        {bannerIcon[tone]}
      </span>
      <div>{children}</div>
    </div>
  );
}

export function KpiCard({ label, value, caption, primary, to, testId }: { label: string; value: string; caption: string; primary?: boolean; to?: string; testId?: string }) {
  const body = (
    <>
      <span className="kpi-label">{label}</span>
      <span className="kpi-value" data-testid={testId}>
        {value}
      </span>
      <span className="kpi-caption">{caption}</span>
    </>
  );
  const cls = `card kpi${primary ? ' kpi-primary' : ''}`;
  return to ? (
    <Link className={cls} to={to}>
      {body}
    </Link>
  ) : (
    <div className={cls}>{body}</div>
  );
}

const liveText: Record<LiveStatus, [string, string]> = {
  live: ['●', 'Live'],
  connecting: ['◌', 'Подключение…'],
  reconnecting: ['◌', 'Переподключение…'],
  offline: ['○', 'Офлайн · опрос 15 с'],
};
export function LivePill({ status }: { status: LiveStatus }) {
  const [g, t] = liveText[status];
  const cls = status === 'live' ? 'live-live' : status === 'offline' ? 'live-offline' : 'live-reconnecting';
  return (
    <span className={`live ${cls}`} data-testid="live-status" data-status={status} title="Статус live-обновлений">
      <span aria-hidden="true">{g}</span>
      {t}
    </span>
  );
}

export function Skeleton({ lines = 4 }: { lines?: number }) {
  return (
    <div className="stack" aria-busy="true" aria-label="Загрузка">
      {Array.from({ length: lines }, (_, i) => (
        <div key={i} className="skeleton" style={{ width: `${90 - i * 10}%` }} />
      ))}
    </div>
  );
}

export function ErrorBox({ error }: { error: unknown }) {
  const e = error as { status?: number; message?: string; code?: string };
  if (e?.status === 503 && e.code === 'no_generation')
    return (
      <Banner tone="info">
        Данные ещё не рассчитаны: загрузите тестовые данные (раздел «Источник») или дождитесь событий источника и первого пересчёта.
      </Banner>
    );
  return <Banner tone="danger">Не удалось загрузить данные: {e?.message ?? 'ошибка сети'}. Повторим автоматически.</Banner>;
}

/** Чип индикатора оттока. Тон дублируется глифом и текстом; неизвестное — серый пунктир, не ноль. */
export type ChurnTone = 'high' | 'medium' | 'low' | 'churned' | 'decline' | 'growth' | 'unknown';
const churnGlyph: Record<ChurnTone, string> = { high: '▲', medium: '●', low: '▼', churned: '×', decline: '▼', growth: '▲', unknown: '?' };
export function ChurnChip({ tone, children, title, testId }: { tone: ChurnTone; children: ReactNode; title?: string; testId?: string }) {
  return (
    <span className={`churn churn-${tone}`} title={title} data-testid={testId} data-tone={tone}>
      <span aria-hidden="true">{churnGlyph[tone]}</span>
      {children}
    </span>
  );
}

/** Индекс «уход клиента целиком» */
export function FullChurnChip({ fc, prefix, testId }: { fc: { score: number | null; level: string; state: string }; prefix?: string; testId?: string }) {
  const pre = prefix ? `${prefix} ` : '';
  if (fc.state === 'churned') return <ChurnChip tone="churned" testId={testId} title="Все услуги удалены — кандидат на возврат">{pre}Ушёл</ChurnChip>;
  if (fc.state === 'blocked') return <ChurnChip tone="unknown" testId={testId} title="Остались только заблокированные услуги">{pre}заблокирован</ChurnChip>;
  if (fc.score === null) return <ChurnChip tone="unknown" testId={testId} title="Индекс неизвестен">{pre}нет данных</ChurnChip>;
  const lvl = fc.level as 'high' | 'medium' | 'low';
  const v = Number.isInteger(fc.score) ? String(fc.score) : (Math.round(fc.score * 10) / 10).toFixed(1).replace('.', ',');
  const label = { high: 'Высокий', medium: 'Средний', low: 'Низкий' }[lvl] ?? '';
  return (
    <ChurnChip tone={fc.state === 'partial' ? 'unknown' : lvl} testId={testId} title={fc.state === 'partial' ? 'Оценены не все услуги — индекс не выше указанного' : undefined}>
      {pre}{fc.state === 'partial' ? '≤ ' : ''}{v} · {label}
    </ChurnChip>
  );
}
