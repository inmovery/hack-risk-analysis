import { useState, type FormEvent } from 'react';
import { Navigate, useSearchParams } from 'react-router';
import { useAuth } from '../auth/AuthContext';
import { ApiError, apiUrl } from '../api/client';
import { Banner } from '../components/ui';
import { Button } from '../components/controls';
import { BrandLines } from '../components/icons';

/** Только относительный путь этого же сайта (защита от open redirect, включая `//host` и `/\host`). */
export function safeReturnUrl(raw: string | null): string {
  if (!raw || !raw.startsWith('/') || raw.startsWith('//') || raw.includes('\\')) return '/';
  try {
    const u = new URL(raw, window.location.origin);
    return u.origin === window.location.origin ? u.pathname + u.search + u.hash : '/';
  } catch {
    return '/';
  }
}

export function LoginPage() {
  const { user, login, sessionExpired } = useAuth();
  const [params] = useSearchParams();
  const [form, setForm] = useState({ login: '', password: '' });
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const safeReturn = safeReturnUrl(params.get('returnUrl'));

  if (user) {
    if (safeReturn.startsWith('/api/')) {
      window.location.assign(apiUrl(safeReturn)); // Swagger UI обслуживается API-сервером, не SPA
      return null;
    }
    return <Navigate to={safeReturn} replace />;
  }

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await login(form.login, form.password);
    } catch (err) {
      const status = err instanceof ApiError ? err.status : 0;
      setError(status === 429 ? 'Слишком много попыток. Подождите минуту.' : status === 401 ? 'Неверный логин или пароль' : status === 403 ? 'Сессия обновилась — нажмите «Войти» ещё раз' : 'Сервер недоступен, попробуйте ещё раз');
    } finally {
      setBusy(false);
    }
  }

  return (
    <main className="login-page">
      <BrandLines className="login-pattern" />
      <form className="card login-card" onSubmit={submit} aria-labelledby="login-title" noValidate>
        <div className="row" style={{ gap: 12 }}>
          <span className="brand-mark brand-mark-dark" aria-hidden="true">é</span>
          <h1 id="login-title">Aéza · Risk</h1>
        </div>
        <p className="small secondary">Вход для маркетологов и администраторов модели</p>
        {(sessionExpired || params.get('expired')) && <Banner tone="warning">Сессия истекла — войдите снова.</Banner>}
        <label className="field">
          <span className="field-label">Логин</span>
          <input className="input" name="login" autoComplete="username" required value={form.login} onChange={(e) => setForm({ ...form, login: e.target.value })} aria-invalid={!!error} />
        </label>
        <label className="field">
          <span className="field-label">Пароль</span>
          <input className="input" name="password" type="password" autoComplete="current-password" required value={form.password}
            onChange={(e) => setForm({ ...form, password: e.target.value })} aria-invalid={!!error} aria-describedby={error ? 'login-error' : undefined} />
        </label>
        {error && (
          <p id="login-error" className="field-error" role="alert">
            {error}
          </p>
        )}
        <Button variant="primary" type="submit" loading={busy} disabled={!form.login || !form.password}>
          Войти
        </Button>
        <p className="caption muted">Ошибка входа не раскрывает, существует ли логин. После 5 неудачных попыток вход блокируется на 5 минут.</p>
      </form>
    </main>
  );
}
