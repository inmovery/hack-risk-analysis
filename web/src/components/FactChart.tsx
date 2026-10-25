import type { HistoryDay } from '../api/types';
import { eur } from '../format';

const series = [
  { key: 'renewed', label: 'продлено', color: 'var(--color-status-success-fg)' },
  { key: 'toProlongWait', label: '→ ожидание оплаты', color: 'var(--color-status-warning-fg)' },
  { key: 'deleted', label: 'удалено', color: 'var(--color-status-danger-fg)' },
] as const;

/** Легенда факта: сплошные цвета (факт), в отличие от штрихованного прогноза */
export function FactLegend() {
  return (
    <span className="legend">
      {series.map((s) => (
        <span key={s.key} className="row" style={{ gap: 4, alignItems: 'center' }}>
          <span className="swatch" style={{ background: s.color }} /> {s.label}
        </span>
      ))}
    </span>
  );
}

/** Факт из журнала событий по дням: сплошная заливка, дни до подключения потока не рисуются */
export function FactChart({ days }: { days: HistoryDay[] }) {
  if (days.length === 0) return null;
  const W = 760, H = 200, pad = 28, base = H - 30;
  const total = (d: HistoryDay) => d.renewed + d.toProlongWait + d.deleted;
  const max = Math.max(...days.map(total), 1);
  const slot = (W - pad * 2) / days.length;
  const bw = Math.max(6, Math.min(36, slot - 10));
  const label = (iso: string) => `${iso.slice(8, 10)}.${iso.slice(5, 7)}`;
  const every = Math.ceil(days.length / 10);
  return (
    <figure style={{ margin: 0 }} data-testid="fact-chart">
      <svg className="chart" viewBox={`0 0 ${W} ${H}`} role="img" aria-label="Факт по дням получения событий: продления, переходы в ожидание оплаты, удаления">
        <line x1={pad} x2={W - pad} y1={base} y2={base} stroke="var(--color-border-default)" />
        {days.map((d, i) => {
          const x = pad + i * slot + (slot - bw) / 2;
          let y = base;
          return (
            <g key={d.date}>
              {series.map((s) => {
                const v = d[s.key];
                if (!v) return null;
                const h = Math.max(2, Math.round(((base - 10) * v) / max));
                y -= h;
                return (
                  <rect key={s.key} x={x} y={y} width={bw} height={h} fill={s.color}>
                    <title>{`${label(d.date)}: ${s.label} — ${v}`}</title>
                  </rect>
                );
              })}
              {i % every === 0 && (
                <text x={x} y={H - 12} fontSize="11" fill="var(--color-text-muted)">{label(d.date)}</text>
              )}
            </g>
          );
        })}
      </svg>
      <details>
        <summary className="caption secondary">Таблица значений</summary>
        <table className="data">
          <thead>
            <tr><th>День</th><th className="r">Продлено</th><th className="r">→ ожидание</th><th className="r">Удалено</th><th className="r">Ушли целиком</th><th className="r">Платежи</th><th className="r">Пополнения</th></tr>
          </thead>
          <tbody>
            {days.map((d) => (
              <tr key={d.date}>
                <td>{label(d.date)}</td><td className="r">{d.renewed}</td><td className="r">{d.toProlongWait}</td><td className="r">{d.deleted}</td>
                <td className="r">{d.customersLostAll}</td><td className="r">{eur(d.paymentsMinor)} · {d.payments}</td><td className="r">{eur(d.topUpsMinor)} · {d.topUps}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </details>
    </figure>
  );
}
