import { describe, expect, it } from 'vitest';
import { describeEvent, eventTone, paymentShort } from './journal';
import type { JournalItem } from './api/types';

const base: JournalItem = {
  seq: 1, receivedAt: '2026-10-24T12:00:00Z', occurredAt: '2026-10-24T11:59:58Z', eventType: null, kind: 'service', entityId: 5, ownerId: 5, outcome: 'applied', staleReason: null,
  changes: [], effect: {}, origin: 'push', synthetic: true,
};

describe('журнал источника', () => {
  it('описывает продление и смену статуса', () => {
    const e = { ...base, changes: ['status_changed', 'renewed'], effect: { statusFrom: 'prolong_wait', statusTo: 'active', expiresFrom: '2026-10-24T10:00:00Z', expiresTo: '2026-11-24T10:00:00Z' } };
    expect(describeEvent(e)).toBe('prolong_wait → active · срок 24.10.2026 → 24.11.2026');
    expect(eventTone(e)).toBe('success');
  });

  it('объясняет отброшенное устаревшее событие', () => {
    expect(describeEvent({ ...base, outcome: 'stale', staleReason: 'expires_regressed' })).toBe('отброшено: срок не может уменьшиться');
  });

  it('показывает пополнение и уход клиента целиком без email', () => {
    expect(describeEvent({ ...base, kind: 'account', changes: ['balance_changed', 'topped_up'], effect: { balanceFrom: 1478, balanceTo: 3478, toppedUpMinor: 2000 } }))
      .toBe('пополнение +€ 20,00 · баланс € 14,78 → € 34,78');
    const lost = { ...base, changes: ['status_changed', 'deleted', 'customer_lost_all_services'], effect: { statusFrom: 'prolong_wait', statusTo: 'deleted' } };
    expect(describeEvent(lost)).toContain('клиент ушёл целиком');
    expect(eventTone(lost)).toBe('danger');
  });

  it('тренд платежей: неизвестное не выдаётся за ноль', () => {
    expect(paymentShort('decline', -45)).toBe('▼ −45 %');
    expect(paymentShort('stable', 0)).toBe('— 0 %');
    expect(paymentShort('stable', -12.5)).toBe('≈ −12,5 %');
    expect(paymentShort('growth', null)).toBe('▲ новые');
    expect(paymentShort('insufficient_history', null)).toBe('? мало истории');
  });
});
