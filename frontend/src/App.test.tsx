import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { App } from './App';

function jsonResponse(status: number, body: unknown) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

// No workspaceId is ever seeded here - each page's own data-fetching short-circuits on
// "nenhum workspace selecionado" (already covered per-page), so the only network call these tests
// need to account for is AuthProvider's session probe.
function renderApp(initialPath: string) {
  return render(
    <MemoryRouter initialEntries={[initialPath]}>
      <App />
    </MemoryRouter>,
  );
}

describe('App routing', () => {
  beforeEach(() => {
    localStorage.clear();
  });

  it('redirects to /login when the session probe comes back unauthorized', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse(401, {})));

    renderApp('/');

    expect(await screen.findByRole('heading', { name: 'sdlc-dashboard' })).toBeInTheDocument();
  });

  it('redirects "/" to /workspace once authenticated, and lets the operator move between the three tabs', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse(200, [])));

    renderApp('/');

    expect(await screen.findByRole('heading', { name: 'Workspace' })).toBeInTheDocument();

    await userEvent.click(screen.getByRole('link', { name: 'Specs' }));
    expect(await screen.findByRole('heading', { name: 'Specs' })).toBeInTheDocument();

    await userEvent.click(screen.getByRole('link', { name: 'Dashboard' }));
    expect(await screen.findByRole('heading', { name: 'Dashboard' })).toBeInTheDocument();

    await userEvent.click(screen.getByRole('link', { name: 'Workspace' }));
    expect(await screen.findByRole('heading', { name: 'Workspace' })).toBeInTheDocument();
  });

  it('redirects an unknown path back into the app instead of a blank/broken screen', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse(200, [])));

    renderApp('/does-not-exist');

    expect(await screen.findByRole('heading', { name: 'Workspace' })).toBeInTheDocument();
  });

  it('deep-linking to a protected route while unauthenticated still lands on /login, not a blank page', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse(401, {})));

    renderApp('/dashboard');

    expect(await screen.findByRole('heading', { name: 'sdlc-dashboard' })).toBeInTheDocument();
  });
});
