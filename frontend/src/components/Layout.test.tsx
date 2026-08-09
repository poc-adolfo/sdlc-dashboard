import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { AuthProvider } from '../auth/AuthContext';
import { WorkspaceProvider } from '../workspace/WorkspaceContext';
import { Layout } from './Layout';

function jsonResponse(status: number, body: unknown) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function renderLayout(initialEntries: string[] = ['/assessment']) {
  return render(
    <MemoryRouter initialEntries={initialEntries}>
      <AuthProvider>
        <WorkspaceProvider>
          <Routes>
            <Route element={<Layout />}>
              <Route path="/assessment" element={<p>Assessment screen</p>} />
              <Route path="/specs" element={<p>Specs screen</p>} />
            </Route>
          </Routes>
        </WorkspaceProvider>
      </AuthProvider>
    </MemoryRouter>,
  );
}

describe('Layout', () => {
  beforeEach(() => {
    localStorage.clear();
    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse(200, [])));
  });

  it('renders the four nav tabs and highlights the one matching the current route', async () => {
    renderLayout(['/assessment']);
    await screen.findByText('Assessment screen');

    for (const label of ['Assessment', 'Specs', 'Dashboard', 'Credenciais']) {
      expect(screen.getByRole('link', { name: label })).toBeInTheDocument();
    }
    expect(screen.getByRole('link', { name: 'Assessment' })).toHaveClass('app-tab--active');
    expect(screen.getByRole('link', { name: 'Specs' })).not.toHaveClass('app-tab--active');
  });

  it('navigates to the clicked tab', async () => {
    renderLayout(['/assessment']);
    await screen.findByText('Assessment screen');

    await userEvent.click(screen.getByRole('link', { name: 'Specs' }));

    expect(await screen.findByText('Specs screen')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Specs' })).toHaveClass('app-tab--active');
  });

  it('the workspace picker persists the entered id for other screens to read', async () => {
    renderLayout();
    await screen.findByText('Assessment screen');

    await userEvent.type(screen.getByLabelText('Workspace'), '42');
    await userEvent.click(screen.getByRole('button', { name: 'Usar' }));

    expect(localStorage.getItem('sdlc-dashboard:workspaceId')).toBe('42');
  });

  it('the workspace picker clears the stored id when submitted blank', async () => {
    localStorage.setItem('sdlc-dashboard:workspaceId', '7');
    renderLayout();
    await screen.findByText('Assessment screen');

    const input = screen.getByLabelText('Workspace');
    expect(input).toHaveValue('7'); // reflects what was already selected
    await userEvent.clear(input);
    await userEvent.click(screen.getByRole('button', { name: 'Usar' }));

    expect(localStorage.getItem('sdlc-dashboard:workspaceId')).toBeNull();
  });

  it('"Sair" calls POST /auth/logout', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (url === '/auth/logout') return jsonResponse(200, {});
      return jsonResponse(200, []);
    });
    vi.stubGlobal('fetch', fetchMock);

    renderLayout();
    await screen.findByText('Assessment screen');

    await userEvent.click(screen.getByRole('button', { name: 'Sair' }));

    await waitFor(() => expect(fetchMock).toHaveBeenCalledWith('/auth/logout', expect.objectContaining({ method: 'POST' })));
  });
});
