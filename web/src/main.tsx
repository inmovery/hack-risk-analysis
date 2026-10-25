import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter, Navigate, Route, Routes, useLocation } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import '@fontsource-variable/inter';
import '@fontsource-variable/exo-2';
import './styles/app.css';
import { AuthProvider, useAuth } from './auth/AuthContext';
import { LiveProvider } from './live/LiveContext';
import { AppShell } from './layout/AppShell';
import { LoginPage } from './pages/LoginPage';
import { OverviewPage } from './pages/OverviewPage';
import { CustomersPage } from './pages/CustomersPage';
import { CustomerPage } from './pages/CustomerPage';
import { ModelPage } from './pages/ModelPage';
import { SourcePage } from './pages/SourcePage';
import { SimulationPage } from './pages/SimulationPage';

const queryClient = new QueryClient({
  defaultOptions: { queries: { staleTime: 5_000, refetchOnWindowFocus: true, retry: 1 } },
});

function Protected() {
  const { user, loading, sessionExpired } = useAuth();
  const loc = useLocation();
  if (loading) return <div className="content" aria-busy="true">Загрузка…</div>;
  if (!user) {
    const ret = encodeURIComponent(loc.pathname + loc.search);
    return <Navigate to={`/login?returnUrl=${ret}${sessionExpired ? '&expired=1' : ''}`} replace />;
  }
  return (
    <LiveProvider enabled>
      <AppShell />
    </LiveProvider>
  );
}

function App() {
  return (
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route element={<Protected />}>
        <Route index element={<OverviewPage />} />
        <Route path="customers" element={<CustomersPage />} />
        <Route path="customers/:id" element={<CustomerPage />} />
        <Route path="model" element={<ModelPage />} />
        <Route path="source" element={<SourcePage />} />
        <Route path="simulation" element={<SimulationPage />} />
        <Route path="*" element={<Navigate to="/" replace />} />
      </Route>
    </Routes>
  );
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <BrowserRouter>
        <AuthProvider>
          <App />
        </AuthProvider>
      </BrowserRouter>
    </QueryClientProvider>
  </StrictMode>,
);
