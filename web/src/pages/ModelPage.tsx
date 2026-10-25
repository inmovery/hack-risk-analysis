import { Fragment, useEffect, useMemo, useRef, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { useMlStatus, useRevisions, useSettings } from '../api/queries';
import { MlCard } from '../components/MlCard';
import { Button, ChoiceCards } from '../components/controls';
import { api, ApiError } from '../api/client';
import { useAuth } from '../auth/AuthContext';
import { Topbar } from '../layout/AppShell';
import { Banner, ErrorBox, RiskBadge, Skeleton, Tag } from '../components/ui';
import { dateTime, eur, score } from '../format';
import type { ModelSettings, PreviewResponse, RiskLevel, ScoringMode } from '../api/types';

function diffPatch(base: ModelSettings, draft: ModelSettings) {
  const patch: Record<string, unknown> = {};
  const weights: Record<string, number> = {};
  for (const [k, v] of Object.entries(draft.weights)) if (base.weights[k] !== v) weights[k] = v;
  if (Object.keys(weights).length) patch.weights = weights;
  if (draft.horizonDays !== base.horizonDays) patch.horizonDays = draft.horizonDays;
  if (draft.urgencyWindowDays !== base.urgencyWindowDays) patch.urgencyWindowDays = draft.urgencyWindowDays;
  if (draft.thresholds.medium !== base.thresholds.medium || draft.thresholds.high !== base.thresholds.high) patch.thresholds = draft.thresholds;
  if (draft.clock.mode !== base.clock.mode || draft.clock.tickMinutes !== base.clock.tickMinutes) patch.clock = draft.clock;
  const bc = base.churn, dc = draft.churn;
  if (dc && bc && (dc.nonRenewalDays !== bc.nonRenewalDays || dc.paymentWindowDays !== bc.paymentWindowDays || dc.paymentDeclinePct !== bc.paymentDeclinePct))
    patch.churn = dc;
  return patch;
}

/**
 * Модель риска. По умолчанию основной балл — ML; редактор правил (веса, окно срочности, предпросмотр) появляется только в режиме «Правила».
 * Пороги уровней, горизонт денег, время оценки и определения оттока действуют в обоих режимах и редактируются всегда.
 */
export function ModelPage() {
  const { user } = useAuth();
  const isAdmin = user?.role === 'admin';
  const qc = useQueryClient();
  const settings = useSettings();
  const revisions = useRevisions();
  const ml = useMlStatus();
  const [draft, setDraft] = useState<ModelSettings | null>(null);
  const [baseRevision, setBaseRevision] = useState<number | null>(null);
  const [comment, setComment] = useState('');
  const [errors, setErrors] = useState<Record<string, string[]>>({});
  const [saved, setSaved] = useState<number | null>(null);
  const [busy, setBusy] = useState<'save' | 'preview' | null>(null);
  const [preview, setPreview] = useState<PreviewResponse | null>(null);
  const [conflict, setConflict] = useState<number | null>(null);
  const [askMode, setAskMode] = useState<ScoringMode | null>(null);
  const [modeBusy, setModeBusy] = useState(false);
  const [modeError, setModeError] = useState<string | null>(null);
  const dialog = useRef<HTMLDialogElement>(null);
  const modeDialog = useRef<HTMLDialogElement>(null);
  const latest = settings.data?.latest;

  useEffect(() => {
    // Черновик берётся из последней ревизии; не затираем незавершённые правки пользователя.
    if (latest && (draft === null || (baseRevision !== latest.revision && !dirtyRef.current))) {
      setDraft(structuredClone(latest.settings));
      setBaseRevision(latest.revision);
    }
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [latest?.revision]);

  const patch = useMemo(() => (latest && draft ? diffPatch(latest.settings, draft) : {}), [latest, draft]);
  const dirty = Object.keys(patch).length > 0;
  const dirtyRef = useRef(false);
  dirtyRef.current = dirty;
  useEffect(() => { if (conflict !== null) dialog.current?.showModal(); }, [conflict]);
  useEffect(() => { if (askMode !== null) modeDialog.current?.showModal(); }, [askMode]);
  useEffect(() => setPreview(null), [patch]);

  if (settings.isLoading || ml.isLoading || !draft || !latest)
    return (<><Topbar title="Модель риска" /><main id="main" className="content">{settings.error ? <ErrorBox error={settings.error} /> : <Skeleton lines={8} />}</main></>);

  const active = settings.data!.active;
  const recalculating = saved !== null && (active?.revision ?? 0) < saved;
  const set = (fn: (d: ModelSettings) => void) => setDraft((d) => { const n = structuredClone(d!); fn(n); return n; });
  const primary: ScoringMode = ml.data?.scoring?.primary ?? 'ml';
  const used = ml.data?.scoring?.generation;
  const hasModel = !!ml.data?.shadow;
  const rulesOn = primary === 'rules';

  async function save() {
    setBusy('save'); setErrors({});
    try {
      const r = await api<{ modelRevision: number; jobId: number | null; status: string }>('/api/v1/model/settings', { method: 'PATCH', body: { baseRevision, ...patch, comment: comment || undefined } });
      setSaved(r.modelRevision); setBaseRevision(r.modelRevision); setComment('');
      await qc.invalidateQueries({ queryKey: ['settings'] });
    } catch (e) {
      if (e instanceof ApiError && e.status === 422) setErrors(e.fieldErrors);
      else if (e instanceof ApiError && e.status === 409) setConflict((e.body?.latestRevision as number | undefined) ?? null);
      else setErrors({ _: [(e as Error).message] });
    } finally { setBusy(null); }
  }
  async function runPreview() {
    setBusy('preview'); setErrors({});
    try { setPreview(await api<PreviewResponse>('/api/v1/model/preview', { method: 'POST', body: { baseRevision, ...patch } })); }
    catch (e) { if (e instanceof ApiError && e.status === 422) setErrors(e.fieldErrors); else setErrors({ _: [(e as Error).message] }); }
    finally { setBusy(null); }
  }
  async function switchMode(mode: ScoringMode) {
    setModeBusy(true); setModeError(null);
    try {
      await api('/api/v1/ml/scoring', { method: 'PUT', body: { primary: mode } });
      await qc.invalidateQueries({ queryKey: ['source', 'ml'] });
      modeDialog.current?.close();
    } catch (e) {
      setModeError(e instanceof ApiError ? `${e.message}${e.code ? ` (${e.code})` : ''}` : (e as Error).message);
    } finally { setModeBusy(false); }
  }
  function reloadLatest() {
    dialog.current?.close(); setConflict(null);
    setDraft(structuredClone(latest!.settings)); setBaseRevision(latest!.revision);
    void qc.invalidateQueries({ queryKey: ['settings'] });
  }
  const err = (k: string) => errors[k]?.join(' ');

  const weightsCard = (
    <section className="card" aria-labelledby="w-title" data-testid="rules-editor">
      <div className="card-head">
        <h2 id="w-title">Правила: признаки и веса</h2>
        <span className="caption muted">score услуги = min(100, Σ вес × сила признака 0…1)</span>
      </div>
      {settings.data!.catalog.map((f, i, all) => {
        const w = draft.weights[f.key] ?? 0;
        const was = latest.settings.weights[f.key] ?? 0;
        const header = f.behavioral && !all[i - 1]?.behavioral && (
          <div className="stack" style={{ gap: 2, margin: '12px 0 4px' }} key={`${f.key}-h`} data-testid="behavior-weights">
            <h3 className="small">Поведение клиента (события v2)</h3>
            <span className="caption muted">
              По умолчанию вес 0: признаки считаются и видны в карточке, но балл не меняют. Если у клиента нет поведенческих данных,
              признак с весом не снимает оценку — клиент помечается «нет данных поведения».
            </span>
          </div>
        );
        return (
          <Fragment key={f.key}>
            {header}
            <div className="weight-row">
              <div className="stack" style={{ gap: 2 }}>
                <label htmlFor={`w-${f.key}`}><strong>{f.title}</strong></label>
                <span className="caption muted">{f.description}</span>
              </div>
              <input type="range" min={0} max={100} step={1} value={w} disabled={!isAdmin} aria-label={`${f.title}: вес`}
                onChange={(e) => set((d) => { d.weights[f.key] = Number(e.target.value); })} />
              <input id={`w-${f.key}`} className="input num-input num" type="number" min={0} max={100} step={1} value={w} disabled={!isAdmin}
                aria-invalid={!!err(`weights.${f.key}`)} data-testid={`weight-${f.key}`}
                onChange={(e) => set((d) => { d.weights[f.key] = e.target.value === '' ? 0 : Number(e.target.value); })} />
              <span className="caption muted">{w === was ? `в ревизии r${latest.revision}` : `было ${score(was)} · ${w > was ? '+' : ''}${score(w - was)}`}</span>
            </div>
          </Fragment>
        );
      })}
      <div className="form-grid" style={{ marginTop: 16 }}>
        <NumField label="Окно срочности, дней" value={draft.urgencyWindowDays} hint="1–30, ≤ горизонта" error={err('urgencyWindowDays')} disabled={!isAdmin}
          onChange={(v) => set((d) => { d.urgencyWindowDays = v; })} testId="window" />
      </div>
    </section>
  );

  return (
    <>
      <Topbar title="Модель риска" />
      <main id="main" className="content">
        {!isAdmin && <Banner tone="info">Менять режим, пороги и веса может администратор модели. Вы видите действующую ревизию r{latest.revision}.</Banner>}
        {recalculating && <Banner tone="info">Ревизия r{saved} сохранена · пересчёт… Таблица и KPI пока показывают ревизию r{active?.revision} — обновятся автоматически.</Banner>}
        {saved !== null && !recalculating && <Banner tone="info">Ревизия r{saved} в расчёте (поколение №{active?.generationId}).</Banner>}
        {settings.data!.pending && saved === null && <Banner tone="info">Идёт пересчёт…</Banner>}
        {errors._ && <Banner tone="danger">{errors._.join(' ')}</Banner>}
        {Object.keys(errors).filter((k) => k !== '_').length > 0 && <Banner tone="danger">Настройки не прошли проверку — действующая ревизия не изменена. {Object.values(errors).flat().join(' ')}</Banner>}

        <section className="card stack" aria-labelledby="mode-title" data-testid="scoring" data-primary={primary} data-used={used?.used ?? ''}>
          <div className="card-head" style={{ marginBottom: 0 }}>
            <h2 id="mode-title">Чем считается основной балл</h2>
            {used && used.requested !== primary && <Tag tone="info">⟳ Пересчёт в новом режиме…</Tag>}
          </div>
          <ChoiceCards<ScoringMode> ariaLabel="Чем считается основной балл" value={primary} disabled={!isAdmin}
            onChange={(m) => { setModeError(null); setAskMode(m); }}
            options={[
              {
                value: 'ml', title: 'ML-модель', testId: 'scoring-ml', badge: <Tag tone="info">по умолчанию</Tag>,
                body: hasModel
                  ? 'Score = вероятность непродления × 100. Причины — вклад признаков в прогноз. Модель обучена на журнале событий и проверена по годам.'
                  : 'Модель не загружена: пока считают правила. Загрузите пакет модели в карточке ниже.',
              },
              {
                value: 'rules', title: 'Правила', testId: 'scoring-rules',
                body: 'Score = Σ вес × сила признака. Веса и окно срочности настраиваются вручную, предпросмотр показывает эффект до сохранения.',
              },
            ]} />
          {primary === 'ml' && used?.fallback && (
            <Banner tone="warning" testId="ml-fallback">
              Выбрана ML-модель, но текущий расчёт посчитан правилами: {used.fallback === 'ml_failed' ? 'модель не выполнилась' : 'модель не загружена'}.
            </Banner>
          )}
        </section>

        <div className="grid-2">
          <div className="stack-16">
            {rulesOn ? weightsCard : (
              <div className="rules-off" data-testid="rules-off">
                <div className="stack" style={{ gap: 4 }}>
                  <strong>Правила выключены</strong>
                  <span className="small secondary">Веса признаков и окно срочности не влияют на балл. Чтобы настраивать коэффициенты вручную, включите правила.</span>
                </div>
                {isAdmin && <Button variant="secondary" onClick={() => { setModeError(null); setAskMode('rules'); }} testId="enable-rules">Включить правила</Button>}
              </div>
            )}

            <section className="card stack" aria-labelledby="t-title" data-testid="common-settings">
              <div className="card-head" style={{ marginBottom: 0 }}>
                <h2 id="t-title">Уровни, деньги и время оценки</h2>
                <span className="caption muted">действуют в любом режиме</span>
              </div>
              <div className="form-grid">
                <NumField label="Порог «Средний», от" value={draft.thresholds.medium} hint="0 < средний < высокий" error={err('thresholds') ?? err('thresholds.medium')} disabled={!isAdmin}
                  onChange={(v) => set((d) => { d.thresholds.medium = v; })} testId="threshold-medium" />
                <NumField label="Порог «Высокий», от" value={draft.thresholds.high} hint="≤ 100; по неокруглённому score" error={err('thresholds.high')} disabled={!isAdmin}
                  onChange={(v) => set((d) => { d.thresholds.high = v; })} testId="threshold-high" />
                <NumField label="Горизонт, дней" value={draft.horizonDays} hint="1–90: деньги и прогноз" error={err('horizonDays')} disabled={!isAdmin}
                  onChange={(v) => set((d) => { d.horizonDays = v; })} testId="horizon" />
                <NumField label="Пересчёт по времени, мин" value={draft.clock.tickMinutes} hint="1–60, только в live" error={err('clock.tickMinutes')} disabled={!isAdmin || draft.clock.mode !== 'live'}
                  onChange={(v) => set((d) => { d.clock.tickMinutes = v; })} testId="tick" />
              </div>
              <div className="toolbar" role="group" aria-label="Время оценки">
                <span className="small secondary">Время оценки:</span>
                <button className="chip" aria-pressed={draft.clock.mode === 'live'} disabled={!isAdmin} onClick={() => set((d) => { d.clock.mode = 'live'; })}>Live: текущее время</button>
                <button className="chip" aria-pressed={draft.clock.mode === 'source'} disabled={!isAdmin} onClick={() => set((d) => { d.clock.mode = 'source'; })}>На момент выгрузки данных</button>
              </div>
              <p className="caption muted">
                {rulesOn ? 'Score от 0 до 100 — индекс правил, не вероятность.' : 'Score = вероятность непродления по модели × 100.'} Уровень — по порогам; деньги — продления в горизонте.
                Срочность растёт со временем без новых данных: в live фоновый пересчёт идёт каждые N минут.
              </p>
            </section>

            {draft.churn && (
              <section className="card stack" aria-labelledby="churn-title" data-testid="churn-settings">
                <h2 id="churn-title">Определения оттока</h2>
                <p className="caption secondary">Отток — три явления: непродление, уход клиента целиком, снижение платежей. Эти настройки не меняют score — они задают индикаторы и факты.</p>
                <div className="form-grid">
                  <NumField label="Непродление: дней после срока" value={draft.churn.nonRenewalDays} hint={`1–${settings.data!.limits.maxNonRenewalDays}; «не продлено» — только при свежем источнике`}
                    error={err('churn.nonRenewalDays')} disabled={!isAdmin} onChange={(v) => set((d) => { d.churn!.nonRenewalDays = v; })} testId="churn-n" />
                  <NumField label="Окно платежей, дней" value={draft.churn.paymentWindowDays} hint={`${settings.data!.limits.minPaymentWindowDays}–${settings.data!.limits.maxPaymentWindowDays}; последние W против предыдущих W`}
                    error={err('churn.paymentWindowDays')} disabled={!isAdmin} onChange={(v) => set((d) => { d.churn!.paymentWindowDays = v; })} testId="churn-window" />
                  <NumField label="Снижение платежей, от %" value={draft.churn.paymentDeclinePct} hint="1–100; клиент моложе 2W — «мало истории»"
                    error={err('churn.paymentDeclinePct')} disabled={!isAdmin} onChange={(v) => set((d) => { d.churn!.paymentDeclinePct = v; })} testId="churn-pct" />
                  <label className="field">
                    <span className="field-label">Уход целиком</span>
                    <input className="input" value="min score услуг" disabled readOnly />
                    <span className="field-hint">минимум score действующих услуг (без настройки)</span>
                  </label>
                </div>
              </section>
            )}

            {isAdmin && (
              <div className="toolbar">
                <input className="input" placeholder="Комментарий к ревизии (необязательно)" aria-label="Комментарий к ревизии" value={comment} onChange={(e) => setComment(e.target.value)}
                  style={{ minWidth: 280 }} maxLength={500} />
                <Button variant="primary" disabled={!dirty || busy === 'preview'} loading={busy === 'save'} onClick={() => void save()} testId="save-model">Сохранить и пересчитать</Button>
                {rulesOn && (
                  <Button variant="secondary" disabled={!dirty || busy === 'save'} loading={busy === 'preview'} onClick={() => void runPreview()} testId="preview-model">Предпросмотр</Button>
                )}
                <Button variant="ghost" disabled={!dirty || busy !== null} onClick={() => setDraft(structuredClone(latest.settings))}>Отменить изменения</Button>
                {dirty && <Tag tone="warning">Черновик не сохранён</Tag>}
              </div>
            )}

            {ml.data && <MlCard ml={ml.data} isAdmin={isAdmin} primary={primary} />}
          </div>

          <aside className="stack-16">
            {preview && rulesOn && (
              <section className="card stack" aria-labelledby="pv-title" data-testid="preview">
                <h2 id="pv-title">Предпросмотр черновика (не сохранён)</h2>
                {(['high', 'medium', 'low', 'unknown'] as RiskLevel[]).map((l) => (
                  <div key={l} className="row" style={{ justifyContent: 'space-between' }}><RiskBadge level={l} /><span className="num">{preview.byLevelBefore[l] ?? 0} → {preview.byLevelAfter[l] ?? 0}</span></div>
                ))}
                <div className="row" style={{ justifyContent: 'space-between' }}><span className="small secondary">Выручка под риском</span><span className="num">{eur(preview.atRiskBefore)} → {eur(preview.atRiskAfter)}</span></div>
                <p className="caption muted">Сменят уровень: {preview.changedLevelCount}. Расчёт на текущих данных источника, без сохранения.</p>
                <ul className="small" style={{ margin: 0, paddingLeft: 16 }}>
                  {preview.changes.slice(0, 8).map((c) => <li key={c.customerId}>#{c.customerId}: {score(c.scoreBefore)} → {score(c.scoreAfter)}{c.levelBefore !== c.levelAfter ? ` (${c.levelBefore} → ${c.levelAfter})` : ''}</li>)}
                </ul>
              </section>
            )}
            <section className="card stack" aria-labelledby="rev-title">
              <h2 id="rev-title">Ревизии настроек</h2>
              <p className="caption secondary">Сохранена: r{latest.revision} · в расчёте: {active ? `r${active.revision} (поколение №${active.generationId})` : '—'}</p>
              {(revisions.data ?? []).slice(0, 6).map((r) => (
                <div key={r.revision} className="stack" style={{ gap: 2 }}>
                  <span className="small"><strong>r{r.revision}</strong> · {dateTime(r.createdAt)} · {r.createdBy}{r.comment ? ` · «${r.comment}»` : ''}</span>
                  {active?.revision === r.revision ? <Tag tone="info">в расчёте</Tag> : r.revision === latest.revision ? <Tag tone="warning">сохранена, пересчёт</Tag> : null}
                </div>
              ))}
            </section>
          </aside>
        </div>

        <dialog ref={modeDialog} className="dialog" aria-labelledby="mode-dialog-title" onClose={() => setAskMode(null)} data-testid="mode-dialog">
          <div className="dialog-body">
            <h2 id="mode-dialog-title">{askMode === 'rules' ? 'Включить правила?' : 'Вернуть ML-модель?'}</h2>
            <p className="secondary">
              {askMode === 'rules'
                ? 'Балл, уровни, сортировки, деньги и /metrics пересчитаются по весам правил. Появятся редактор весов и предпросмотр. Модель останется рядом для сравнения.'
                : hasModel
                  ? 'Балл станет вероятностью непродления по модели × 100. Веса правил перестанут влиять на балл, редактор правил скроется; сохранённые веса не потеряются.'
                  : 'Модель не загружена: до загрузки пакета балл продолжат считать правила.'}
            </p>
            {modeError && <Banner tone="danger" role="alert">{modeError}</Banner>}
            <div className="toolbar">
              <Button variant="primary" loading={modeBusy} disabled={askMode === 'ml' && !hasModel} onClick={() => askMode && void switchMode(askMode)} testId="mode-confirm">
                {askMode === 'rules' ? 'Включить правила' : 'Вернуть ML'}
              </Button>
              <Button variant="ghost" disabled={modeBusy} onClick={() => modeDialog.current?.close()}>Отмена</Button>
            </div>
          </div>
        </dialog>

        <dialog ref={dialog} className="dialog" aria-labelledby="conflict-title" onClose={() => setConflict(null)}>
          <div className="dialog-body">
            <h2 id="conflict-title">Настройки уже изменены</h2>
            <p className="secondary">Пока вы редактировали ревизию r{baseRevision}, сохранена ревизия r{conflict}. Ваши изменения не применены.</p>
            <div className="toolbar">
              <Button variant="primary" onClick={reloadLatest}>Загрузить актуальную и сравнить</Button>
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
