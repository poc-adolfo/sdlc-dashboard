import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { api, UnauthorizedError } from '../api/client';

type AuthStatus = 'checking' | 'authenticated' | 'anonymous';

interface AuthContextValue {
  status: AuthStatus;
  login: (username: string, password: string) => Promise<void>;
  logout: () => void;
}

const AuthContext = createContext<AuthContextValue | null>(null);

// There is no dedicated "whoami" endpoint (seção 14 of the spec doesn't define one - the session
// cookie is HttpOnly, so the frontend can't just read it either). GET /clients?q= is the cheapest
// authenticated read in the API contract - used here purely as a session probe on first load, not for
// its actual search behavior.
async function probeSession(): Promise<boolean> {
  try {
    await api.get('/clients?q=');
    return true;
  } catch (error) {
    if (error instanceof UnauthorizedError) return false;
    // A transient/network failure shouldn't strand the user on a login screen they can't get past -
    // treat it as "not known to be logged out" and let the first real request settle it via onUnauthorized.
    return true;
  }
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const [status, setStatus] = useState<AuthStatus>('checking');

  useEffect(() => {
    let cancelled = false;
    probeSession().then((authenticated) => {
      if (!cancelled) setStatus(authenticated ? 'authenticated' : 'anonymous');
    });
    return () => {
      cancelled = true;
    };
  }, []);

  const login = useCallback(async (username: string, password: string) => {
    await api.post('/auth/login', { username, password });
    setStatus('authenticated');
  }, []);

  const logout = useCallback(() => {
    // POST /auth/login is the only auth endpoint the spec defines (seção 14) - there is no
    // POST /auth/logout. Clearing client-side state and letting the signed cookie expire on its own
    // (Authentication:ExpirationMinutes) is the only logout this contract supports.
    setStatus('anonymous');
  }, []);

  const value = useMemo(() => ({ status, login, logout }), [status, login, logout]);

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthContextValue {
  const context = useContext(AuthContext);
  if (context === null) throw new Error('useAuth must be used within an AuthProvider');
  return context;
}
