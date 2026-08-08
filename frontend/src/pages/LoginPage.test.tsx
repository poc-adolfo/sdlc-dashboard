import { describe, expect, it, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { AuthProvider } from '../auth/AuthContext';
import { LoginPage } from './LoginPage';

function jsonResponse(status: number, body: unknown) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

describe('LoginPage', () => {
  beforeEach(() => {
    // AuthProvider probes the session on mount (GET /clients?q=) - unauthenticated by default so the
    // login form actually renders instead of redirecting away.
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => jsonResponse(401, { error: 'unauthorized' })),
    );
  });

  it('submits the entered credentials to POST /auth/login', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL, _init?: RequestInit) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (url === '/auth/login') return jsonResponse(200, {});
      return jsonResponse(401, {});
    });
    vi.stubGlobal('fetch', fetchMock);

    render(
      <MemoryRouter initialEntries={['/login']}>
        <AuthProvider>
          <LoginPage />
        </AuthProvider>
      </MemoryRouter>,
    );

    await userEvent.type(screen.getByLabelText('Usuário'), 'operator');
    await userEvent.type(screen.getByLabelText('Senha'), 'secret');
    await userEvent.click(screen.getByRole('button', { name: 'Entrar' }));

    await waitFor(() => {
      const loginCall = fetchMock.mock.calls.find(([input]) => (typeof input === 'string' ? input : input.toString()) === '/auth/login');
      expect(loginCall).toBeDefined();
    });
    const [, init] = fetchMock.mock.calls.find(([input]) => (typeof input === 'string' ? input : input.toString()) === '/auth/login')!;
    expect(JSON.parse(init!.body as string)).toEqual({ username: 'operator', password: 'secret' });
  });

  it('shows an inline error on invalid credentials without navigating away', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => jsonResponse(401, { error: 'invalid credentials' })),
    );

    render(
      <MemoryRouter initialEntries={['/login']}>
        <AuthProvider>
          <LoginPage />
        </AuthProvider>
      </MemoryRouter>,
    );

    await userEvent.type(screen.getByLabelText('Usuário'), 'operator');
    await userEvent.type(screen.getByLabelText('Senha'), 'wrong');
    await userEvent.click(screen.getByRole('button', { name: 'Entrar' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Usuário ou senha inválidos.');
  });
});
