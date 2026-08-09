import { NavLink, Outlet } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';

const NAV_ITEMS = [
  { to: '/assessment', label: 'Assessment' },
  { to: '/specs', label: 'Specs' },
  { to: '/dashboard', label: 'Dashboard' },
  { to: '/credenciais', label: 'Credenciais' },
];

// Bottom tab bar, not a top nav: mobile-first (seção 1) means designing for a thumb-reachable
// one-handed layout first, then letting it scale up - a bottom bar works at any viewport width, so
// there's no separate "desktop nav" to maintain.
export function Layout() {
  const { logout } = useAuth();

  return (
    <div className="app-shell">
      <header className="app-header">
        <span className="app-title">sdlc-dashboard</span>
        <button type="button" className="link-button" onClick={logout}>
          Sair
        </button>
      </header>

      <main className="app-content">
        <Outlet />
      </main>

      <nav className="app-tabbar" aria-label="Navegação principal">
        {NAV_ITEMS.map((item) => (
          <NavLink
            key={item.to}
            to={item.to}
            className={({ isActive }) => `app-tab${isActive ? ' app-tab--active' : ''}`}
          >
            {item.label}
          </NavLink>
        ))}
      </nav>
    </div>
  );
}
