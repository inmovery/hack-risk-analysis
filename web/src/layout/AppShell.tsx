import { useEffect, useState, type ReactNode } from 'react';
import { NavLink, Outlet, useLocation, useOutletContext } from 'react-router';
import { useAuth } from '../auth/AuthContext';
import { useLive } from '../live/LiveContext';
import { useSummary } from '../api/queries';
import { Banner, LivePill, Tag } from '../components/ui';
import { BrandLines, IconCustomers, IconModel, IconOverview, IconSimulation, IconSource } from '../components/icons';
import { age, dateTime, localTime } from '../format';

const nav = [
  { to: '/', label: 'Обзор', icon: <IconOverview />, end: true },
  { to: '/customers', label: 'Клиенты', icon: <IconCustomers />, end: false },
  { to: '/model', label: 'Модель', icon: <IconModel />, end: false },
  { to: '/source', label: 'Источник', icon: <IconSource />, end: false },
  { to: '/simulation', label: 'Симуляция', icon: <IconSimulation />, end: false },
];

export function AppShell() {
  const { user, logout } = useAuth();
  const live = useLive();
  const [menu, setMenu] = useState(false);
  const loc = useLocation();
  useEffect(() => setMenu(false), [loc.pathname]);

  return (
    <div className="shell" data-menu={menu ? 'open' : 'closed'}>
      <a className="skip-link" href="#main">
        К содержимому
      </a>
      <aside className="sidebar" aria-label="Навигация">
        <div className="brand">
          <span className="brand-mark" aria-hidden="true">é</span>
          <span className="stack" style={{ gap: 0 }}>
            <span className="brand-title">Aéza · Risk</span>
            <span className="brand-sub">Анализатор оттока</span>
          </span>
        </div>
        <nav className="stack" style={{ gap: 4 }}>
          {nav.map((n) => (
            <NavLink key={n.to} to={n.to} end={n.end} className="nav-item">
              <span className="nav-icon" aria-hidden="true">
                {n.icon}
              </span>
              {n.label}
            </NavLink>
          ))}
        </nav>
        <div className="sidebar-spacer" />
        <BrandLines className="sidebar-pattern" />
        <div className="user-block">
          <strong>{user?.login}</strong>
          <span className="caption secondary">{user?.role === 'admin' ? 'Администратор модели' : 'Аналитик (чтение)'}</span>
          <button className="linklike" onClick={() => void logout()} data-testid="logout">
            Выйти
          </button>
        </div>
      </aside>
      <div className="main">
        <div className="sr-only" aria-live="polite" data-testid="live-announcement">
          {live.announcement}
        </div>
        <Outlet context={{ openMenu: () => setMenu(true) }} />
      </div>
    </div>
  );
}

/** Шапка страницы: заголовок, свежесть источника, активное поколение, live-статус, пересчёт. */
export function Topbar({ title, children }: { title: ReactNode; children?: ReactNode }) {
  const live = useLive();
  const summary = useSummary();
  const shell = useOutletContext<{ openMenu: () => void } | undefined>();
  const g = summary.data?.generation;
  const pending = summary.data?.pending;
  const stale = g ? g.sourceAgeSeconds > 24 * 3600 : false;
  // Поток считается молчащим через час без событий (Events:SilentAfterMinutes по умолчанию)
  const silent = g ? g.sourceKind === 'stream' && g.sourceAgeSeconds > 3600 : false;
  return (
    <>
      <header className="topbar">
        <button className="btn btn-ghost btn-sm menu-toggle" aria-label="Открыть меню" onClick={() => shell?.openMenu()}>
          ☰
        </button>
        <h1>{title}</h1>
        {children}
        {g && (g.sourceKind === 'stream' ? (
          <Tag tone={silent ? 'warning' : 'neutral'} title={`Последнее изменение источника (событие): ${dateTime(g.sourceUpdatedAt)}; база — тестовые данные на ${dateTime(g.sourceAsOf)}`}>
            <span data-testid="freshness">{silent ? `Поток молчит ${age(g.sourceAgeSeconds)}` : `Поток · событие ${age(g.sourceAgeSeconds)} назад`}</span>
          </Tag>
        ) : (
          <Tag tone={stale ? 'warning' : 'neutral'} title={`Момент выгрузки тестовых данных (sourceAsOf): ${dateTime(g.sourceAsOf)}`}>
            <span data-testid="freshness">Данные на {dateTime(g.sourceAsOf).replace(' UTC', '')} UTC · {age(g.sourceAgeSeconds)} назад</span>
          </Tag>
        ))}
        {g?.clockMode === 'source' && <Tag tone="info">Оценка на момент выгрузки данных</Tag>}
        {pending && (
          <Tag tone="info" title={`Задание #${pending.jobId}`}>
            ⟳ Пересчёт…
          </Tag>
        )}
        {g && (
          <span className="caption secondary gen-label" data-testid="generation-label" data-generation={g.id} data-model-revision={g.modelRevision}>
            Расчёт №{g.id} · модель r{g.modelRevision} · источник r{g.sourceRevision} · {localTime(g.publishedAt)}
          </span>
        )}
        <LivePill status={live.status} />
      </header>
      {summary.data?.lastFailure && g && (summary.data.lastFailure.modelRevision ?? 0) >= g.modelRevision && (
        <div style={{ padding: '16px 24px 0' }}>
          <Banner tone="danger">
            Последний пересчёт (задание #{summary.data.lastFailure.jobId}, {summary.data.lastFailure.errorCode}) не удался. Показан последний успешный расчёт №{g.id}.
          </Banner>
        </div>
      )}
    </>
  );
}
