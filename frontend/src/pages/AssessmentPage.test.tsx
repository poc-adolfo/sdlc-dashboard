import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { WorkspaceProvider, useWorkspace } from '../workspace/WorkspaceContext';
import { AssessmentPage } from './AssessmentPage';

function jsonResponse(status: number, body: unknown) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function renderPage() {
  return render(
    <WorkspaceProvider>
      <AssessmentPage />
    </WorkspaceProvider>,
  );
}

// Stands in for Layout's WorkspacePicker changing the shared WorkspaceContext out from under
// AssessmentPage - both consume the same provider in the real app (App.tsx).
function WorkspaceSwitcher({ to }: { to: number }) {
  const { setWorkspaceId } = useWorkspace();
  return (
    <button type="button" onClick={() => setWorkspaceId(to)}>
      Switch workspace
    </button>
  );
}

const DEFAULT_CONTENT = '## Linha de negocio do cliente\n';

describe('AssessmentPage', () => {
  beforeEach(() => {
    localStorage.clear();
    localStorage.setItem('sdlc-dashboard:workspaceId', '7');
  });

  it('prompts to pick a workspace when none is selected', () => {
    localStorage.clear();
    renderPage();

    expect(screen.getByText('Selecione um workspace acima para começar.')).toBeInTheDocument();
  });

  it('searches clients as the operator types and lets them pick an existing one', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (url.startsWith('/clients?q=')) return jsonResponse(200, [{ id: 1, name: 'Acme Corp' }]);
      if (url === '/workspaces/7/assessments' && init?.method === 'POST') {
        const body = JSON.parse(init.body as string);
        expect(body).toEqual({ client_id: 1 });
        return jsonResponse(200, { id: 42, workspaceId: 7, clientId: 1, content: DEFAULT_CONTENT, status: 'em_andamento' });
      }
      throw new Error(`unexpected request: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    renderPage();
    await userEvent.type(screen.getByLabelText('Cliente'), 'Acme');

    const match = await screen.findByRole('button', { name: 'Acme Corp' });
    await userEvent.click(match);

    expect(await screen.findByLabelText('Conteúdo')).toHaveValue(DEFAULT_CONTENT);
    expect(screen.getByText('Acme Corp')).toBeInTheDocument();
  });

  it('offers to create a new client when the typed name has no exact match', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (url.startsWith('/clients?q=')) return jsonResponse(200, []);
      if (url === '/workspaces/7/assessments' && init?.method === 'POST') {
        const body = JSON.parse(init.body as string);
        expect(body).toEqual({ client_name: 'Nova Empresa' });
        return jsonResponse(200, { id: 43, workspaceId: 7, clientId: 2, content: DEFAULT_CONTENT, status: 'em_andamento' });
      }
      throw new Error(`unexpected request: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    renderPage();
    await userEvent.type(screen.getByLabelText('Cliente'), 'Nova Empresa');

    const createOption = await screen.findByRole('button', { name: 'Criar novo cliente: "Nova Empresa"' });
    await userEvent.click(createOption);

    expect(await screen.findByText('Nova Empresa')).toBeInTheDocument();
  });

  it('does not offer to create a client while the search is still pending', async () => {
    let resolveSearch!: (response: Response) => void;
    const pending = new Promise<Response>((resolve) => {
      resolveSearch = resolve;
    });
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (url.startsWith('/clients?q=')) return pending;
      throw new Error(`unexpected request: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    renderPage();
    await userEvent.type(screen.getByLabelText('Cliente'), 'Acme');

    await screen.findByRole('status'); // "Buscando..." - proves the debounced request actually started
    expect(screen.queryByRole('button', { name: /Criar novo cliente/ })).not.toBeInTheDocument();

    resolveSearch(jsonResponse(200, []));
    expect(await screen.findByRole('button', { name: 'Criar novo cliente: "Acme"' })).toBeInTheDocument();
  });

  it('does not offer to create a client when the search itself fails', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (url.startsWith('/clients?q=')) return jsonResponse(500, {});
      throw new Error(`unexpected request: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    renderPage();
    await userEvent.type(screen.getByLabelText('Cliente'), 'Acme');

    expect(await screen.findByRole('alert')).toHaveTextContent('Não foi possível buscar clientes');
    expect(screen.queryByRole('button', { name: /Criar novo cliente/ })).not.toBeInTheDocument();
  });

  it('discards a stale, out-of-order search response instead of overwriting newer results', async () => {
    const deferred: Record<string, { promise: Promise<Response>; resolve: (response: Response) => void }> = {};
    for (const term of ['Ac', 'Acme']) {
      let resolve!: (response: Response) => void;
      const promise = new Promise<Response>((res) => {
        resolve = res;
      });
      deferred[term] = { promise, resolve };
    }

    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (url.startsWith('/clients?q=')) {
        const term = decodeURIComponent(url.slice('/clients?q='.length));
        return deferred[term].promise;
      }
      throw new Error(`unexpected request: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    renderPage();
    const input = screen.getByLabelText('Cliente');

    await userEvent.type(input, 'Ac');
    await waitFor(() => expect(fetchMock).toHaveBeenCalledWith('/clients?q=Ac', expect.anything()));

    await userEvent.type(input, 'me');
    await waitFor(() => expect(fetchMock).toHaveBeenCalledWith('/clients?q=Acme', expect.anything()));

    // The newer request resolves first, as it normally would.
    deferred.Acme.resolve(jsonResponse(200, [{ id: 1, name: 'Acme Corp' }]));
    expect(await screen.findByRole('button', { name: 'Acme Corp' })).toBeInTheDocument();

    // The older, now-stale request resolves after - it must not clobber the newer results. There's no
    // new observable event to await for "this was correctly ignored", so give its promise chain a real
    // tick to run before asserting nothing changed.
    deferred.Ac.resolve(jsonResponse(200, [{ id: 2, name: 'Ac Company' }]));
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(screen.queryByRole('button', { name: 'Ac Company' })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Acme Corp' })).toBeInTheDocument();
  });

  async function selectExistingClientAndLoadAssessment(fetchMock: ReturnType<typeof vi.fn>) {
    renderPage();
    await userEvent.type(screen.getByLabelText('Cliente'), 'Acme');
    await userEvent.click(await screen.findByRole('button', { name: 'Acme Corp' }));
    await screen.findByLabelText('Conteúdo');
    return fetchMock;
  }

  it('saves edited content via the same upsert endpoint, now with assessment_id', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (url.startsWith('/clients?q=')) return jsonResponse(200, [{ id: 1, name: 'Acme Corp' }]);
      if (url === '/workspaces/7/assessments' && init?.method === 'POST') {
        const body = JSON.parse(init.body as string);
        if (body.assessment_id) {
          expect(body).toEqual({ assessment_id: 42, client_id: 1, content: 'texto editado' });
          return jsonResponse(200, { id: 42, workspaceId: 7, clientId: 1, content: 'texto editado', status: 'em_andamento' });
        }
        return jsonResponse(200, { id: 42, workspaceId: 7, clientId: 1, content: DEFAULT_CONTENT, status: 'em_andamento' });
      }
      throw new Error(`unexpected request: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    await selectExistingClientAndLoadAssessment(fetchMock);

    const textarea = screen.getByLabelText('Conteúdo');
    await userEvent.clear(textarea);
    await userEvent.type(textarea, 'texto editado');
    await userEvent.click(screen.getByRole('button', { name: 'Salvar' }));

    await waitFor(() => expect(textarea).toHaveValue('texto editado'));
  });

  it('shows a success message when Concluir succeeds, without calling any Hermes profile', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (url.startsWith('/clients?q=')) return jsonResponse(200, [{ id: 1, name: 'Acme Corp' }]);
      if (url === '/workspaces/7/assessments' && init?.method === 'POST') return jsonResponse(200, { id: 42, workspaceId: 7, clientId: 1, content: DEFAULT_CONTENT, status: 'em_andamento' });
      if (url === '/workspaces/7/assessments/42/concluir') return jsonResponse(200, { concluido: true });
      throw new Error(`unexpected request: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    await selectExistingClientAndLoadAssessment(fetchMock);

    await userEvent.click(screen.getByRole('button', { name: 'Concluir' }));

    expect(await screen.findByText('Assessment concluído.')).toBeInTheDocument();
  });

  it('shows a retry-friendly error when Concluir fails', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (url.startsWith('/clients?q=')) return jsonResponse(200, [{ id: 1, name: 'Acme Corp' }]);
      if (url === '/workspaces/7/assessments' && init?.method === 'POST') return jsonResponse(200, { id: 42, workspaceId: 7, clientId: 1, content: DEFAULT_CONTENT, status: 'em_andamento' });
      if (url === '/workspaces/7/assessments/42/concluir') return jsonResponse(500, {});
      throw new Error(`unexpected request: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    await selectExistingClientAndLoadAssessment(fetchMock);

    await userEvent.click(screen.getByRole('button', { name: 'Concluir' }));

    expect(await screen.findByText('Não foi possível concluir o assessment. Tente novamente.')).toBeInTheDocument();
  });

  it('"trocar" returns to the client picker', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (url.startsWith('/clients?q=')) return jsonResponse(200, [{ id: 1, name: 'Acme Corp' }]);
      if (url === '/workspaces/7/assessments' && init?.method === 'POST') return jsonResponse(200, { id: 42, workspaceId: 7, clientId: 1, content: DEFAULT_CONTENT, status: 'em_andamento' });
      throw new Error(`unexpected request: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    await selectExistingClientAndLoadAssessment(fetchMock);

    await userEvent.click(screen.getByRole('button', { name: 'trocar' }));

    expect(screen.getByLabelText('Cliente')).toBeInTheDocument();
    expect(screen.queryByLabelText('Conteúdo')).not.toBeInTheDocument();
  });

  it('resets the loaded assessment when the workspace changes elsewhere in the app', async () => {
    // QA finding on PR #22: Layout's workspace picker isn't scoped to this page - if it changes
    // workspaceId while an assessment for the old workspace is loaded, Salvar/Concluir must not keep
    // submitting against the stale assessment id/client under the new workspace's URL.
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (url.startsWith('/clients?q=')) return jsonResponse(200, [{ id: 1, name: 'Acme Corp' }]);
      if (url === '/workspaces/7/assessments' && init?.method === 'POST') return jsonResponse(200, { id: 42, workspaceId: 7, clientId: 1, content: DEFAULT_CONTENT, status: 'em_andamento' });
      throw new Error(`unexpected request: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    render(
      <WorkspaceProvider>
        <WorkspaceSwitcher to={8} />
        <AssessmentPage />
      </WorkspaceProvider>,
    );

    await userEvent.type(screen.getByLabelText('Cliente'), 'Acme');
    await userEvent.click(await screen.findByRole('button', { name: 'Acme Corp' }));
    await screen.findByLabelText('Conteúdo');

    await userEvent.click(screen.getByRole('button', { name: 'Switch workspace' }));

    expect(await screen.findByLabelText('Cliente')).toBeInTheDocument();
    expect(screen.queryByLabelText('Conteúdo')).not.toBeInTheDocument();

    // No request for workspace 8 should ever reference the assessment/client that belonged to
    // workspace 7 - proves the state was actually cleared, not just visually hidden.
    const workspace8Calls = fetchMock.mock.calls.filter(([input]) => (typeof input === 'string' ? input : input.toString()).startsWith('/workspaces/8/'));
    expect(workspace8Calls).toHaveLength(0);
  });
});
