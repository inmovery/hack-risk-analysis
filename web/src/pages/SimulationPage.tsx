import { useEffect, useRef, useState } from 'react';
import { Link } from 'react-router';
import { useQueryClient } from '@tanstack/react-query';
import { useSimulation } from '../api/queries';
import { api, ApiError } from '../api/client';
import { useAuth } from '../auth/AuthContext';
import { Topbar } from '../layout/AppShell';
import { Banner, ErrorBox, Skeleton, Tag } from '../components/ui';
import { Button, Checkbox, ChoiceCards } from '../components/controls';
import { EventComposer } from '../components/EventComposer';
import { dateTime } from '../format';
import { phaseLabel, sameWorld, sendProgress, validateSettings } from '../simulation';
import type { SimulationSettings, SimulationView, WorldSettings, WorldSource } from '../api/types';

const num = (v: number) => v.toLocaleString('ru-RU');

function worldText(w: WorldSettings) {
  return w.source === 'organizer'
    ? `тестовые данные организаторов + ${num(w.extraCustomers)} клиентов, seed ${w.seed}`
    : `сгенерированный snapshot системы: ${num(w.customers)} клиентов, история ${w.historyDays} дн., seed ${w.seed}`;
}

export function SimulationPage() {
  const { user } = useAuth();
  const isAdmin = user?.role === 'admin';
  const qc = useQueryClient();
  const sim = useSimulation();
  const view = sim.data;
  const [draft, setDraft] = useState<SimulationSettings | null>(null);
  const [errors, setErrors] = useState<Record<string, string[]>>({});
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const dialog = useRef<HTMLDialogElement>(null);
  const dirty = !!view && !!draft && JSON.stringify(draft) !== JSON.stringify(view.settings);
  const dirtyRef = useRef(false);
  dirtyRef.current = dirty;

  useEffect(() => {
    // Черновик — из сохранённых настроек; незавершённые правки опрос не затирает
    if (view && (draft === null || !dirtyRef.current)) setDraft(structuredClone(view.settings));
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [view?.revision]);

  if (sim.isLoading || !view || !draft)
    return (<><Topbar title="Симуляция" /><main id="main" className="content">{sim.error ? <ErrorBox error={sim.error} /> : <Skeleton lines={8} />}</main></>);

  const status = view.agent.status;
  const progress = sendProgress(status);
  const set = (fn: (d: SimulationSettings) => void) => setDraft((d) => { const n = structuredClone(d!); fn(n); return n; });
  const err = (k: string) => errors[k]?.join(' ');
  const worldDraftChanged = !sameWorld(draft.world, view.world.settings);

  async function call(path: string, method: 'PUT' | 'POST', body?: unknown): Promise<SimulationView | null> {
    try {
      const r = await api<SimulationView>(path, { method, body });
      qc.setQueryData(['source', 'simulation'], r);
      return r;
    } catch (e) {
      if (e instanceof ApiError && e.status === 422) setErrors(e.fieldErrors);
      else setErrors({ _: [(e as Error).message] });
      return null;
    }
  }

  async function save(settings: SimulationSettings) {
    const local = validateSettings(settings);
    if (Object.keys(local).length) { setErrors(local); return null; }
    setErrors({});
    const r = await call('/api/v1/simulation/settings', 'PUT', settings);
    if (r) setDraft(structuredClone(r.settings));
    return r;
  }

  async function run(action: () => Promise<unknown>) {
    setBusy(true); setNotice(null);
    try { await action(); } finally { setBusy(false); }
  }

  const startScenario = () => run(async () => {
    if (await call('/api/v1/simulation/scenario', 'POST')) setNotice('Сценарий запрошен: симулятор начнёт цепочку для одного клиента.');
  });
  const reset = () => run(async () => {
    dialog.current?.close();
    if (dirty && !(await save(draft))) return;
    if (await call('/api/v1/simulation/reset', 'POST')) setNotice('Snapshot системы пересоздаётся: данные анализатора очищены, симулятор отправит их заново.');
  });

  return (
    <>
      <Topbar title="Симуляция" />
      <main id="main" className="content">
        {!view.eventsOnly && <Banner tone="info">Анализатор работает на загруженных тестовых данных — симуляция выключена.</Banner>}
        {!isAdmin && <Banner tone="info">Менять настройки симуляции может администратор. Вы видите текущее состояние.</Banner>}
        {errors._ && <Banner tone="danger">{errors._.join(' ')}</Banner>}
        {notice && <Banner tone="info">{notice}</Banner>}

        <div className="grid-2">
          <div className="stack-16">
            <section className="card stack" aria-labelledby="sim-status-title" data-testid="sim-status" data-online={view.agent.online}>
              <div className="row" style={{ justifyContent: 'space-between', alignItems: 'baseline' }}>
                <h2 id="sim-status-title">Симулятор</h2>
                <span className="row" style={{ gap: 6 }}>
                  {view.eventsOnly && <Tag tone="success" title="Симуляция не ставится на паузу: события идут каждый такт">● работает постоянно</Tag>}
                  <Tag tone={view.agent.online ? 'info' : 'warning'} title={view.agent.reportedAt ? `Последний отчёт: ${dateTime(view.agent.reportedAt)}` : 'Отчётов ещё не было'}>
                    {view.agent.online ? 'Симулятор на связи' : 'Нет связи'}
                  </Tag>
                </span>
              </div>
              {status ? (
                <>
                  <div className="row" style={{ gap: 8, alignItems: 'baseline' }}>
                    <span className="small secondary">Состояние:</span>
                    <strong data-testid="sim-phase" data-phase={status.phase}>{phaseLabel[status.phase] ?? status.phase}</strong>
                    {view.agent.reportedAt && <span className="caption secondary">· отчёт {dateTime(view.agent.reportedAt)}</span>}
                  </div>
                  <dl className="kv-grid">
                    <dt>Snapshot системы</dt><dd className="num">{status.runId ?? '—'} · ревизия {status.worldRevision}</dd>
                    <dt>Часы snapshot системы</dt><dd>{dateTime(status.simClock)}</dd>
                    <dt>Клиентов</dt><dd className="num">{num(status.customers)}</dd>
                    <dt>Услуг (действующих)</dt><dd className="num">{num(status.services)} ({num(status.liveServices)})</dd>
                    <dt>Событий сгенерировано</dt><dd className="num">{num(status.eventsGenerated)}</dd>
                    <dt>Отправлено / в очереди</dt><dd className="num">{num(status.eventsSent)} / {num(status.backlog)}</dd>
                    <dt>Последняя отправка</dt><dd>{dateTime(status.lastSentAt)}</dd>
                  </dl>
                  {progress !== null && (
                    <div className="stack" style={{ gap: 4 }}>
                      <span className="caption secondary">{phaseLabel[status.phase]}: {Math.round(progress * 100)}%</span>
                      <div className="track" role="progressbar" aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(progress * 100)} aria-label="Отправка событий">
                        <div className="bar" style={{ width: `${progress * 100}%`, background: 'var(--color-accent-default)' }} />
                      </div>
                    </div>
                  )}
                  {status.lastError && <Banner tone="danger">Ошибка симулятора: {status.lastError}</Banner>}
                  {status.lastNarration && <p className="caption secondary" data-testid="sim-narration">Сценарий: {status.lastNarration}</p>}
                </>
              ) : (
                <p className="small secondary">Симулятор ещё не присылал отчётов. Он забирает настройки у анализатора и сообщает своё состояние сам.</p>
              )}
              <Link to="/source" className="small">Журнал событий →</Link>
            </section>

            <EventComposer isAdmin={isAdmin} eventsOnly={view.eventsOnly} online={view.agent.online} />

            <section className="card stack" aria-labelledby="sim-control-title">
              <h2 id="sim-control-title">Управление</h2>
              <div className="toolbar">
                <Button variant="secondary" size="sm" disabled={!isAdmin || busy || !view.eventsOnly} onClick={() => void startScenario()} testId="sim-scenario">
                  Запустить сценарий
                </Button>
                <span className="caption muted">цепочка событий одного клиента по шагам</span>
              </div>
              <p className="caption muted">Симуляция работает постоянно: события идут каждый такт. После перерыва связи симулятор догоняет время — анализатор оценивает по текущим часам.</p>
              <div className="form-grid">
                <NumField label="Такт, с" value={draft.tickSeconds} hint="1–60: как часто отправлять события" error={err('tickSeconds')} disabled={!isAdmin}
                  onChange={(v) => set((d) => { d.tickSeconds = v; })} testId="sim-tick" />
                <NumField label="Шаг сценария, с" value={draft.scenarioStepSeconds} hint="5–300: пауза между шагами цепочки" error={err('scenarioStepSeconds')} disabled={!isAdmin}
                  onChange={(v) => set((d) => { d.scenarioStepSeconds = v; })} testId="sim-step" />
                <div className="field">
                  <span className="field-label">Доставка</span>
                  <Checkbox checked={draft.chaos} disabled={!isAdmin} onChange={(v) => set((d) => { d.chaos = v; })} testId="sim-chaos"
                    label="Сбои доставки" description="перестановки и повторы — проверяет устойчивость приёма" />
                </div>
              </div>
            </section>

            <section className="card stack" aria-labelledby="sim-world-title" data-testid="sim-world">
              <h2 id="sim-world-title">Snapshot системы</h2>
              {view.pending.worldChanged && <Banner tone="warning">Параметры snapshot системы изменены: применятся после пересоздания.</Banner>}
              <ChoiceCards<WorldSource> ariaLabel="Источник snapshot системы" value={draft.world.source} disabled={!isAdmin}
                onChange={(v) => set((d) => { d.world.source = v; })}
                options={[
                  { value: 'organizer', title: 'Тестовые данные + N клиентов', testId: 'sim-source-organizer', body: 'Клиенты из тестовых данных организаторов и N дополнительных.' },
                  { value: 'generated', title: 'Сгенерированный с историей', testId: 'sim-source-generated', body: 'Заданное число клиентов и история событий до текущего момента.' },
                ]} />
              <div className="form-grid">
                {draft.world.source === 'organizer' ? (
                  <NumField label="Дополнительных клиентов (N)" value={draft.world.extraCustomers} hint="0–2000" error={err('world.extraCustomers')} disabled={!isAdmin}
                    onChange={(v) => set((d) => { d.world.extraCustomers = v; })} testId="sim-extra" />
                ) : (
                  <>
                    <NumField label="Клиентов" value={draft.world.customers} hint="10–3000" error={err('world.customers')} disabled={!isAdmin}
                      onChange={(v) => set((d) => { d.world.customers = v; })} testId="sim-customers" />
                    <NumField label="История, дней" value={draft.world.historyDays} hint="0–365: события до текущего момента" error={err('world.historyDays')} disabled={!isAdmin}
                      onChange={(v) => set((d) => { d.world.historyDays = v; })} testId="sim-history" />
                  </>
                )}
                <NumField label="Seed" value={draft.world.seed} hint="один seed — одна и та же история" error={err('world.seed')} disabled={!isAdmin}
                  onChange={(v) => set((d) => { d.world.seed = v; })} testId="sim-seed" />
              </div>
              {worldDraftChanged && !view.pending.worldChanged && <p className="caption secondary">Новые параметры snapshot системы применятся после пересоздания.</p>}
              {isAdmin && (
                <div className="toolbar">
                  <Button variant="primary" disabled={!dirty} loading={busy} onClick={() => void run(async () => { await save(draft); })} testId="sim-save">Сохранить</Button>
                  <Button variant="ghost" disabled={!dirty || busy} onClick={() => { setDraft(structuredClone(view.settings)); setErrors({}); }}>Отменить изменения</Button>
                  <Button variant="danger" disabled={busy || !view.eventsOnly} onClick={() => dialog.current?.showModal()} testId="sim-reset">Пересоздать snapshot системы</Button>
                  {dirty && <Tag tone="warning">Черновик не сохранён</Tag>}
                </div>
              )}
            </section>
          </div>

          <aside className="stack-16">
            <section className="card stack" aria-labelledby="sim-current-title">
              <h2 id="sim-current-title">Текущий snapshot системы</h2>
              <dl className="kv-grid">
                <dt>Ревизия</dt><dd className="num">{view.world.revision}</dd>
                <dt>Создан</dt><dd>{dateTime(view.world.startedAt)}</dd>
                <dt>Параметры</dt><dd>{worldText(view.world.settings)}</dd>
                {view.world.resetAt && (<><dt>Пересоздан</dt><dd>{dateTime(view.world.resetAt)}{view.world.resetBy ? ` · ${view.world.resetBy}` : ''}</dd></>)}
              </dl>
            </section>
            <section className="card stack" aria-labelledby="sim-settings-title">
              <h2 id="sim-settings-title">Настройки</h2>
              <dl className="kv-grid">
                <dt>Ревизия</dt><dd className="num">{view.revision}</dd>
                <dt>Изменены</dt><dd>{dateTime(view.updatedAt)}{view.updatedBy ? ` · ${view.updatedBy}` : ''}</dd>
                <dt>Сценариев запрошено</dt><dd className="num">{view.scenarioRequest}</dd>
              </dl>
            </section>
          </aside>
        </div>

        <dialog ref={dialog} className="dialog" aria-labelledby="reset-title">
          <div className="dialog-body">
            <h2 id="reset-title">Пересоздать snapshot системы?</h2>
            <p className="secondary">Все события, расчёты и исходы анализатора будут удалены, симулятор создаст snapshot системы заново по этим параметрам.</p>
            <p className="small">{worldText(draft.world)}</p>
            <div className="toolbar">
              <Button variant="danger" loading={busy} onClick={() => void reset()} testId="sim-reset-confirm">Пересоздать</Button>
              <Button variant="ghost" onClick={() => dialog.current?.close()}>Отмена</Button>
            </div>
          </div>
        </dialog>
      </main>
    </>
  );
}

function NumField({ label, value, hint, error, disabled, onChange, testId }: { label: string; value: number; hint: string; error?: string | undefined; disabled?: boolean; onChange: (v: number) => void; testId?: string }) {
  return (
    <label className="field">
      <span className="field-label">{label}</span>
      <input className="input num" type="number" value={value} disabled={disabled} aria-invalid={!!error} data-testid={testId}
        onChange={(e) => onChange(e.target.value === '' ? 0 : Number(e.target.value))} />
      {error ? <span className="field-error">{error}</span> : <span className="field-hint">{hint}</span>}
    </label>
  );
}
