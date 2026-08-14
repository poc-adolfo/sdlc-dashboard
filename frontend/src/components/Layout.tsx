import { useState } from 'react';
import { NavLink, Outlet, useNavigate } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';
import { useWorkspace, useWorkspaceList, WorkspaceListProvider } from '../workspace/WorkspaceContext';

// Lets a page (SpecsPage, seção 5.2/5.4) collapse the main nav itself when it wants more room - passed
// down through <Outlet context>, consumed via useOutletContext(), so Layout doesn't need to know why.
export interface LayoutContext {
  setNavOpen: (open: boolean) => void;
}

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
  // Collapse only matters at the desktop breakpoint (left sidebar) - the mobile bottom tabbar always
  // shows its tabs regardless of this state, since there's nowhere for a toggle to usefully live there.
  const [navOpen, setNavOpen] = useState(true);

  return (
    <WorkspaceListProvider>
      <div className={`app-shell${navOpen ? '' : ' app-shell--nav-collapsed'}`}>
        <header className="app-header">
          <span className="app-title">sdlc-dashboard</span>
          <WorkspacePicker />
          <button type="button" className="link-button" onClick={() => void logout()}>
            Sair
          </button>
        </header>

        <main className="app-content">
          <Outlet context={{ setNavOpen } satisfies LayoutContext} />
        </main>

        <nav className={`app-tabbar${navOpen ? '' : ' app-tabbar--collapsed'}`} aria-label="Navegação principal">
          <button
            type="button"
            className="app-tabbar-toggle"
            onClick={() => setNavOpen((open) => !open)}
            aria-label={navOpen ? 'Recolher navegação' : 'Expandir navegação'}
            aria-expanded={navOpen}
          >
            {navOpen ? '‹' : '›'}
          </button>
          {navOpen &&
            NAV_ITEMS.map((item) => (
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
