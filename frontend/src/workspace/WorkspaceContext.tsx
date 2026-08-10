import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { api } from '../api/client';

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

// The workspace switcher (Layout.tsx) picks from GET /workspaces (see WorkspaceListProvider below), but
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

export interface WorkspaceOption {
  id: number;
  name: string;
}

interface WorkspaceListContextValue {
  workspaces: WorkspaceOption[];
  loading: boolean;
  refresh: () => Promise<WorkspaceOption[]>;
}

const WorkspaceListContext = createContext<WorkspaceListContextValue | null>(null);

// Separate from WorkspaceProvider above on purpose: WorkspaceProvider wraps the whole app (App.tsx),
// including /login, where GET /workspaces would just 401 before the operator has a session. Layout.tsx
// mounts this one instead, scoped to the authenticated subtree - the header's dropdown and the
// WorkspacePage's create/edit form both read the same list from here (via useWorkspaceListContext) so
// creating a workspace on WorkspacePage makes it show up in the header dropdown immediately, without a
// second, independent fetch to keep in sync.
export function WorkspaceListProvider({ children }: { children: ReactNode }) {
  const [workspaces, setWorkspaces] = useState<WorkspaceOption[]>([]);
  const [loading, setLoading] = useState(true);
  // Same request-sequence guard used throughout this app (Revisor/QA findings on PR #22/#24/#25):
  // refresh() can be called again (e.g. right after creating a workspace) before a slower, earlier call
  // has resolved - a stale response must not overwrite the newer one.
  const fetchSeq = useRef(0);

  const refresh = useCallback(async () => {
    const seq = ++fetchSeq.current;
    setLoading(true);
    try {
      const data = await api.get<WorkspaceOption[]>('/workspaces');
      if (seq === fetchSeq.current) setWorkspaces(data);
      return data;
    } finally {
      if (seq === fetchSeq.current) setLoading(false);
    }
  }, []);

  useEffect(() => {
    refresh();
  }, [refresh]);

  const value = useMemo(() => ({ workspaces, loading, refresh }), [workspaces, loading, refresh]);

  return <WorkspaceListContext.Provider value={value}>{children}</WorkspaceListContext.Provider>;
}

export function useWorkspaceList(): WorkspaceListContextValue {
  const context = useContext(WorkspaceListContext);
  if (context === null) throw new Error('useWorkspaceList must be used within a WorkspaceListProvider');
  return context;
}
