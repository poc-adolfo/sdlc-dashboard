import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { WorkspaceProvider, WorkspaceListProvider, useWorkspace } from '../workspace/WorkspaceContext';
import { WorkspacePage } from './WorkspacePage';

function jsonResponse(status: number, body: unknown) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

// Every test exercises the same "reached /specs" outcome for a successful Concluir, so the app needs a
// real route to land on - WorkspacePage alone has no way to show that a navigate('/specs') happened.
function renderPage() {
  return render(
    <MemoryRouter initialEntries={['/workspace']}>
      <WorkspaceProvider>
        <WorkspaceListProvider>
          <Routes>
            <Route path="/workspace" element={<WorkspacePage />} />
            <Route path="/specs" element={<p>Specs screen</p>} />
          </Routes>
        </WorkspaceListProvider>
      </WorkspaceProvider>
    </MemoryRouter>,
  );
}

// Stands in for Layout's WorkspacePicker changing the shared WorkspaceContext out from under
// WorkspacePage - both consume the same provider in the real app (App.tsx).
function WorkspaceSwitcher({ to }: { to: number }) {
  const { setWorkspaceId } = useWorkspace();
  return (
    <button type="button" onClick={() => setWorkspaceId(to)}>
      Switch workspace
    </button>
  );
}

const WORKSPACE = { id: 7, name: 'Acme Platform', slug: 'acme-platform', platform: 'github', platformRef: 'acme/platform', clientId: null, status: 'active', createdAt: '2026-08-05T00:00:00Z' };
const DEFAULT_CONTENT = '## Linha de negocio do cliente\n';

// Shared by every describe block below: WorkspaceSection always fetches the workspace's own fields plus
// its in-progress assessment (for prefill) as soon as a workspaceId exists, and CredentialsSection always
// fetches the credentials list - tests that don't care about one of these still need it stubbed or every
// request they didn't anticipate throws "unexpected request".
function withWorkspaceHandlers(extra: (url: string, init?: RequestInit) => Response | Promise<Response> | undefined) {
  return vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === 'string' ? input : input.toString();
    const result = await extra(url, init);
    if (result) return result;
    if (url === `/workspaces/${WORKSPACE.id}`) return jsonResponse(200, WORKSPACE);
    if (url === `/workspaces/${WORKSPACE.id}/assessments/current`) return jsonResponse(404, {});
    if (url === `/workspaces/${WORKSPACE.id}/credenciais`) return jsonResponse(200, []);
    if (url === '/workspaces') return jsonResponse(200, [WORKSPACE]);
    throw new Error(`unexpected request: ${url}`);
  });
}

describe('WorkspacePage - Workspace section (identidade + cliente + assessment)', () => {
  beforeEach(() => {
    localStorage.clear();
  });

  it('shows a blank form with Concluir disabled, and no Credenciais section, when no workspace is selected', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse(200, [])));
    renderPage();

    expect(await screen.findByLabelText('Nome')).toHaveValue('');
    expect(screen.getByLabelText('Cliente')).toBeInTheDocument();
    expect(screen.getByLabelText('Conteúdo')).toHaveValue('');
    expect(screen.getByRole('button', { name: 'Concluir' })).toBeDisabled();
    expect(screen.queryByRole('heading', { name: 'Credenciais' })).not.toBeInTheDocument();
  });

  it('fills every field and Concluir creates the workspace, saves the assessment, concludes it, and navigates to specs', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (url === '/workspaces' && init?.method === 'POST') {
        const body = JSON.parse(init.body as string);
        expect(body).toEqual({ name: 'Acme Platform', platform: 'github', platform_ref: 'acme/platform' });
        return jsonResponse(201, WORKSPACE);
      }
      if (url === '/workspaces') return jsonResponse(200, []);
      if (url === `/workspaces/${WORKSPACE.id}`) return jsonResponse(200, WORKSPACE);
      if (url === `/workspaces/${WORKSPACE.id}/assessments/current`) return jsonResponse(404, {});
      if (url.startsWith('/clients?q=')) return jsonResponse(200, [{ id: 1, name: 'Acme Corp' }]);
      if (url === `/workspaces/${WORKSPACE.id}/assessments` && init?.method === 'POST') {
        const body = JSON.parse(init.body as string);
        expect(body).toEqual({ client_id: 1, content: 'Cliente vende sapatos.' });
        return jsonResponse(200, { id: 42, workspaceId: WORKSPACE.id, clientId: 1, content: 'Cliente vende sapatos.', status: 'em_andamento' });
      }
      if (url === `/workspaces/${WORKSPACE.id}/assessments/42/concluir`) return jsonResponse(200, { concluido: true });
      throw new Error(`unexpected request: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    renderPage();
    await screen.findByLabelText('Nome');

    await userEvent.type(screen.getByLabelText('Nome'), 'Acme Platform');
    await userEvent.type(screen.getByLabelText('Repositório/Projeto'), 'acme/platform');
    await userEvent.type(screen.getByLabelText('Cliente'), 'Acme');
    await userEvent.click(await screen.findByRole('button', { name: 'Acme Corp' }));
    await userEvent.clear(screen.getByLabelText('Conteúdo'));
    await userEvent.type(screen.getByLabelText('Conteúdo'), 'Cliente vende sapatos.');

    const concluirButton = screen.getByRole('button', { name: 'Concluir' });
    expect(concluirButton).toBeEnabled();
    await userEvent.click(concluirButton);

    expect(await screen.findByText('Specs screen')).toBeInTheDocument();
  });

  it('loads an existing workspace plus its in-progress assessment, and Concluir re-saves everything via PATCH', async () => {
    localStorage.setItem('sdlc-dashboard:workspaceId', String(WORKSPACE.id));
    const fetchMock = withWorkspaceHandlers((url, init) => {
      if (url === `/workspaces/${WORKSPACE.id}` && init?.method === 'PATCH') {
        const body = JSON.parse(init.body as string);
        expect(body).toEqual({ name: 'Renamed', platform: 'github', platform_ref: 'acme/platform' });
        return jsonResponse(200, { ...WORKSPACE, name: 'Renamed' });
      }
      if (url === `/workspaces/${WORKSPACE.id}/assessments/current`) {
        return jsonResponse(200, { id: 42, workspaceId: WORKSPACE.id, clientId: 1, clientName: 'Acme Corp', content: DEFAULT_CONTENT, status: 'em_andamento' });
      }
      if (url === `/workspaces/${WORKSPACE.id}/assessments` && init?.method === 'POST') {
        const body = JSON.parse(init.body as string);
        expect(body).toEqual({ client_id: 1, content: DEFAULT_CONTENT });
        return jsonResponse(200, { id: 42, workspaceId: WORKSPACE.id, clientId: 1, content: DEFAULT_CONTENT, status: 'em_andamento' });
      }
      if (url === `/workspaces/${WORKSPACE.id}/assessments/42/concluir`) return jsonResponse(200, { concluido: true });
      return undefined;
    });
    vi.stubGlobal('fetch', fetchMock);
    renderPage();

    const nameInput = await screen.findByLabelText('Nome');
    await waitFor(() => expect(nameInput).toHaveValue('Acme Platform'));
    expect(await screen.findByText('Acme Corp')).toBeInTheDocument();
    expect(screen.getByLabelText('Conteúdo')).toHaveValue(DEFAULT_CONTENT);

    await userEvent.clear(nameInput);
    await userEvent.type(nameInput, 'Renamed');
    expect(screen.getByRole('button', { name: 'Concluir' })).toBeEnabled();
    await userEvent.click(screen.getByRole('button', { name: 'Concluir' }));

    expect(await screen.findByText('Specs screen')).toBeInTheDocument();
  });

  it('surfaces the platform-locked 409 from Concluir with an explanatory message, without navigating', async () => {
    localStorage.setItem('sdlc-dashboard:workspaceId', String(WORKSPACE.id));
    const fetchMock = withWorkspaceHandlers((url, init) => {
      if (url === `/workspaces/${WORKSPACE.id}` && init?.method === 'PATCH') return jsonResponse(409, { error: 'locked' });
      if (url === `/workspaces/${WORKSPACE.id}/assessments/current`) {
        return jsonResponse(200, { id: 42, workspaceId: WORKSPACE.id, clientId: 1, clientName: 'Acme Corp', content: DEFAULT_CONTENT, status: 'em_andamento' });
      }
      return undefined;
    });
    vi.stubGlobal('fetch', fetchMock);
    renderPage();
    await waitFor(async () => expect(await screen.findByLabelText('Nome')).toHaveValue('Acme Platform'));

    await userEvent.click(screen.getByRole('button', { name: 'Concluir' }));

    expect(await screen.findByText('Plataforma e repositório não podem ser alterados depois que o ciclo já começou para este workspace.')).toBeInTheDocument();
    expect(screen.queryByText('Specs screen')).not.toBeInTheDocument();
  });

  it('the workspace section is a collapsible accordion, open by default, showing the name once collapsed', async () => {
    localStorage.setItem('sdlc-dashboard:workspaceId', String(WORKSPACE.id));
    const fetchMock = withWorkspaceHandlers(() => undefined);
    vi.stubGlobal('fetch', fetchMock);
    renderPage();
    await waitFor(async () => expect(await screen.findByLabelText('Nome')).toHaveValue('Acme Platform'));

    const summaryTitle = screen.getByText('Acme Platform', { selector: 'summary span' });
    await userEvent.click(summaryTitle);

    // jsdom keeps a closed <details>'s children in the DOM (it doesn't run layout/CSS), so
    // "collapsed" here means not visible, not absent - toBeInTheDocument would pass either way.
    expect(screen.getByLabelText('Nome')).not.toBeVisible();
    expect(summaryTitle).toBeVisible(); // the summary itself, still visible collapsed
    expect(screen.getByRole('button', { name: 'Concluir' })).toBeVisible(); // Concluir lives in the summary bar too

    await userEvent.click(summaryTitle);
    expect(await screen.findByLabelText('Nome')).toHaveValue('Acme Platform');
  });

  it('searches clients as the operator types and lets them pick an existing one', async () => {
    localStorage.setItem('sdlc-dashboard:workspaceId', String(WORKSPACE.id));
    const fetchMock = withWorkspaceHandlers((url) => {
      if (url.startsWith('/clients?q=')) return jsonResponse(200, [{ id: 1, name: 'Acme Corp' }]);
      return undefined;
    });
    vi.stubGlobal('fetch', fetchMock);
    renderPage();
    await userEvent.type(await screen.findByLabelText('Cliente'), 'Acme');

    const match = await screen.findByRole('button', { name: 'Acme Corp' });
    await userEvent.click(match);

    expect(await screen.findByText('Acme Corp')).toBeInTheDocument();
    expect(screen.queryByLabelText('Cliente')).not.toBeInTheDocument();
  });

  it('offers to create a new client when the typed name has no exact match', async () => {
    localStorage.setItem('sdlc-dashboard:workspaceId', String(WORKSPACE.id));
    const fetchMock = withWorkspaceHandlers((url) => {
      if (url.startsWith('/clients?q=')) return jsonResponse(200, []);
      return undefined;
    });
    vi.stubGlobal('fetch', fetchMock);
    renderPage();
    await userEvent.type(await screen.findByLabelText('Cliente'), 'Nova Empresa');

    const createOption = await screen.findByRole('button', { name: 'Criar novo cliente: "Nova Empresa"' });
    await userEvent.click(createOption);

    expect(await screen.findByText('Nova Empresa')).toBeInTheDocument();
  });

  it('"trocar" returns to the client search', async () => {
    localStorage.setItem('sdlc-dashboard:workspaceId', String(WORKSPACE.id));
    const fetchMock = withWorkspaceHandlers((url) => {
      if (url.startsWith('/clients?q=')) return jsonResponse(200, [{ id: 1, name: 'Acme Corp' }]);
      return undefined;
    });
    vi.stubGlobal('fetch', fetchMock);
    renderPage();
    await userEvent.type(await screen.findByLabelText('Cliente'), 'Acme');
    await userEvent.click(await screen.findByRole('button', { name: 'Acme Corp' }));
    await screen.findByText('Acme Corp');

    await userEvent.click(screen.getByRole('button', { name: 'trocar' }));

    expect(screen.getByLabelText('Cliente')).toBeInTheDocument();
  });

  it('resets the loaded workspace/assessment state when the workspace changes elsewhere in the app', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (url === '/workspaces') return jsonResponse(200, [WORKSPACE, { ...WORKSPACE, id: 8, name: 'Other' }]);
      if (url === `/workspaces/${WORKSPACE.id}`) return jsonResponse(200, WORKSPACE);
      if (url === `/workspaces/${WORKSPACE.id}/assessments/current`) {
        return jsonResponse(200, { id: 42, workspaceId: WORKSPACE.id, clientId: 1, clientName: 'Acme Corp', content: DEFAULT_CONTENT, status: 'em_andamento' });
      }
      if (url === '/workspaces/8') return jsonResponse(200, { ...WORKSPACE, id: 8, name: 'Other' });
      if (url === '/workspaces/8/assessments/current') return jsonResponse(404, {});
      if (url === '/workspaces/8/credenciais') return jsonResponse(200, []);
      throw new Error(`unexpected request: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    localStorage.setItem('sdlc-dashboard:workspaceId', String(WORKSPACE.id));

    render(
      <MemoryRouter initialEntries={['/workspace']}>
        <WorkspaceProvider>
          <WorkspaceListProvider>
            <WorkspaceSwitcher to={8} />
            <Routes>
              <Route path="/workspace" element={<WorkspacePage />} />
              <Route path="/specs" element={<p>Specs screen</p>} />
            </Routes>
          </WorkspaceListProvider>
        </WorkspaceProvider>
      </MemoryRouter>,
    );
    await screen.findByText('Acme Corp');

    await userEvent.click(screen.getByRole('button', { name: 'Switch workspace' }));

    await waitFor(() => expect(screen.getByLabelText('Nome')).toHaveValue('Other'));
    expect(screen.getByLabelText('Cliente')).toBeInTheDocument(); // back to the search field - no client carried over
  });
});

describe('WorkspacePage - Credenciais section', () => {
  beforeEach(() => {
    localStorage.clear();
    localStorage.setItem('sdlc-dashboard:workspaceId', String(WORKSPACE.id));
  });

  it('lists all 7 perfis, marking which ones are cadastrados and which are not', async () => {
    const fetchMock = withWorkspaceHandlers((url) => {
      if (url === `/workspaces/${WORKSPACE.id}/credenciais`) {
        return jsonResponse(200, [
          { id: 1, perfil: 'dev', platformUsername: 'recolocarme-web', scopes: 'Contents:RW', status: 'active', createdAt: '2026-08-05T00:00:00Z', rotatedAt: null },
        ]);
      }
      return undefined;
    });
    vi.stubGlobal('fetch', fetchMock);
    renderPage();

    expect(await screen.findByText('Dev')).toBeInTheDocument();
    expect(screen.getByText('recolocarme-web · Ativa')).toBeInTheDocument();
    expect(screen.getByText('Escopos: Contents:RW')).toBeInTheDocument();
    expect(screen.getByText('Analista de Requisitos')).toBeInTheDocument();
    expect(screen.getAllByText('Não cadastrado')).toHaveLength(6); // the other 6 perfis
  });

  it('cadastra uma credencial pelo botão da linha e atualiza a lista', async () => {
    let created = false;
    const fetchMock = withWorkspaceHandlers((url, init) => {
      if (url === `/workspaces/${WORKSPACE.id}/credenciais` && init?.method === 'POST') {
        const body = JSON.parse(init.body as string);
        expect(body).toEqual({ perfil: 'dev', platform_username: 'recolocarme-web', token: 'super-secret-token' });
        created = true;
        return jsonResponse(201, {});
      }
      if (url === `/workspaces/${WORKSPACE.id}/credenciais`) {
        return jsonResponse(200, created ? [{ id: 1, perfil: 'dev', platformUsername: 'recolocarme-web', scopes: null, status: 'active', createdAt: '2026-08-05T00:00:00Z', rotatedAt: null }] : []);
      }
      return undefined;
    });
    vi.stubGlobal('fetch', fetchMock);
    renderPage();
    await screen.findByText('Dev');

    const devRow = screen.getByText('Dev').closest('li')!;
    await userEvent.click(within(devRow).getByRole('button', { name: 'Cadastrar' }));
    await userEvent.type(screen.getByLabelText('Usuário na plataforma'), 'recolocarme-web');
    await userEvent.type(screen.getByLabelText('Token'), 'super-secret-token');
    await userEvent.click(within(devRow).getByRole('button', { name: 'Salvar' }));

    expect(await screen.findByText('Credencial para Dev cadastrada.')).toBeInTheDocument();
    expect(await screen.findByText('recolocarme-web · Ativa')).toBeInTheDocument();
  });

  it('"Cancelar" closes the inline form without submitting', async () => {
    const fetchMock = withWorkspaceHandlers((url) => (url === `/workspaces/${WORKSPACE.id}/credenciais` ? jsonResponse(200, []) : undefined));
    vi.stubGlobal('fetch', fetchMock);
    renderPage();
    await screen.findByText('Dev');

    const devRow = screen.getByText('Dev').closest('li')!;
    await userEvent.click(within(devRow).getByRole('button', { name: 'Cadastrar' }));
    await userEvent.click(screen.getByRole('button', { name: 'Cancelar' }));

    expect(screen.queryByLabelText('Usuário na plataforma')).not.toBeInTheDocument();
    expect(within(devRow).getByRole('button', { name: 'Cadastrar' })).toBeInTheDocument();
  });

  it('shows a retry message on a concurrent registration conflict (409)', async () => {
    const fetchMock = withWorkspaceHandlers((url, init) => {
      if (url === `/workspaces/${WORKSPACE.id}/credenciais` && init?.method === 'POST') return jsonResponse(409, { error: 'conflict' });
      if (url === `/workspaces/${WORKSPACE.id}/credenciais`) return jsonResponse(200, []);
      return undefined;
    });
    vi.stubGlobal('fetch', fetchMock);
    renderPage();
    await screen.findByText('Dev');

    const devRow = screen.getByText('Dev').closest('li')!;
    await userEvent.click(within(devRow).getByRole('button', { name: 'Cadastrar' }));
    await userEvent.type(screen.getByLabelText('Usuário na plataforma'), 'recolocarme-web');
    await userEvent.type(screen.getByLabelText('Token'), 'x');
    await userEvent.click(within(devRow).getByRole('button', { name: 'Salvar' }));

    expect(await screen.findByText('Já existe um cadastro concorrente para este perfil. Tente novamente.')).toBeInTheDocument();
  });

  it('shows a retryable error when the list fails to load', async () => {
    const fetchMock = withWorkspaceHandlers((url) => (url === `/workspaces/${WORKSPACE.id}/credenciais` ? jsonResponse(500, {}) : undefined));
    vi.stubGlobal('fetch', fetchMock);
    renderPage();

    expect(await screen.findByText('Não foi possível carregar as credenciais. Tente novamente.')).toBeInTheDocument();
  });
});
