import { createContext, useCallback, useContext, useMemo, useState, type ReactNode } from 'react';

const STORAGE_KEY = 'sdlc-dashboard:workspaceId';

interface WorkspaceContextValue {
  workspaceId: number | null;
  setWorkspaceId: (id: number | null) => void;
}

const WorkspaceContext = createContext<WorkspaceContextValue | null>(null);

function readStoredWorkspaceId(): number | null {
  const raw = localStorage.getItem(STORAGE_KEY);
  if (raw === null) return null;
  const parsed = Number(raw);
  return Number.isInteger(parsed) && parsed > 0 ? parsed : null;
}

// There is no workspace list/picker endpoint or screen in this WBS (frontend-operacional-sdlc-hermes.md
// only defines assessment/specs/dashboard/credenciais screens - seção 12, items 11-14) - every page
// operates within a single workspace whose id the operator already knows. This just remembers that id
// across tabs and reloads within the same browser, so item 11 doesn't need to invent workspace CRUD UI
// to be usable end to end.
export function WorkspaceProvider({ children }: { children: ReactNode }) {
  const [workspaceId, setWorkspaceIdState] = useState<number | null>(() => readStoredWorkspaceId());

  const setWorkspaceId = useCallback((id: number | null) => {
    setWorkspaceIdState(id);
    if (id === null) localStorage.removeItem(STORAGE_KEY);
    else localStorage.setItem(STORAGE_KEY, String(id));
  }, []);

  const value = useMemo(() => ({ workspaceId, setWorkspaceId }), [workspaceId, setWorkspaceId]);

  return <WorkspaceContext.Provider value={value}>{children}</WorkspaceContext.Provider>;
}

export function useWorkspace(): WorkspaceContextValue {
  const context = useContext(WorkspaceContext);
  if (context === null) throw new Error('useWorkspace must be used within a WorkspaceProvider');
  return context;
}
