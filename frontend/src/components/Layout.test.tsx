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

  it('the workspace picker lists workspaces from GET /workspaces and persists the selected id', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse(200, [
      { id: 1, name: 'Acme' },
      { id: 2, name: 'Beta' },
    ])));
    renderLayout();
    await screen.findByText('Assessment screen');

    const select = await screen.findByLabelText('Workspace');
    await waitFor(() => expect(screen.getByRole('option', { name: 'Beta' })).toBeInTheDocument());

    await userEvent.selectOptions(select, 'Beta');

    expect(localStorage.getItem('sdlc-dashboard:workspaceId')).toBe('2');
  });

  it('the workspace picker preselects a previously stored workspace id', async () => {
    localStorage.setItem('sdlc-dashboard:workspaceId', '7');
    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse(200, [{ id: 7, name: 'Stored Workspace' }])));
    renderLayout();
    await screen.findByText('Assessment screen');

    const select = await screen.findByLabelText('Workspace');
    await waitFor(() => expect(select).toHaveValue('7'));
  });

  it('"Novo" opens a form that creates a workspace and selects it', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (url === '/workspaces' && init?.method === 'POST') {
        return jsonResponse(201, { id: 9, name: 'New Workspace' });
      }
      return jsonResponse(200, []);
    });
    vi.stubGlobal('fetch', fetchMock);
    renderLayout();
    await screen.findByText('Assessment screen');

    await userEvent.click(screen.getByRole('button', { name: 'Novo' }));
    await userEvent.type(screen.getByLabelText('Nome'), 'New Workspace');
    await userEvent.type(screen.getByLabelText('Repositório/Projeto'), 'acme/new-workspace');
    await userEvent.click(screen.getByRole('button', { name: 'Criar' }));

    await waitFor(() => expect(localStorage.getItem('sdlc-dashboard:workspaceId')).toBe('9'));
    expect(screen.getByLabelText('Workspace')).toHaveValue('9');
    expect(screen.queryByLabelText('Nome')).not.toBeInTheDocument(); // form closes after creating
  });

  it('"Cancelar" closes the new-workspace form without creating anything', async () => {
    renderLayout();
    await screen.findByText('Assessment screen');

    await userEvent.click(screen.getByRole('button', { name: 'Novo' }));
    await userEvent.click(screen.getByRole('button', { name: 'Cancelar' }));

    expect(screen.queryByLabelText('Nome')).not.toBeInTheDocument();
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
