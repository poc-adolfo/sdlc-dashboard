import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { api, setUnauthorizedHandler, UnauthorizedError } from '../api/client';

type AuthStatus = 'checking' | 'authenticated' | 'anonymous' | 'error';

interface AuthContextValue {
  status: AuthStatus;
  login: (username: string, password: string) => Promise<void>;
  logout: () => Promise<void>;
  // Re-runs the session probe - the only way out of 'error' short of a page reload.
  retry: () => void;
}

const AuthContext = createContext<AuthContextValue | null>(null);

// There is no dedicated "whoami" endpoint (seção 14 of the spec doesn't define one - the session
// cookie is HttpOnly, so the frontend can't just read it either). GET /clients?q= is the cheapest
// authenticated read in the API contract - used here purely as a session probe on first load, not for
// its actual search behavior.
//
// Only a confirmed 401 means "anonymous". Any other failure (network error, timeout, 5xx) is a
// transient/infrastructure problem, not proof the operator isn't logged in - Revisor review on PR #20
// flagged an earlier version of this that treated every non-401 failure as "authenticated" and let
// protected routes render without ever validating the session. Reporting those as 'error' instead
// keeps the two failure modes distinct: ProtectedRoute shows a retry state for 'error', and only
// redirects to /login for a real 'anonymous'.
async function probeSession(): Promise<'authenticated' | 'anonymous' | 'error'> {
  try {
    await api.get('/clients?q=');
    return 'authenticated';
  } catch (error) {
    if (error instanceof UnauthorizedError) return 'anonymous';
    return 'error';
  }
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const [status, setStatus] = useState<AuthStatus>('checking');
  const mountedRef = useRef(true);
  useEffect(() => () => {
    mountedRef.current = false;
  }, []);

  const checkSession = useCallback(async () => {
    setStatus('checking');
    const result = await probeSession();
    if (mountedRef.current) setStatus(result);
  }, []);

  useEffect(() => {
    checkSession();
  }, [checkSession]);

  // Any 401 from anywhere in the app - not just this probe - means the session is gone. Without this,
  // a page that's been open past ExpirationMinutes only finds out when some unrelated request happens
  // to fail, and shows that request's own generic error instead of sending the operator back to login.
  useEffect(() => {
    setUnauthorizedHandler(() => {
      if (mountedRef.current) setStatus('anonymous');
    });
    return () => setUnauthorizedHandler(null);
  }, []);

  const login = useCallback(async (username: string, password: string) => {
    await api.post('/auth/login', { username, password });
    setStatus('authenticated');
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
    }
  }, []);

  const value = useMemo(() => ({ status, login, logout, retry: checkSession }), [status, login, logout, checkSession]);

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthContextValue {
  const context = useContext(AuthContext);
  if (context === null) throw new Error('useAuth must be used within an AuthProvider');
  return context;
}
