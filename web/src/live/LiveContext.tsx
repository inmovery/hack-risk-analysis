import { createContext, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from '@microsoft/signalr';
import { useQueryClient } from '@tanstack/react-query';
import { apiUrl } from '../api/client';

export type LiveStatus = 'connecting' | 'live' | 'reconnecting' | 'offline';

interface LiveState {
  status: LiveStatus;
  /** Последнее опубликованное поколение, о котором сообщил сервер. */
  lastGenerationId: number | null;
  lastEventAt: number | null;
  /** Интервал опроса как fallback, пока live недоступен. */
  /** Интервал опроса REST: 15 с без live-канала, 60 с при live */
  pollInterval: number;
  announcement: string;
}

const Ctx = createContext<LiveState>({ status: 'connecting', lastGenerationId: null, lastEventAt: null, pollInterval: 15000, announcement: '' });

interface GenerationPublished { generationId: number; sourceRevision: number; sourceSeq: number; modelRevision: number; asOf: string; reason: string; publishedAt: string; }
interface SourceChanged { seq: number; applied: number; received: number; changes: Record<string, number>; synthetic: boolean; origin: string; }

/** SignalR: сервер шлёт идентификаторы, клиент обновляет запросы без перезагрузки; без связи — опрос */
export function LiveProvider({ enabled, children }: { enabled: boolean; children: ReactNode }) {
  const qc = useQueryClient();
  const [status, setStatus] = useState<LiveStatus>('connecting');
  const [lastGenerationId, setLastGenerationId] = useState<number | null>(null);
  const [lastEventAt, setLastEventAt] = useState<number | null>(null);
  const [announcement, setAnnouncement] = useState('');
  const debounce = useRef<number | undefined>(undefined);

  useEffect(() => {
    if (!enabled) return;
    let stopped = false;
    let retryTimer: number | undefined;
    const invalidate = (all = false) => {
      window.clearTimeout(debounce.current);
      debounce.current = window.setTimeout(() => {
        void qc.invalidateQueries({ queryKey: ['gen'] });
        void qc.invalidateQueries({ queryKey: ['settings'] });
        void qc.invalidateQueries({ queryKey: ['source'] });
        if (all) void qc.invalidateQueries();
      }, 250);
    };
    const conn: HubConnection = new HubConnectionBuilder()
      .withUrl(apiUrl('/hubs/risk'), { withCredentials: true })
      .withAutomaticReconnect({ nextRetryDelayInMilliseconds: (ctx) => Math.min(15000, 1000 * 2 ** ctx.previousRetryCount) })
      .configureLogging(LogLevel.Warning)
      .build();

    conn.on('generationPublished', (e: GenerationPublished) => {
      setLastGenerationId(e.generationId);
      setLastEventAt(Date.now());
      setAnnouncement(`Данные обновлены: расчёт №${e.generationId} (модель r${e.modelRevision}, источник r${e.sourceRevision})`);
      invalidate();
    });
    conn.on('jobQueued', () => invalidate());
    // Пакет событий принят: журнал и состояние приёма обновляются сразу, расчёт — после пересчёта (generationPublished)
    conn.on('sourceChanged', (e: SourceChanged) => {
      setLastEventAt(Date.now());
      if (e.applied > 0) setAnnouncement(`Источник: применено событий ${e.applied} из ${e.received}${e.synthetic ? ' (симулятор)' : ''}`);
      void qc.invalidateQueries({ queryKey: ['source'] });
    });
    conn.on('recalcFailed', () => invalidate());
    // Канал событий реплики восстановился после разрыва: события могли потеряться — перечитать всё
    conn.on('resync', () => invalidate(true));
    conn.onreconnecting(() => setStatus('reconnecting'));
    conn.onreconnected(() => {
      setStatus('live');
      invalidate(true);
    });
    conn.onclose(() => {
      if (stopped) return;
      setStatus('offline');
      retryTimer = window.setTimeout(start, 15000);
    });

    async function start() {
      if (stopped || conn.state !== HubConnectionState.Disconnected) return;
      try {
        setStatus((s) => (s === 'offline' ? 'reconnecting' : s));
        await conn.start();
        setStatus('live');
        invalidate(true);
      } catch {
        setStatus('offline');
        retryTimer = window.setTimeout(start, 15000);
      }
    }
    void start();
    return () => {
      stopped = true;
      window.clearTimeout(retryTimer);
      void conn.stop();
    };
  }, [enabled, qc]);

  const value = useMemo<LiveState>(
    () => ({ status, lastGenerationId, lastEventAt, pollInterval: status === 'live' ? 60000 : 15000, announcement }),
    [status, lastGenerationId, lastEventAt, announcement],
  );
  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}

export const useLive = () => useContext(Ctx);
