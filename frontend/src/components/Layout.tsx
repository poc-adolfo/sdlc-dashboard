import { NavLink, Outlet, useNavigate } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';
import { useWorkspace, useWorkspaceList, WorkspaceListProvider } from '../workspace/WorkspaceContext';

const NAV_ITEMS = [
  { to: '/workspace', label: 'Workspace' },
  { to: '/specs', label: 'Specs' },
  { to: '/dashboard', label: 'Dashboard' },
];

function WorkspacePicker() {
  const { workspaceId, setWorkspaceId } = useWorkspace();
  const { workspaces, loading } = useWorkspaceList();
  const navigate = useNavigate();

  // Creating/editing a workspace now lives on WorkspacePage itself (seção 4/5.1/8 - onboarding
  // consolidated into one screen) - "Novo" just clears the selection and takes the operator there, it no
  // longer opens its own inline form.
  function handleNovo() {
    setWorkspaceId(null);
    navigate('/workspace');
  }

  return (
    <div className="workspace-picker">
      <label htmlFor="workspace-select">Workspace</label>
      <select
        id="workspace-select"
        value={workspaceId ?? ''}
        onChange={(e) => setWorkspaceId(e.target.value ? Number(e.target.value) : null)}
        disabled={loading}
      >
        <option value="">Selecione...</option>
        {workspaces.map((w) => (
          <option key={w.id} value={w.id}>
            {w.name}
          </option>
        ))}
      </select>
      <button type="button" onClick={handleNovo}>
        Novo
      </button>
    </div>
  );
}

// Bottom tab bar, not a top nav: mobile-first (seção 1) means designing for a thumb-reachable
// one-handed layout first, then letting it scale up - from the tablet breakpoint up it repositions to a
// top tab strip instead (index.css, .app-shell grid areas), same markup either way.
export function Layout() {
  const { logout } = useAuth();

  return (
    <WorkspaceListProvider>
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
    </WorkspaceListProvider>
  );
}
