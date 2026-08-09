import { describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { api } from '../api/client';
import { AuthProvider, useAuth } from './AuthContext';

function jsonResponse(status: number, body: unknown) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function TestConsumer() {
  const { status, logout, retry } = useAuth();
  return (
    <div>
      <p>status: {status}</p>
      <button type="button" onClick={() => void logout()}>
        Sair
      </button>
      <button type="button" onClick={retry}>
        Probar de novo
      </button>
    </div>
  );
}

describe('AuthContext logout', () => {
  it('expires the session server-side, and a subsequent probe (simulating a reload) reflects it', async () => {
    // Revisor finding on PR #20: logout previously only cleared client state - the cookie stayed
    // valid, so a reload's session probe could silently re-authenticate. Once the backend has
    // actually expired the cookie (mocked here as the point after which /clients?q= starts 401ing),
    // a fresh probe must also come back anonymous, not just the immediate post-logout state.
    let sessionValid = true;
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (url === '/auth/logout') {
        sessionValid = false;
        return jsonResponse(200, {});
      }
      return sessionValid ? jsonResponse(200, []) : jsonResponse(401, {});
    });
    vi.stubGlobal('fetch', fetchMock);

    render(
      <AuthProvider>
        <TestConsumer />
      </AuthProvider>,
    );

    expect(await screen.findByText('status: authenticated')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Sair' }));
    await waitFor(() => expect(screen.getByText('status: anonymous')).toBeInTheDocument());

    const logoutCall = fetchMock.mock.calls.find(([input]) => (typeof input === 'string' ? input : input.toString()) === '/auth/logout');
    expect(logoutCall).toBeDefined();

    // A fresh probe (what AuthProvider does on every page load) must not silently re-authenticate.
    await userEvent.click(screen.getByRole('button', { name: 'Probar de novo' }));
    await waitFor(() => expect(screen.getByText('status: anonymous')).toBeInTheDocument());
  });

  it('a 401 from an unrelated request elsewhere in the app also flips status to anonymous', async () => {
    // Reported symptom: the session expires mid-page (past ExpirationMinutes), but AuthContext only
    // ever checked once, at mount - a later request from some other screen (e.g. the Assessment
    // client search) would 401 and just show that screen's own generic error, with no way back to
    // /login short of a manual reload. Any 401, from any request, must now flip status immediately.
    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse(200, [])));

    render(
      <AuthProvider>
        <TestConsumer />
      </AuthProvider>,
    );
    expect(await screen.findByText('status: authenticated')).toBeInTheDocument();

    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse(401, {})));
    await expect(api.get('/clients?q=SomethingUnrelated')).rejects.toThrow();

    await waitFor(() => expect(screen.getByText('status: anonymous')).toBeInTheDocument());
  });

  it('still clears client-side state even when the logout request itself fails', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (url === '/auth/logout') throw new TypeError('Failed to fetch');
      return jsonResponse(200, []);
    });
    vi.stubGlobal('fetch', fetchMock);

    render(
      <AuthProvider>
        <TestConsumer />
      </AuthProvider>,
    );

    expect(await screen.findByText('status: authenticated')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Sair' }));

    await waitFor(() => expect(screen.getByText('status: anonymous')).toBeInTheDocument());
  });
});
