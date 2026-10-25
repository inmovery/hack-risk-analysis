import { describe, expect, it } from 'vitest';
import { phaseLabel, sameWorld, sendProgress, validateSettings } from './simulation';
import type { SimulationAgentStatus, SimulationSettings } from './api/types';

const settings = (patch: Partial<SimulationSettings> = {}, world: Partial<SimulationSettings['world']> = {}): SimulationSettings => ({
  running: true, tickSeconds: 5, scenarioStepSeconds: 15, chaos: true,
  world: { source: 'organizer', seed: 42, extraCustomers: 50, customers: 300, historyDays: 90, ...world }, ...patch,
});

const status = (patch: Partial<SimulationAgentStatus>): SimulationAgentStatus => ({
  agentVersion: '1', worldRevision: 1, runId: 'ab12cd34', phase: 'live', simClock: null, customers: 0, services: 0, liveServices: 0,
  eventsGenerated: 0, eventsSent: 0, backlog: 0, lastSentAt: null, lastError: null, lastNarration: null, scenarioHandled: 0, ...patch,
});

describe('simulation', () => {
  it('у каждой фазы есть подпись по-русски', () => {
    expect(phaseLabel.bootstrap).toBe('отправляет начальные данные');
    expect(phaseLabel.history).toBe('отправляет историю');
    expect(Object.keys(phaseLabel)).toHaveLength(6);
  });

  it('допустимые настройки проходят проверку', () => {
    expect(validateSettings(settings())).toEqual({});
  });

  it('ошибки — по путям полей, как в ответе 422', () => {
    const e = validateSettings(settings({ tickSeconds: 0, scenarioStepSeconds: 400 }, { customers: 5, historyDays: 366, seed: 0, extraCustomers: 2001 }));
    expect(Object.keys(e).sort()).toEqual(['scenarioStepSeconds', 'tickSeconds', 'world.customers', 'world.extraCustomers', 'world.historyDays', 'world.seed']);
    expect(validateSettings(settings({ tickSeconds: 2.5 })).tickSeconds).toBeDefined();
  });

  it('полоса прогресса — только при начальной загрузке и истории', () => {
    expect(sendProgress(status({ phase: 'live', eventsSent: 10, backlog: 10 }))).toBeNull();
    expect(sendProgress(status({ phase: 'history', eventsSent: 30, backlog: 10 }))).toBe(0.75);
    expect(sendProgress(status({ phase: 'bootstrap', eventsSent: 0, backlog: 0 }))).toBe(0);
    expect(sendProgress(null)).toBeNull();
  });

  it('snapshot системы сравнивается по всем параметрам', () => {
    expect(sameWorld(settings().world, settings().world)).toBe(true);
    expect(sameWorld(settings().world, settings({}, { seed: 7 }).world)).toBe(false);
  });
});
