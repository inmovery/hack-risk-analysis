import { describe, expect, it } from 'vitest';
import { eur, NBSP, score } from './format';
import { weekly } from './components/ForecastChart';

const n = (s: string) => s.replaceAll(' ', NBSP);

describe('eur: целые еврокопейки без плавающей арифметики', () => {
  it('форматирует копейки, тысячи и знак', () => {
    expect(eur(0)).toBe(n('€ 0,00'));
    expect(eur(5)).toBe(n('€ 0,05'));
    expect(eur(123456789)).toBe(n('€ 1 234 567,89'));
    expect(eur(-1500)).toBe(n('−€ 15,00'));
    expect(eur(null)).toBe('—');
  });
  it('не теряет точность на больших суммах (до 2^53)', () => {
    expect(eur(9007199254740991)).toBe(n('€ 90 071 992 547 409,91'));
  });
});

describe('score', () => {
  it('целые без дробной части, остальное — 1 знак', () => {
    expect(score(30)).toBe('30');
    expect(score(21.482639)).toBe('21,5');
    expect(score(null)).toBe('—');
  });
});

describe('weekly: корзины прогноза', () => {
  it('группирует дни по 7 от первого дня и суммирует', () => {
    const days = Array.from({ length: 9 }, (_, i) => ({ date: `2026-10-${String(19 + i).padStart(2, '0')}`, plannedMinor: 100, atRiskMinor: 10, renewals: 1 }));
    const w = weekly(days);
    expect(w).toHaveLength(2);
    expect(w[0]).toMatchObject({ planned: 700, atRisk: 70, renewals: 7, label: '19.10–25.10' });
    expect(w[1]).toMatchObject({ planned: 200, renewals: 2 });
  });
});
