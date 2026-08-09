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

// The workspace switcher (Layout.tsx) picks from GET /workspaces, but still only holds an id here -
// this context doesn't need the full workspace record, just to remember the selection across tabs and
// reloads within the same browser.
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
