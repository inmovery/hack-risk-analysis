import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { api, clearCsrf, ensureCsrf, onUnauthorized, setCsrfToken } from '../api/client';
import type { UserInfo } from '../api/types';

interface AuthState {
  user: UserInfo | null;
  loading: boolean;
  sessionExpired: boolean;
  login: (login: string, password: string) => Promise<void>;
  logout: () => Promise<void>;
}

const Ctx = createContext<AuthState | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const qc = useQueryClient();
  const [user, setUser] = useState<UserInfo | null>(null);
  const [loading, setLoading] = useState(true);
  const [sessionExpired, setSessionExpired] = useState(false);

  useEffect(() => {
    api<UserInfo>('/api/v1/auth/me', { silent401: true })
      .then(setUser)
      .catch(() => setUser(null))
      .finally(() => setLoading(false));
  }, []);

  useEffect(
    () =>
      onUnauthorized(() => {
        clearCsrf();
        setUser((u) => {
          if (u) setSessionExpired(true);
          return null;
        });
        qc.clear();
      }),
    [qc],
  );

  const login = useCallback(
    async (login: string, password: string) => {
      clearCsrf();
      await ensureCsrf(true); // свежий токен анонимного пользователя: старый мог быть привязан к истёкшей сессии
      const r = await api<{ user: UserInfo; csrfToken: string }>('/api/v1/auth/login', { method: 'POST', body: { login, password }, silent401: true });
      setCsrfToken(r.csrfToken);
      qc.clear();
      setSessionExpired(false);
      setUser(r.user);
    },
    [qc],
  );

  const logout = useCallback(async () => {
    try {
      await api('/api/v1/auth/logout', { method: 'POST', silent401: true });
    } finally {
      clearCsrf();
      qc.clear();
      setUser(null);
    }
  }, [qc]);

  const value = useMemo(() => ({ user, loading, sessionExpired, login, logout }), [user, loading, sessionExpired, login, logout]);
  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}

export function useAuth(): AuthState {
  const v = useContext(Ctx);
  if (!v) throw new Error('AuthProvider missing');
  return v;
}
