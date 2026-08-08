import { describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { AuthProvider } from '../auth/AuthContext';
import { ProtectedRoute } from './ProtectedRoute';

function jsonResponse(status: number, body: unknown) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function renderProtectedDashboard() {
  return render(
    <MemoryRouter initialEntries={['/dashboard']}>
      <AuthProvider>
        <Routes>
          <Route path="/login" element={<p>Login screen</p>} />
          <Route element={<ProtectedRoute />}>
            <Route path="/dashboard" element={<p>Dashboard screen</p>} />
          </Route>
        </Routes>
      </AuthProvider>
    </MemoryRouter>,
  );
}

describe('ProtectedRoute', () => {
  it('redirects to /login when the session probe comes back unauthorized', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => jsonResponse(401, {})),
    );

    renderProtectedDashboard();

    expect(await screen.findByText('Login screen')).toBeInTheDocument();
  });

  it('renders the protected content when the session probe succeeds', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => jsonResponse(200, [])),
    );

    renderProtectedDashboard();

    expect(await screen.findByText('Dashboard screen')).toBeInTheDocument();
  });

  // Revisor finding on PR #20: an earlier version treated any non-401 failure (network error, 5xx) as
  // "authenticated" and rendered protected content without ever validating the session - fail-open.
  // Both cases must show the retry state instead, and never redirect to /login or render the dashboard.
  it('shows a retry state - not the login screen, not the dashboard - on a network failure', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => {
        throw new TypeError('Failed to fetch');
      }),
    );

    renderProtectedDashboard();

    expect(await screen.findByRole('alert')).toHaveTextContent('Não foi possível confirmar sua sessão');
    expect(screen.queryByText('Login screen')).not.toBeInTheDocument();
    expect(screen.queryByText('Dashboard screen')).not.toBeInTheDocument();
  });

  it('shows a retry state - not the login screen, not the dashboard - on a 5xx response', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => jsonResponse(503, { error: 'unavailable' })),
    );

    renderProtectedDashboard();

    expect(await screen.findByRole('alert')).toHaveTextContent('Não foi possível confirmar sua sessão');
    expect(screen.queryByText('Login screen')).not.toBeInTheDocument();
    expect(screen.queryByText('Dashboard screen')).not.toBeInTheDocument();
  });

  it('retries the probe when the retry button is clicked, and renders protected content once it succeeds', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(jsonResponse(503, { error: 'unavailable' }))
      .mockResolvedValueOnce(jsonResponse(200, []));
    vi.stubGlobal('fetch', fetchMock);

    renderProtectedDashboard();

    await screen.findByRole('alert');
    await userEvent.click(screen.getByRole('button', { name: 'Tentar novamente' }));

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(2));
    expect(await screen.findByText('Dashboard screen')).toBeInTheDocument();
  });
});
