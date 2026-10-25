import { useRef, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { useSnapshots, useSummary } from '../api/queries';
import { api, ApiError } from '../api/client';
import { useAuth } from '../auth/AuthContext';
import { Topbar } from '../layout/AppShell';
import { Banner, ErrorBox, Skeleton, Tag } from '../components/ui';
import { StreamSection } from '../components/StreamSection';
import { age, dateTime, flagLabel } from '../format';
import type { ValidationReport } from '../api/types';

export function SourcePage() {
  const { user } = useAuth();
  const isAdmin = user?.role === 'admin';
  const qc = useQueryClient();
  const snapshots = useSnapshots();
  const summary = useSummary();
  const [file, setFile] = useState<File | null>(null);
  const [asOf, setAsOf] = useState('');
  const [report, setReport] = useState<ValidationReport | null>(null);
  const [result, setResult] = useState<{ tone: 'info' | 'warning' | 'danger'; text: string } | null>(null);
  const [busy, setBusy] = useState(false);
  const confirmRef = useRef<HTMLDialogElement>(null);
  const current = snapshots.data?.find((s) => s.active);
  // Загрузка доступна, когда список ревизий получен: без активной ревизии это первая загрузка (без If-Match)
  const canUpload = snapshots.isSuccess;

  const url = (base: string) => (asOf ? `${base}?sourceAsOf=${encodeURIComponent(new Date(asOf + 'Z').toISOString())}` : base);

  async function validate() {
    if (!file) return;
    setBusy(true); setResult(null);
    try { setReport(await api<ValidationReport>(url('/api/v1/source/validate'), { method: 'POST', rawBody: await file.text(), headers: { 'Content-Type': 'application/json' } })); }
    catch (e) { setResult({ tone: 'danger', text: (e as Error).message }); }
    finally { setBusy(false); }
  }

  async function replace() {
    if (!file || !canUpload) return;
    confirmRef.current?.close();
    setBusy(true); setResult(null);
    try {
      const headers: Record<string, string> = { 'Content-Type': 'application/json' };
      if (current) headers['If-Match'] = `"${current.revision}"`;
      const r = await api<{ sourceRevision: number; jobId: number | null; idempotent: boolean }>(url('/api/v1/source/snapshot'), {
        method: 'PUT', rawBody: await file.text(), headers,
      });
      setResult(r.idempotent
        ? { tone: 'info', text: `Эти тестовые данные уже загружены (ревизия r${r.sourceRevision}) — повтор ничего не изменил.` }
        : { tone: 'info', text: `Тестовые данные загружены: ревизия r${r.sourceRevision}, задание пересчёта #${r.jobId}. Результаты обновятся автоматически.` });
      setReport(null); setFile(null);
      await qc.invalidateQueries({ queryKey: ['source'] });
    } catch (e) {
      if (e instanceof ApiError && e.status === 422) {
        setReport({ valid: false, counts: { accounts: 0, services: 0, transactions: 0 }, errors: e.fieldErrors, errorCounts: (e.body?.errorCounts as Record<string, number>) ?? {}, warnings: {}, sourceAsOf: null, sourceAsOfAssumed: false });
        setResult({ tone: 'danger', text: 'Тестовые данные не прошли проверку — активный источник не изменён.' });
      } else if (e instanceof ApiError && (e.status === 409 || e.status === 412)) {
        setResult({ tone: 'warning', text: `${e.message}. Обновите страницу источника и повторите.` });
      } else setResult({ tone: 'danger', text: (e as Error).message });
    } finally { setBusy(false); }
  }

  return (
    <>
      <Topbar title="Источник и поток" />
      <main id="main" className="content">
        {snapshots.isLoading && <Skeleton lines={6} />}
        {snapshots.error && <ErrorBox error={snapshots.error} />}
        {result && <Banner tone={result.tone}>{result.text}</Banner>}
        <StreamSection isAdmin={isAdmin} />
        <div className="grid-2-wide">
          <section className="card stack" aria-labelledby="cur-title">
            <div className="row-wrap" style={{ alignItems: 'center', gap: 8 }}>
              <h2 id="cur-title">Тестовые данные{current ? ` · ревизия ${current.revision}` : ''}</h2>
              {current && <Tag tone={current.synthetic ? 'warning' : 'info'}>{current.synthetic ? 'Синтетика / демо-изменение' : JSON.parse(current.provenance).label?.startsWith('organizer') ? 'Тестовый набор организаторов' : 'Данные источника'}</Tag>}
              {summary.data?.generation.sourceKind === 'stream' && <Tag tone="info">База · поверх событий потока</Tag>}
              {summary.data?.generation.sourceKind === 'snapshot' && summary.data.generation.sourceAgeSeconds > 24 * 3600 && <Tag tone="warning">Устарел: {age(summary.data.generation.sourceAgeSeconds)}</Tag>}
            </div>
            {!current && !snapshots.isLoading && <p className="secondary">Тестовые данные ещё не загружены.</p>}
            {current && (
              <div className="row-wrap" style={{ gap: 24 }}>
                {[
                  ['Момент выгрузки (sourceAsOf)', `${dateTime(current.sourceAsOf)}${current.sourceAsOfAssumed ? ' (принят как время получения)' : ''}`],
                  ['Получен', `${dateTime(current.receivedAt)} · ${current.actor}`],
                  ['Аккаунты · услуги · транзакции', `${current.counts.accounts} · ${current.counts.services} · ${current.counts.transactions}`],
                  ['SHA-256 содержимого', `${current.contentHash.slice(0, 8)}…${current.contentHash.slice(-8)}`],
                  ['Адаптер', `${current.adapter} · schema ${current.schemaVersion}`],
                  ['Предупреждения', Object.entries(current.warnings).map(([k, v]) => `${k}: ${v}`).join(', ') || 'нет'],
                ].map(([k, v]) => <div key={k} className="kv" style={{ width: 220 }}><span className="kv-label">{k}</span><span className="small">{v}</span></div>)}
              </div>
            )}
            {summary.data && Object.keys(summary.data.qualityFlags).length > 0 && (
              <div className="row-wrap" style={{ gap: 6 }}>
                {Object.entries(summary.data.qualityFlags).map(([f, n]) => <Tag key={f} tone="warning">{flagLabel[f] ?? f}: {n}</Tag>)}
              </div>
            )}
          </section>

          <section className="card stack" aria-labelledby="up-title">
            <h2 id="up-title">Загрузка тестовых данных (полная замена)</h2>
            {!isAdmin ? (
              <p className="small secondary">Загружать тестовые данные может администратор модели (или по API с INGEST_TOKEN).</p>
            ) : (
              <>
                <label className="dropzone">
                  <strong>⇪ Выберите JSON-файл</strong>
                  <span className="caption muted" style={{ display: 'block' }}>data.json или envelope {'{ schemaVersion, sourceAsOf, accounts, services, transactions }'} · до 50 МБ</span>
                  <input type="file" accept="application/json,.json" data-testid="snapshot-file" style={{ marginTop: 8 }}
                    onChange={(e) => { setFile(e.target.files?.[0] ?? null); setReport(null); setResult(null); }} />
                </label>
                <label className="field">
                  <span className="field-label">sourceAsOf, UTC (если его нет в файле)</span>
                  <input className="input" type="datetime-local" value={asOf} onChange={(e) => setAsOf(e.target.value)} />
                  <span className="field-hint">Пусто и нет в файле → время получения, с пометкой «принят как время получения».</span>
                </label>
                {report && (
                  <div className={`banner banner-${report.valid ? 'info' : 'danger'}`} role="status" data-testid="validation-report">
                    <div className="stack" style={{ gap: 4 }}>
                      <strong>{report.valid ? 'Проверка пройдена' : 'Ошибки проверки'}</strong>
                      <span className="small">Аккаунтов {report.counts.accounts}, услуг {report.counts.services}, транзакций {report.counts.transactions}.</span>
                      {Object.entries(report.errors).slice(0, 10).map(([k, v]) => <span key={k} className="caption"><code>{k}</code>: {v.join(' ')}</span>)}
                      {Object.keys(report.warnings).length > 0 && <span className="caption">Предупреждения: {Object.entries(report.warnings).map(([k, v]) => `${k}: ${v}`).join(', ')}</span>}
                    </div>
                  </div>
                )}
                <p className="caption muted">Полная замена: всё, чего нет в новых тестовых данных, исчезнет из расчёта (это не событие удаления). Тот же контент повторно ничего не меняет. События, полученные после момента выгрузки тестовых данных, применятся поверх него.</p>
                <div className="toolbar">
                  <button className="btn btn-secondary" disabled={!file || busy} onClick={() => void validate()} data-testid="validate-snapshot">Проверить</button>
                  <button className="btn btn-danger" disabled={!file || busy || !canUpload} onClick={() => confirmRef.current?.showModal()} data-testid="replace-snapshot">
                    {current ? 'Заменить тестовые данные' : 'Загрузить тестовые данные'}
                  </button>
                </div>
              </>
            )}
          </section>
        </div>

        <section className="card card-flush" aria-labelledby="hist-title">
          <h2 id="hist-title" style={{ padding: '16px 20px 8px' }}>История загрузок тестовых данных</h2>
          <div className="table-wrap">
            <table className="data">
              <thead><tr><th>Ревизия</th><th>sourceAsOf</th><th>Получен</th><th>Записи</th><th>Кто</th><th>Пометка</th></tr></thead>
              <tbody>
                {(snapshots.data ?? []).map((s) => (
                  <tr key={s.revision}>
                    <td className="num">r{s.revision} {s.active && <Tag tone="info">активный</Tag>}</td>
                    <td>{dateTime(s.sourceAsOf)}</td><td>{dateTime(s.receivedAt)}</td>
                    <td className="num">{s.counts.accounts} · {s.counts.services} · {s.counts.transactions}</td>
                    <td>{s.actor}</td><td>{s.synthetic ? 'синтетика' : 'источник'}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <p className="caption muted" style={{ padding: '0 20px 12px' }}>{summary.data?.history.note}</p>
        </section>

        <dialog ref={confirmRef} className="dialog" aria-labelledby="confirm-title">
          <div className="dialog-body">
            <h2 id="confirm-title">{current ? 'Заменить тестовые данные?' : 'Загрузить тестовые данные?'}</h2>
            <p className="secondary">
              {current
                ? `Новый файл полностью заменит ревизию r${current.revision}. Будет создано новое поколение расчёта; прежние ревизии останутся в истории.`
                : 'Это первая загрузка: файл станет ревизией тестовых данных, по нему будет создано первое поколение расчёта.'}
            </p>
            <div className="toolbar">
              <button className="btn btn-danger" onClick={() => void replace()} data-testid="confirm-replace">{current ? 'Заменить' : 'Загрузить'}</button>
              <button className="btn btn-ghost" onClick={() => confirmRef.current?.close()}>Отмена</button>
            </div>
          </div>
        </dialog>
      </main>
    </>
  );
}
