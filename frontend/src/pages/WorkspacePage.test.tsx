import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { WorkspaceProvider, WorkspaceListProvider, useWorkspace } from '../workspace/WorkspaceContext';
import { WorkspacePage } from './WorkspacePage';

function jsonResponse(status: number, body: unknown) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function renderPage() {
  return render(
    <WorkspaceProvider>
      <WorkspaceListProvider>
        <WorkspacePage />
      </WorkspaceListProvider>
    </WorkspaceProvider>,
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

describe('WorkspacePage - creating/editing the workspace', () => {
  beforeEach(() => {
    localStorage.clear();
  });

  it('shows a blank create form and no Assessment/Credenciais sections when no workspace is selected', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse(200, [])));
    renderPage();

    expect(await screen.findByLabelText('Nome')).toHaveValue('');
    expect(screen.getByRole('button', { name: 'Criar workspace' })).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Assessment' })).not.toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Credenciais' })).not.toBeInTheDocument();
  });

  it('creates a workspace and reveals the Assessment/Credenciais sections for it', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (url === '/workspaces' && init?.method === 'POST') {
        const body = JSON.parse(init.body as string);
        expect(body).toEqual({ name: 'Acme Platform', platform: 'github', platform_ref: 'acme/platform' });
        return jsonResponse(201, WORKSPACE);
      }
      if (url === '/workspaces') return jsonResponse(200, [WORKSPACE]);
      if (url === `/workspaces/${WORKSPACE.id}`) return jsonResponse(200, WORKSPACE);
      if (url === `/workspaces/${WORKSPACE.id}/credenciais`) return jsonResponse(200, []);
      throw new Error(`unexpected request: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    renderPage();
    await screen.findByLabelText('Nome');

    await userEvent.type(screen.getByLabelText('Nome'), 'Acme Platform');
    await userEvent.type(screen.getByLabelText('Repositório/Projeto'), 'acme/platform');
    await userEvent.click(screen.getByRole('button', { name: 'Criar workspace' }));

    expect(await screen.findByRole('heading', { name: 'Assessment' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Credenciais' })).toBeInTheDocument();
    expect(screen.getByLabelText('Nome')).toHaveValue('Acme Platform'); // now in edit mode, reloaded from the server
  });

  it('loads and edits an already-selected workspace via PATCH', async () => {
    localStorage.setItem('sdlc-dashboard:workspaceId', String(WORKSPACE.id));
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (url === `/workspaces/${WORKSPACE.id}` && init?.method === 'PATCH') {
        const body = JSON.parse(init.body as string);
        expect(body).toEqual({ name: 'Renamed', platform: 'github', platform_ref: 'acme/platform' });
        return jsonResponse(200, { ...WORKSPACE, name: 'Renamed' });
      }
      if (url === `/workspaces/${WORKSPACE.id}`) return jsonResponse(200, WORKSPACE);
      if (url === '/workspaces') return jsonResponse(200, [WORKSPACE]);
      if (url === `/workspaces/${WORKSPACE.id}/credenciais`) return jsonResponse(200, []);
      throw new Error(`unexpected request: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    renderPage();

    const nameInput = await screen.findByLabelText('Nome');
    await waitFor(() => expect(nameInput).toHaveValue('Acme Platform'));
    await userEvent.clear(nameInput);
    await userEvent.type(nameInput, 'Renamed');
    await userEvent.click(screen.getByRole('button', { name: 'Salvar' }));

    expect(await screen.findByText('Workspace atualizado.')).toBeInTheDocument();
  });

  it('surfaces the platform-locked 409 with an explanatory message', async () => {
    localStorage.setItem('sdlc-dashboard:workspaceId', String(WORKSPACE.id));
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (url === `/workspaces/${WORKSPACE.id}` && init?.method === 'PATCH') return jsonResponse(409, { error: 'locked' });
      if (url === `/workspaces/${WORKSPACE.id}`) return jsonResponse(200, WORKSPACE);
      if (url === '/workspaces') return jsonResponse(200, [WORKSPACE]);
      if (url === `/workspaces/${WORKSPACE.id}/credenciais`) return jsonResponse(200, []);
      throw new Error(`unexpected request: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    renderPage();
    await waitFor(async () => expect(await screen.findByLabelText('Nome')).toHaveValue('Acme Platform'));

    await userEvent.click(screen.getByRole('button', { name: 'Salvar' }));

    expect(await screen.findByText('Plataforma e repositório não podem ser alterados depois que o ciclo já começou para este workspace.')).toBeInTheDocument();
  });

  it('the workspace section is a collapsible accordion, open by default, showing the name once collapsed', async () => {
    localStorage.setItem('sdlc-dashboard:workspaceId', String(WORKSPACE.id));
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (url === `/workspaces/${WORKSPACE.id}`) return jsonResponse(200, WORKSPACE);
      if (url === '/workspaces') return jsonResponse(200, [WORKSPACE]);
      if (url === `/workspaces/${WORKSPACE.id}/credenciais`) return jsonResponse(200, []);
      throw new Error(`unexpected request: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    renderPage();
    await waitFor(async () => expect(await screen.findByLabelText('Nome')).toHaveValue('Acme Platform'));

    const summary = screen.getByText('Acme Platform', { selector: 'summary' });
    await userEvent.click(summary);

    // jsdom keeps a closed <details>'s children in the DOM (it doesn't run layout/CSS), so
    // "collapsed" here means not visible, not absent - toBeInTheDocument would pass either way.
    expect(screen.getByLabelText('Nome')).not.toBeVisible();
    expect(screen.getByText('Acme Platform', { selector: 'summary' })).toBeVisible(); // the summary itself, still visible collapsed

    await userEvent.click(summary);
    expect(await screen.findByLabelText('Nome')).toHaveValue('Acme Platform');
  });
});

describe('WorkspacePage - Assessment section', () => {
  beforeEach(() => {
    localStorage.clear();
    localStorage.setItem('sdlc-dashboard:workspaceId', String(WORKSPACE.id));
  });

  function withWorkspaceHandlers(extra: (url: string, init?: RequestInit) => Response | Promise<Response> | undefined) {
    return vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (url === `/workspaces/${WORKSPACE.id}`) return jsonResponse(200, WORKSPACE);
      if (url === '/workspaces') return jsonResponse(200, [WORKSPACE]);
      if (url === `/workspaces/${WORKSPACE.id}/credenciais`) return jsonResponse(200, []);
      const result = await extra(url, init);
      if (result) return result;
      throw new Error(`unexpected request: ${url}`);
    });
  }

  it('searches clients as the operator types and lets them pick an existing one', async () => {
    const fetchMock = withWorkspaceHandlers((url, init) => {
      if (url.startsWith('/clients?q=')) return jsonResponse(200, [{ id: 1, name: 'Acme Corp' }]);
      if (url === `/workspaces/${WORKSPACE.id}/assessments` && init?.method === 'POST') {
        const body = JSON.parse(init.body as string);
        expect(body).toEqual({ client_id: 1 });
        return jsonResponse(200, { id: 42, workspaceId: WORKSPACE.id, clientId: 1, content: DEFAULT_CONTENT, status: 'em_andamento' });
      }
      return undefined;
    });
    vi.stubGlobal('fetch', fetchMock);

    renderPage();
    await userEvent.type(await screen.findByLabelText('Cliente'), 'Acme');

    const match = await screen.findByRole('button', { name: 'Acme Corp' });
    await userEvent.click(match);

    expect(await screen.findByLabelText('Conteúdo')).toHaveValue(DEFAULT_CONTENT);
  });

  it('offers to create a new client when the typed name has no exact match', async () => {
    const fetchMock = withWorkspaceHandlers((url, init) => {
      if (url.startsWith('/clients?q=')) return jsonResponse(200, []);
      if (url === `/workspaces/${WORKSPACE.id}/assessments` && init?.method === 'POST') {
        const body = JSON.parse(init.body as string);
        expect(body).toEqual({ client_name: 'Nova Empresa' });
        return jsonResponse(200, { id: 43, workspaceId: WORKSPACE.id, clientId: 2, content: DEFAULT_CONTENT, status: 'em_andamento' });
      }
      return undefined;
    });
    vi.stubGlobal('fetch', fetchMock);

    renderPage();
    await userEvent.type(await screen.findByLabelText('Cliente'), 'Nova Empresa');

    const createOption = await screen.findByRole('button', { name: 'Criar novo cliente: "Nova Empresa"' });
    await userEvent.click(createOption);

    expect(await screen.findByText('Nova Empresa')).toBeInTheDocument();
  });

  async function selectExistingClientAndLoadAssessment() {
    await userEvent.type(await screen.findByLabelText('Cliente'), 'Acme');
    await userEvent.click(await screen.findByRole('button', { name: 'Acme Corp' }));
    await screen.findByLabelText('Conteúdo');
  }

  it('shows a success message when Concluir succeeds, without calling any Hermes profile', async () => {
    const fetchMock = withWorkspaceHandlers((url, init) => {
      if (url.startsWith('/clients?q=')) return jsonResponse(200, [{ id: 1, name: 'Acme Corp' }]);
      if (url === `/workspaces/${WORKSPACE.id}/assessments` && init?.method === 'POST') return jsonResponse(200, { id: 42, workspaceId: WORKSPACE.id, clientId: 1, content: DEFAULT_CONTENT, status: 'em_andamento' });
      if (url === `/workspaces/${WORKSPACE.id}/assessments/42/concluir`) return jsonResponse(200, { concluido: true });
      return undefined;
    });
    vi.stubGlobal('fetch', fetchMock);
    renderPage();
    await selectExistingClientAndLoadAssessment();

    await userEvent.click(screen.getByRole('button', { name: 'Concluir' }));

    expect(await screen.findByText('Assessment concluído.')).toBeInTheDocument();
  });

  it('"trocar" returns to the client picker', async () => {
    const fetchMock = withWorkspaceHandlers((url, init) => {
      if (url.startsWith('/clients?q=')) return jsonResponse(200, [{ id: 1, name: 'Acme Corp' }]);
      if (url === `/workspaces/${WORKSPACE.id}/assessments` && init?.method === 'POST') return jsonResponse(200, { id: 42, workspaceId: WORKSPACE.id, clientId: 1, content: DEFAULT_CONTENT, status: 'em_andamento' });
      return undefined;
    });
    vi.stubGlobal('fetch', fetchMock);
    renderPage();
    await selectExistingClientAndLoadAssessment();

    await userEvent.click(screen.getByRole('button', { name: 'trocar' }));

    expect(screen.getByLabelText('Cliente')).toBeInTheDocument();
    expect(screen.queryByLabelText('Conteúdo')).not.toBeInTheDocument();
  });

  it('resets the loaded assessment when the workspace changes elsewhere in the app', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (url.startsWith('/clients?q=')) return jsonResponse(200, [{ id: 1, name: 'Acme Corp' }]);
      if (url === `/workspaces/${WORKSPACE.id}/assessments` && init?.method === 'POST') return jsonResponse(200, { id: 42, workspaceId: WORKSPACE.id, clientId: 1, content: DEFAULT_CONTENT, status: 'em_andamento' });
      if (url === '/workspaces') return jsonResponse(200, [WORKSPACE, { ...WORKSPACE, id: 8, name: 'Other' }]);
      if (url === `/workspaces/${WORKSPACE.id}`) return jsonResponse(200, WORKSPACE);
      if (url === '/workspaces/8') return jsonResponse(200, { ...WORKSPACE, id: 8, name: 'Other' });
      if (url === '/workspaces/8/credenciais') return jsonResponse(200, []);
      throw new Error(`unexpected request: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    render(
      <WorkspaceProvider>
        <WorkspaceListProvider>
          <WorkspaceSwitcher to={8} />
          <WorkspacePage />
        </WorkspaceListProvider>
      </WorkspaceProvider>,
    );
    await selectExistingClientAndLoadAssessment();

    await userEvent.click(screen.getByRole('button', { name: 'Switch workspace' }));

    expect(await screen.findByLabelText('Cliente')).toBeInTheDocument();
    expect(screen.queryByLabelText('Conteúdo')).not.toBeInTheDocument();

    const workspace8AssessmentCalls = fetchMock.mock.calls.filter(([input]) => (typeof input === 'string' ? input : input.toString()) === '/workspaces/8/assessments');
    expect(workspace8AssessmentCalls).toHaveLength(0);
  });
});

describe('WorkspacePage - Credenciais section', () => {
  beforeEach(() => {
    localStorage.clear();
    localStorage.setItem('sdlc-dashboard:workspaceId', String(WORKSPACE.id));
  });

  function withWorkspaceHandlers(extra: (url: string, init?: RequestInit) => Response | Promise<Response> | undefined) {
    return vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (url === `/workspaces/${WORKSPACE.id}`) return jsonResponse(200, WORKSPACE);
      if (url === '/workspaces') return jsonResponse(200, [WORKSPACE]);
      const result = await extra(url, init);
      if (result) return result;
      throw new Error(`unexpected request: ${url}`);
    });
  }

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
    // Two "Salvar" buttons are on screen at once (this inline form's, and the workspace details form
    // above it) - scope the click to this row.
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
