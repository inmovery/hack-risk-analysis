import type { SimulationAgentStatus, SimulationPhase, SimulationSettings } from './api/types';

export const phaseLabel: Record<SimulationPhase, string> = {
  idle: 'ожидает',
  bootstrap: 'отправляет начальные данные',
  history: 'отправляет историю',
  live: 'живёт в реальном времени',
  paused: 'на паузе',
  error: 'ошибка',
};

/** Границы полей — как на сервере; сервер всё равно проверяет и отвечает 422 */
export const limits = {
  tickSeconds: [1, 60],
  scenarioStepSeconds: [5, 300],
  'world.seed': [1, 2147483647],
  'world.extraCustomers': [0, 2000],
  'world.customers': [10, 3000],
  'world.historyDays': [0, 365],
} as const;

type Path = keyof typeof limits;

function valueOf(s: SimulationSettings, path: Path): number {
  switch (path) {
    case 'tickSeconds': return s.tickSeconds;
    case 'scenarioStepSeconds': return s.scenarioStepSeconds;
    case 'world.seed': return s.world.seed;
    case 'world.extraCustomers': return s.world.extraCustomers;
    case 'world.customers': return s.world.customers;
    case 'world.historyDays': return s.world.historyDays;
  }
}

/** Ошибки формы по путям полей (как errors в ответе 422) */
export function validateSettings(s: SimulationSettings): Record<string, string[]> {
  const errors: Record<string, string[]> = {};
  for (const path of Object.keys(limits) as Path[]) {
    const [min, max] = limits[path];
    const v = valueOf(s, path);
    if (!Number.isInteger(v) || v < min || v > max) errors[path] = [`Целое от ${min} до ${max}`];
  }
  return errors;
}

/** Доля отправленного при начальной загрузке и истории; null — полосу не показывать */
export function sendProgress(status: SimulationAgentStatus | null): number | null {
  if (!status || (status.phase !== 'bootstrap' && status.phase !== 'history')) return null;
  const total = status.eventsSent + status.backlog;
  return total > 0 ? Math.min(1, status.eventsSent / total) : 0;
}

export function sameWorld(a: SimulationSettings['world'], b: SimulationSettings['world']): boolean {
  return a.source === b.source && a.seed === b.seed && a.extraCustomers === b.extraCustomers && a.customers === b.customers && a.historyDays === b.historyDays;
}
