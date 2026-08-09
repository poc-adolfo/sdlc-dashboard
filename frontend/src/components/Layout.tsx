import { useState, type FormEvent } from 'react';
import { NavLink, Outlet } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';
import { useWorkspace } from '../workspace/WorkspaceContext';

const NAV_ITEMS = [
  { to: '/assessment', label: 'Assessment' },
  { to: '/specs', label: 'Specs' },
  { to: '/dashboard', label: 'Dashboard' },
  { to: '/credenciais', label: 'Credenciais' },
];

function WorkspacePicker() {
  const { workspaceId, setWorkspaceId } = useWorkspace();
  const [draft, setDraft] = useState(workspaceId?.toString() ?? '');

  function handleSubmit(event: FormEvent) {
    event.preventDefault();
    const parsed = Number(draft);
    setWorkspaceId(Number.isInteger(parsed) && parsed > 0 ? parsed : null);
  }

  return (
    <form className="workspace-picker" onSubmit={handleSubmit}>
      <label htmlFor="workspace-id">Workspace</label>
      <input
        id="workspace-id"
        inputMode="numeric"
        placeholder="ID"
        value={draft}
        onChange={(e) => setDraft(e.target.value)}
      />
      <button type="submit">Usar</button>
    </form>
  );
}

// Bottom tab bar, not a top nav: mobile-first (seção 1) means designing for a thumb-reachable
// one-handed layout first, then letting it scale up - a bottom bar works at any viewport width, so
// there's no separate "desktop nav" to maintain.
export function Layout() {
  const { logout } = useAuth();

  return (
    <div className="app-shell">
      <header className="app-header">
        <span className="app-title">sdlc-dashboard</span>
        <WorkspacePicker />
        <button type="button" className="link-button" onClick={() => void logout()}>
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
