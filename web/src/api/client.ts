// HTTP-клиент: cookie-сессия + CSRF-заголовок для изменяющих запросов, ProblemDetails → ApiError, 401 → событие «сессия истекла».

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly code: string | undefined,
    message: string,
    public readonly body: Record<string, unknown> | undefined,
  ) {
    super(message);
  }
  get fieldErrors(): Record<string, string[]> {
    return (this.body?.errors as Record<string, string[]> | undefined) ?? {};
  }
}

/** Базовый адрес API (VITE_API_BASE); пусто — тот же origin */
export const API_BASE: string = (import.meta.env.VITE_API_BASE ?? '').replace(/\/+$/, '');
const crossOrigin = API_BASE !== '';
const credentials: RequestCredentials = crossOrigin ? 'include' : 'same-origin';

export function apiUrl(path: string): string {
  return API_BASE + path;
}

let csrfToken: string | null = null;
const unauthorizedListeners = new Set<() => void>();

export function onUnauthorized(fn: () => void): () => void {
  unauthorizedListeners.add(fn);
  return () => unauthorizedListeners.delete(fn);
}

export function setCsrfToken(token: string | null): void {
  csrfToken = token;
}

/** Сброс токена после потери сессии: читаемая cookie XSRF-TOKEN привязана к прежнему пользователю. */
export function clearCsrf(): void {
  csrfToken = null;
  document.cookie = 'XSRF-TOKEN=; Max-Age=0; path=/; SameSite=Strict';
}

function readCsrfCookie(): string | null {
  if (crossOrigin) return null; // cookie XSRF-TOKEN принадлежит API-хосту и здесь не видна; токен — из ответов API
  const m = document.cookie.match(/(?:^|; )XSRF-TOKEN=([^;]+)/);
  return m?.[1] ? decodeURIComponent(m[1]) : null;
}

/** Токен из памяти (последний выданный сервером), иначе из cookie; force — всегда запросить новый. */
export async function ensureCsrf(force = false): Promise<string> {
  if (!force) {
    const known = csrfToken ?? readCsrfCookie();
    if (known) return known;
  }
  const r = await fetch(apiUrl('/api/v1/auth/csrf'), { credentials });
  const body = (await r.json()) as { token: string };
  csrfToken = body.token;
  return body.token;
}

export interface RequestOptions {
  method?: 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE';
  body?: unknown;
  rawBody?: BodyInit;
  headers?: Record<string, string>;
  signal?: AbortSignal;
  /** Не поднимать глобальное событие 401 (например, проверка сессии при старте). */
  silent401?: boolean;
}

export async function api<T>(path: string, opts: RequestOptions = {}): Promise<T> {
  const method = opts.method ?? 'GET';
  const headers: Record<string, string> = { Accept: 'application/json', ...(opts.headers ?? {}) };
  if (method !== 'GET') headers['X-CSRF-TOKEN'] = await ensureCsrf();
  let body: BodyInit | undefined = opts.rawBody;
  if (opts.body !== undefined) {
    headers['Content-Type'] = 'application/json';
    body = JSON.stringify(opts.body);
  }
  const r = await fetch(apiUrl(path), { method, headers, body, credentials, signal: opts.signal });
  if (r.status === 204) return undefined as T;
  const text = await r.text();
  const data = text ? (JSON.parse(text) as unknown) : undefined;
  if (!r.ok) {
    const problem = (data ?? {}) as Record<string, unknown>;
    if (r.status === 401 && !opts.silent401) unauthorizedListeners.forEach((fn) => fn());
    throw new ApiError(r.status, problem.code as string | undefined, (problem.title as string | undefined) ?? `HTTP ${r.status}`, problem);
  }
  return data as T;
}
