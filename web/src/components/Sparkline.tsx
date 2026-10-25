/** Мини-график по дням: линия (null — нет данных, разрыв) или столбики. Подпись для экранных дикторов — в label. */
export function Sparkline({ values, kind = 'line', label, testId, height = 40 }: {
  values: (number | null)[]; kind?: 'line' | 'bars'; label: string; testId?: string; height?: number;
}) {
  const w = 240;
  const h = height;
  const max = Math.max(1, ...values.map((v) => v ?? 0));
  const step = values.length > 1 ? w / (values.length - 1) : w;
  const y = (v: number) => h - 2 - (v / max) * (h - 4);
  let body: React.ReactNode;
  if (kind === 'bars') {
    const bw = Math.max(1, w / values.length - 1);
    body = values.map((v, i) => (v ? <rect key={i} x={i * (w / values.length)} y={y(v)} width={bw} height={h - 2 - y(v)} fill="var(--color-chart-planned)" /> : null));
  } else {
    // Отрезки между соседними известными точками: пропуск телеметрии — разрыв, а не ноль
    const segments: string[] = [];
    let cur: string[] = [];
    values.forEach((v, i) => {
      if (v === null) {
        if (cur.length > 1) segments.push(cur.join(' '));
        cur = [];
      } else cur.push(`${(i * step).toFixed(1)},${y(v).toFixed(1)}`);
    });
    if (cur.length > 1) segments.push(cur.join(' '));
    body = segments.map((pts, i) => <polyline key={i} points={pts} fill="none" stroke="var(--color-accent-default)" strokeWidth={1.5} />);
  }
  return (
    <svg viewBox={`0 0 ${w} ${h}`} width="100%" height={h} preserveAspectRatio="none" role="img" aria-label={label} data-testid={testId}>
      <line x1={0} y1={h - 1} x2={w} y2={h - 1} stroke="var(--color-border-default)" strokeWidth={1} />
      {body}
    </svg>
  );
}
