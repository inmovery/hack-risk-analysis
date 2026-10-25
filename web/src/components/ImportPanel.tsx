import { useRef, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { API_BASE, apiUrl, ensureCsrf } from '../api/client';
import { useImport } from '../api/queries';
import { Banner } from './ui';
import { Button, Checkbox } from './controls';
import type { ImportView } from '../api/types';

const statusLabel: Record<ImportView['status'], string> = {
  uploading: 'загружается', pending: 'в очереди', running: 'применяется', succeeded: 'выполнен', failed: 'не выполнен',
};

/** Загрузка файла с прогрессом (fetch не сообщает прогресс отправки) */
function upload(file: File, synthetic: boolean, onProgress: (share: number) => void): Promise<ImportView> {
  return ensureCsrf().then((csrf) => new Promise<ImportView>((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.open('POST', apiUrl(`/api/v1/source/events/import${synthetic ? '?synthetic=true' : ''}`));
    xhr.withCredentials = API_BASE !== '';
    xhr.setRequestHeader('X-CSRF-TOKEN', csrf);
    xhr.setRequestHeader('Content-Type', file.name.endsWith('.gz') ? 'application/gzip' : 'application/x-ndjson');
    xhr.upload.onprogress = (e) => { if (e.lengthComputable) onProgress(e.loaded / e.total); };
    xhr.onload = () => {
      if (xhr.status === 202) resolve(JSON.parse(xhr.responseText) as ImportView);
      else {
        let title = `Ошибка ${xhr.status}`;
        try { title = (JSON.parse(xhr.responseText) as { title?: string }).title ?? title; } catch { /* не JSON */ }
        reject(new Error(title));
      }
    };
    xhr.onerror = () => reject(new Error('Анализатор недоступен'));
    xhr.send(file);
  }));
}

/** Прогресс импорта: куски, исходы, первые ошибки */
export function ImportProgress({ view }: { view: ImportView }) {
  const share = view.chunksTotal ? view.chunksDone / view.chunksTotal : 0;
  return (
    <div className="stack" style={{ gap: 4 }} data-testid="import-progress" data-status={view.status}>
      <span className="small">
        Импорт №{view.id}: <strong>{statusLabel[view.status]}</strong> · {view.chunksDone} из {view.chunksTotal} кусков · {view.eventsTotal.toLocaleString('ru-RU')} событий
      </span>
      <div className="track" role="progressbar" aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(share * 100)} aria-label="Прогресс импорта">
        <div className="bar" style={{ width: `${share * 100}%`, background: 'var(--color-accent-default)' }} />
      </div>
      <span className="caption secondary">
        применено {view.applied} · записано {view.recorded} · без изменений {view.noChange} · устаревших {view.stale} · повторов {view.duplicates} · отклонено {view.rejected}
      </span>
      {view.errorCode && <span className="caption" style={{ color: 'var(--color-status-danger-fg)' }}>Причина: {view.errorCode}</span>}
      {view.errors.length > 0 && <span className="caption secondary">Первые ошибки: {view.errors.slice(0, 3).join('; ')}</span>}
    </div>
  );
}

/** Импорт истории событий из файла JSONL (по событию в строке, можно .gz) — для администратора */
export function ImportPanel() {
  const qc = useQueryClient();
  const input = useRef<HTMLInputElement>(null);
  const [file, setFile] = useState<File | null>(null);
  const [synthetic, setSynthetic] = useState(true);
  const [sent, setSent] = useState<number | null>(null);
  const [progress, setProgress] = useState<number | null>(null);
  const [error, setError] = useState<string | null>(null);
  const current = useImport(sent);

  async function start() {
    if (!file) return;
    setError(null);
    setProgress(0);
    try {
      const view = await upload(file, synthetic, setProgress);
      setSent(view.id);
      void qc.invalidateQueries({ queryKey: ['source'] });
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setProgress(null);
    }
  }

  return (
    <section className="card stack" aria-labelledby="imp-title" data-testid="import-panel">
      <h2 id="imp-title">Импорт истории событий</h2>
      <p className="small secondary">
        Файл JSONL — по событию v1 или v2 в строке, как в POST /api/v1/source/events (можно .gz). Применяется кусками по 5000 строк тем же приёмом,
        что и поток; повтор файла безопасен (повторы eventId — дубликаты). Пересчёт — один раз в конце. События раньше даты тестовых данных
        сохраняются в журнал, но состояние не меняют.
      </p>
      <div className="toolbar">
        <input ref={input} type="file" accept=".jsonl,.ndjson,.gz,.txt" aria-label="Файл истории событий" data-testid="import-file"
          onChange={(e) => setFile(e.target.files?.[0] ?? null)} />
        <Checkbox checked={synthetic} onChange={setSynthetic} label="События симулятора" testId="import-simulated" />
        <Button variant="primary" size="sm" disabled={!file} loading={progress !== null} onClick={() => void start()} testId="import-start">Импортировать</Button>
      </div>
      {progress !== null && <span className="caption secondary" role="status">Отправка файла: {Math.round(progress * 100)}%</span>}
      {error && <Banner tone="danger">{error}</Banner>}
      {current.data && <ImportProgress view={current.data} />}
    </section>
  );
}
