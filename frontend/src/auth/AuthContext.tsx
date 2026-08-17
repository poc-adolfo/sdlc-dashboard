import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { api, setUnauthorizedHandler, UnauthorizedError } from '../api/client';

type AuthStatus = 'checking' | 'authenticated' | 'anonymous' | 'error';

interface AuthContextValue {
  status: AuthStatus;
  // Nome do operador logado (seção 11) - null em qualquer status que não seja 'authenticated'.
  username: string | null;
  login: (username: string, password: string) => Promise<void>;
  logout: () => Promise<void>;
  // Re-runs the session probe - the only way out of 'error' short of a page reload.
  retry: () => void;
}

const AuthContext = createContext<AuthContextValue | null>(null);

// GET /auth/me (seção 11) doubles as session probe and "who's logged in" - the cookie itself is
// HttpOnly, so this is the only way the frontend finds out.
//
// Only a confirmed 401 means "anonymous". Any other failure (network error, timeout, 5xx) is a
// transient/infrastructure problem, not proof the operator isn't logged in - Revisor review on PR #20
// flagged an earlier version of this that treated every non-401 failure as "authenticated" and let
// protected routes render without ever validating the session. Reporting those as 'error' instead
// keeps the two failure modes distinct: ProtectedRoute shows a retry state for 'error', and only
// redirects to /login for a real 'anonymous'.
async function probeSession(): Promise<{ status: 'authenticated'; username: string } | { status: 'anonymous' | 'error' }> {
  try {
    const { username } = await api.get<{ username: string }>('/auth/me');
    return { status: 'authenticated', username };
  } catch (error) {
    if (error instanceof UnauthorizedError) return { status: 'anonymous' };
    return { status: 'error' };
  }
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const [status, setStatus] = useState<AuthStatus>('checking');
  const [username, setUsername] = useState<string | null>(null);
  const mountedRef = useRef(true);
  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
    };
  }, []);

  const checkSession = useCallback(async () => {
    setStatus('checking');
    const result = await probeSession();
    if (!mountedRef.current) return;
    setStatus(result.status);
    setUsername(result.status === 'authenticated' ? result.username : null);
  }, []);

  useEffect(() => {
    checkSession();
  }, [checkSession]);

  // Any 401 from anywhere in the app - not just this probe - means the session is gone. Without this,
  // a page that's been open past ExpirationMinutes only finds out when some unrelated request happens
  // to fail, and shows that request's own generic error instead of sending the operator back to login.
  useEffect(() => {
    setUnauthorizedHandler(() => {
      if (mountedRef.current) {
        setStatus('anonymous');
        setUsername(null);
      }
    });
    return () => setUnauthorizedHandler(null);
  }, []);

  const login = useCallback(async (username: string, password: string) => {
    await api.post('/auth/login', { username, password });
    setStatus('authenticated');
    setUsername(username);
  }, []);

  const logout = useCallback(async () => {
    // Revisor finding on PR #20: this used to only clear client-side state, leaving the HttpOnly
    // session cookie untouched - the session stayed valid and a reload could silently re-authenticate.
    // POST /auth/logout expires that cookie server-side (see AuthEndpoints.cs). Best-effort: if the
    // call itself fails (network error, session already gone), still clear local state - staying on
    // "authenticated" client-side when the operator asked to leave is worse than a cookie that outlives
    // the UI by a few seconds until it's naturally rejected on the next request.
    try {
      await api.post('/auth/logout');
    } catch {
      // ignored - see above
    } finally {
      setStatus('anonymous');
      setUsername(null);
    }
  }, []);

  const value = useMemo(() => ({ status, username, login, logout, retry: checkSession }), [status, username, login, logout, checkSession]);

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthContextValue {
  const context = useContext(AuthContext);
  if (context === null) throw new Error('useAuth must be used within an AuthProvider');
  return context;
}
