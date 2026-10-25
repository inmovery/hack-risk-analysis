import { eur } from '../format';

interface Point { date: string; plannedMinor: number; atRiskMinor: number; renewals: number; }
interface Bucket { label: string; from: string; planned: number; atRisk: number; renewals: number; }

/** Недельные корзины прогноза: [первый день; +7). Последняя корзина может быть неполной. */
export function weekly(points: Point[]): Bucket[] {
  if (points.length === 0) return [];
  const start = new Date(points[0]!.date + 'T00:00:00Z').getTime();
  const buckets: Bucket[] = [];
  for (const p of points) {
    const idx = Math.floor((new Date(p.date + 'T00:00:00Z').getTime() - start) / (7 * 86400000));
    while (buckets.length <= idx) {
      const from = new Date(start + buckets.length * 7 * 86400000);
      const to = new Date(from.getTime() + 6 * 86400000);
      const f = (d: Date) => `${String(d.getUTCDate()).padStart(2, '0')}.${String(d.getUTCMonth() + 1).padStart(2, '0')}`;
      buckets.push({ label: `${f(from)}–${f(to)}`, from: from.toISOString().slice(0, 10), planned: 0, atRisk: 0, renewals: 0 });
    }
    const b = buckets[idx]!;
    b.planned += p.plannedMinor;
    b.atRisk += p.atRiskMinor;
    b.renewals += p.renewals;
  }
  return buckets;
}

/** Прогноз (сценарий): штрихованные столбцы «план» и «под риском» — явно отличаются от факта. */
export function ForecastChart({ points }: { points: Point[] }) {
  const data = weekly(points);
  if (data.length === 0) return <p className="secondary">В горизонте нет плановых продлений.</p>;
  const W = 760, H = 200, pad = 28, base = H - 30;
  const max = Math.max(...data.map((d) => d.planned), 1);
  const slot = (W - pad * 2) / data.length;
  const bw = Math.min(48, (slot - 20) / 2);
  return (
    <figure style={{ margin: 0 }}>
      <svg className="chart" viewBox={`0 0 ${W} ${H}`} role="img" aria-label="Прогноз плановых продлений и выручки под риском по неделям">
        <defs>
          <pattern id="hatch-planned" width="6" height="6" patternUnits="userSpaceOnUse" patternTransform="rotate(45)">
            <rect width="6" height="6" fill="var(--color-chart-planned)" />
            <line x1="0" y1="0" x2="0" y2="6" stroke="var(--color-bg-surface)" strokeWidth="1.5" />
          </pattern>
          <pattern id="hatch-risk" width="6" height="6" patternUnits="userSpaceOnUse" patternTransform="rotate(45)">
            <rect width="6" height="6" fill="var(--color-chart-at-risk)" />
            <line x1="0" y1="0" x2="0" y2="6" stroke="var(--color-bg-surface)" strokeWidth="1.5" opacity="0.6" />
          </pattern>
        </defs>
        <line x1={pad} x2={W - pad} y1={base} y2={base} stroke="var(--color-border-default)" />
        {data.map((d, i) => {
          const x = pad + i * slot + (slot - bw * 2 - 4) / 2;
          const hp = Math.round(((base - 10) * d.planned) / max);
          const hr = Math.round(((base - 10) * d.atRisk) / max);
          return (
            <g key={d.from}>
              <rect x={x} y={base - hp} width={bw} height={hp} fill="url(#hatch-planned)" rx="3">
                <title>{`${d.label}: план ${eur(d.planned)} (${d.renewals} оплат)`}</title>
              </rect>
              <rect x={x + bw + 4} y={base - hr} width={bw} height={hr} fill="url(#hatch-risk)" rx="3">
                <title>{`${d.label}: под риском ${eur(d.atRisk)}`}</title>
              </rect>
              <text x={x} y={H - 12} fontSize="11" fill="var(--color-text-muted)">
                {d.label}
              </text>
            </g>
          );
        })}
      </svg>
      <details>
        <summary className="caption secondary">Таблица значений</summary>
        <table className="data">
          <thead>
            <tr><th>Неделя</th><th className="r">Оплат</th><th className="r">План (прогноз)</th><th className="r">Под риском (прогноз)</th></tr>
          </thead>
          <tbody>
            {data.map((d) => (
              <tr key={d.from}><td>{d.label}</td><td className="r">{d.renewals}</td><td className="r">{eur(d.planned)}</td><td className="r">{eur(d.atRisk)}</td></tr>
            ))}
          </tbody>
        </table>
      </details>
    </figure>
  );
}
