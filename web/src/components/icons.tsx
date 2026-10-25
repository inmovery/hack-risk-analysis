/** Иконки в стиле брендбука: сплошная заливка, скруглённые углы, сетка 24 × 24. */
type IconProps = { className?: string };

export function IconOverview({ className }: IconProps) {
  return (
    <svg className={className} viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
      <rect x="3" y="3" width="8" height="10" rx="2.5" />
      <rect x="13" y="3" width="8" height="6" rx="2.5" />
      <rect x="3" y="15" width="8" height="6" rx="2.5" />
      <rect x="13" y="11" width="8" height="10" rx="2.5" />
    </svg>
  );
}

export function IconCustomers({ className }: IconProps) {
  return (
    <svg className={className} viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
      <circle cx="9" cy="7.5" r="4" />
      <path d="M2 18.5C2 15.46 4.46 13 7.5 13h3c3.04 0 5.5 2.46 5.5 5.5v.5a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2v-.5Z" />
      <circle cx="17" cy="8.5" r="3" opacity=".55" />
      <path d="M17.5 13.5h.5c2.2 0 4 1.8 4 4v1.5a2 2 0 0 1-2 2h-2.2c.13-.38.2-.8.2-1.2v-.8c0-2.2-.95-4.17-2.47-5.5h1.97Z" opacity=".55" />
    </svg>
  );
}

export function IconModel({ className }: IconProps) {
  return (
    <svg className={className} viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
      <rect x="3" y="5" width="18" height="3" rx="1.5" opacity=".55" />
      <rect x="3" y="10.5" width="18" height="3" rx="1.5" opacity=".55" />
      <rect x="3" y="16" width="18" height="3" rx="1.5" opacity=".55" />
      <circle cx="8" cy="6.5" r="3" />
      <circle cx="16" cy="12" r="3" />
      <circle cx="10" cy="17.5" r="3" />
    </svg>
  );
}

export function IconSource({ className }: IconProps) {
  return (
    <svg className={className} viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
      <ellipse cx="12" cy="5.5" rx="8" ry="3" />
      <path d="M4 8.6c1.5 1.3 4.6 1.9 8 1.9s6.5-.6 8-1.9V12c0 1.66-3.58 3-8 3s-8-1.34-8-3V8.6Z" opacity=".75" />
      <path d="M4 15.1c1.5 1.3 4.6 1.9 8 1.9s6.5-.6 8-1.9v3.4c0 1.66-3.58 3-8 3s-8-1.34-8-3v-3.4Z" opacity=".55" />
    </svg>
  );
}

export function IconSimulation({ className }: IconProps) {
  return (
    <svg className={className} viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
      <path d="M7 5.3c0-1.53 1.66-2.5 3-1.73l10.2 5.9c1.33.77 1.33 2.7 0 3.46L10 18.83c-1.34.78-3-.18-3-1.73V5.3Z" />
      <rect x="2" y="7" width="3" height="10" rx="1.5" opacity=".55" />
    </svg>
  );
}

export function IconChevron({ className }: IconProps) {
  return (
    <svg className={className} viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d="M4 6l4 4 4-4" />
    </svg>
  );
}

export function IconCheck({ className }: IconProps) {
  return (
    <svg className={className} viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="2.4" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d="M3.5 8.5l3 3 6-7" />
    </svg>
  );
}

export function IconDash({ className }: IconProps) {
  return (
    <svg className={className} viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="2.4" strokeLinecap="round" aria-hidden="true">
      <path d="M4 8h8" />
    </svg>
  );
}

/** Фирменный паттерн: три параллельные линии и акцентная линия с закруглённым поворотом. */
export function BrandLines({ className, tone = 'dark' }: { className?: string; tone?: 'dark' | 'light' }) {
  const base = tone === 'dark' ? '#333333' : '#DEDEDE';
  return (
    <svg className={className} viewBox="0 0 400 240" preserveAspectRatio="xMidYMax slice" fill="none" aria-hidden="true">
      <g strokeWidth="10" strokeLinecap="round">
        <path d="M-20 140H150a40 40 0 0 0 40-40V-20" stroke={base} />
        <path d="M-20 170H170a60 60 0 0 0 60-60V-20" stroke={base} />
        <path d="M-20 200H190a80 80 0 0 0 80-80V-20" stroke={base} />
        <path d="M-20 230H210a100 100 0 0 0 100-100V-20" stroke="#4CC6F1" />
      </g>
    </svg>
  );
}
