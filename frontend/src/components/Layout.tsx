import { useEffect, useRef, useState, type FormEvent } from 'react';
import { NavLink, Outlet } from 'react-router-dom';
import { api } from '../api/client';
import { useAuth } from '../auth/AuthContext';
import { useWorkspace } from '../workspace/WorkspaceContext';

const NAV_ITEMS = [
  { to: '/assessment', label: 'Assessment' },
  { to: '/specs', label: 'Specs' },
  { to: '/dashboard', label: 'Dashboard' },
  { to: '/credenciais', label: 'Credenciais' },
];

interface WorkspaceOption {
  id: number;
  name: string;
}

// Required fields only (WorkspaceEndpoints.cs Validate) - specs_path/specs_repo/code_repo/etc. stay
// editable later via PATCH, no need to front-load them into this quick-create form.
function NewWorkspaceForm({ onCreated, onCancel }: { onCreated: (workspace: WorkspaceOption) => void; onCancel: () => void }) {
  const [name, setName] = useState('');
  const [platform, setPlatform] = useState<'github' | 'azure_devops'>('github');
  const [platformRef, setPlatformRef] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function handleSubmit(event: FormEvent) {
    event.preventDefault();
    setSubmitting(true);
    setError(null);
    try {
      const workspace = await api.post<WorkspaceOption>('/workspaces', { name, platform, platform_ref: platformRef });
      onCreated(workspace);
    } catch {
      setError('Não foi possível criar o workspace. Verifique os campos e tente novamente.');
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <form className="workspace-new-form" onSubmit={handleSubmit}>
      <label htmlFor="workspace-new-name">Nome</label>
      <input id="workspace-new-name" value={name} onChange={(e) => setName(e.target.value)} disabled={submitting} required />

      <label htmlFor="workspace-new-platform">Plataforma</label>
      <select
        id="workspace-new-platform"
        value={platform}
        onChange={(e) => setPlatform(e.target.value as 'github' | 'azure_devops')}
        disabled={submitting}
      >
        <option value="github">GitHub</option>
        <option value="azure_devops">Azure DevOps</option>
      </select>

      <label htmlFor="workspace-new-platform-ref">Repositório/Projeto</label>
      <input
        id="workspace-new-platform-ref"
        value={platformRef}
        onChange={(e) => setPlatformRef(e.target.value)}
        placeholder="org/repo"
        disabled={submitting}
        required
      />

      {error && <p role="alert">{error}</p>}

      <div className="workspace-new-form-actions">
        <button type="submit" disabled={submitting}>Criar</button>
        <button type="button" onClick={onCancel} disabled={submitting}>Cancelar</button>
      </div>
    </form>
  );
}

function WorkspacePicker() {
  const { workspaceId, setWorkspaceId } = useWorkspace();
  const [workspaces, setWorkspaces] = useState<WorkspaceOption[]>([]);
  const [loading, setLoading] = useState(true);
  const [creating, setCreating] = useState(false);
  // Bumped on every fetch this effect starts so a slower, now-stale response can't overwrite a newer
  // one - same race the ClientPicker/SpecsPage/DashboardPage fetches were already fixed for.
  const fetchSeq = useRef(0);

  useEffect(() => {
    const seq = ++fetchSeq.current;
    setLoading(true);
    api.get<WorkspaceOption[]>('/workspaces').then(
      (data) => {
        if (seq !== fetchSeq.current) return;
        setWorkspaces(data);
        setLoading(false);
      },
      () => {
        if (seq !== fetchSeq.current) return;
        setLoading(false);
      },
    );
  }, []);

  // Creation already updates the list optimistically (below) with the exact record the backend
  // returned, so there's no need to re-fetch the whole list after Criar/Cancelar.
  function handleCreated(workspace: WorkspaceOption) {
    setWorkspaces((prev) => [...prev, workspace].sort((a, b) => a.name.localeCompare(b.name)));
    setWorkspaceId(workspace.id);
    setCreating(false);
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
      <button type="button" onClick={() => setCreating(true)}>
        Novo
      </button>
      {creating && <NewWorkspaceForm onCreated={handleCreated} onCancel={() => setCreating(false)} />}
    </div>
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
