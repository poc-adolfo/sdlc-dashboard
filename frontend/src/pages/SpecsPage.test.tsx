import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { WorkspaceProvider, useWorkspace } from '../workspace/WorkspaceContext';
import { SpecsPage } from './SpecsPage';

function jsonResponse(status: number, body: unknown) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function textResponse(status: number, body: string) {
  return new Response(body, { status, headers: { 'Content-Type': 'text/markdown; charset=utf-8' } });
}

function renderPage() {
  return render(
    <WorkspaceProvider>
      <SpecsPage />
    </WorkspaceProvider>,
  );
}

function WorkspaceSwitcher({ to }: { to: number }) {
  const { setWorkspaceId } = useWorkspace();
  return (
    <button type="button" onClick={() => setWorkspaceId(to)}>
      Switch workspace
    </button>
  );
}

const SPEC_CONTENT = '# Checkout\n\n> Status: rascunho (2026-08-05).\n\nConteudo.\n';
const SPEC_ITEM = { fileName: 'checkout.md', title: 'Checkout', status: 'rascunho', version: 1, updatedAt: '2026-08-05T00:00:00Z' };

async function openProjectAndSpec(fetchMock: ReturnType<typeof vi.fn>) {
  renderPage();
  await userEvent.click(await screen.findByRole('button', { name: 'Abrir' }));
  await userEvent.click(await screen.findByRole('button', { name: 'Abrir' }));
  await screen.findByLabelText('Conteúdo');
  return fetchMock;
}

describe('SpecsPage', () => {
  beforeEach(() => {
    localStorage.clear();
    localStorage.setItem('sdlc-dashboard:workspaceId', '7');
  });

  it('prompts to pick a workspace when none is selected', () => {
    localStorage.clear();
    renderPage();

    expect(screen.getByText('Selecione um workspace acima para começar.')).toBeInTheDocument();
  });

  it('asks to conclude the assessment first when the workspace has no client_id yet', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse(200, { clientId: null })));

    renderPage();

    expect(await screen.findByText('Conclua o assessment deste workspace antes de acessar as specs (seção "Workspace").')).toBeInTheDocument();
  });

  it('lists projects for the workspace and lets the operator open one', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (url === '/workspaces/7') return jsonResponse(200, { clientId: 1 });
      if (url === '/workspaces/7/spec-projects') return jsonResponse(200, ['checkout']);
      if (url === '/workspaces/7/spec-projects/checkout/specs') return jsonResponse(200, [SPEC_ITEM]);
      throw new Error(`unexpected request: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    renderPage();
    expect(await screen.findByText('checkout')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Abrir' }));

    expect(await screen.findByText('Checkout')).toBeInTheDocument();
    expect(screen.getByText('checkout.md · rascunho')).toBeInTheDocument();
  });

  it('creates a new project and opens it', async () => {
    let created = false;
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (url === '/workspaces/7') return jsonResponse(200, { clientId: 1 });
      if (url === '/workspaces/7/spec-projects' && init?.method === 'POST') {
        const body = JSON.parse(init.body as string);
        expect(body).toEqual({ name: 'onboarding' });
        created = true;
        return jsonResponse(201, { name: 'onboarding' });
      }
      if (url === '/workspaces/7/spec-projects') return jsonResponse(200, created ? ['onboarding'] : []);
      if (url === '/workspaces/7/spec-projects/onboarding/specs') return jsonResponse(200, []);
      throw new Error(`unexpected request: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    renderPage();
    await screen.findByText('Nenhum projeto ainda.');

    await userEvent.click(screen.getByRole('button', { name: 'Novo projeto' }));
    await userEvent.type(screen.getByLabelText('Nome do projeto'), 'onboarding');
    await userEvent.click(screen.getByRole('button', { name: 'Criar' }));

    expect(await screen.findByText('Nenhuma spec neste projeto ainda.')).toBeInTheDocument();
  });

  it('creates a new spec with a default template and opens the editor', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (url === '/workspaces/7') return jsonResponse(200, { clientId: 1 });
      if (url === '/workspaces/7/spec-projects') return jsonResponse(200, ['checkout']);
      if (url === '/workspaces/7/spec-projects/checkout/specs' && (!init || init.method === undefined)) return jsonResponse(200, []);
      if (url === '/workspaces/7/spec-projects/checkout/specs/nova.md' && init?.method === 'PUT') {
        const body = JSON.parse(init.body as string);
        expect(body.content).toContain('# nova');
        expect(body.content).toContain('> Status: rascunho');
        return jsonResponse(200, { saved: true });
      }
      if (url === '/workspaces/7/spec-projects/checkout/specs/nova.md') return textResponse(200, '# nova\n\n> Status: rascunho (2026-08-09).\n');
      throw new Error(`unexpected request: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    renderPage();
    await userEvent.click(await screen.findByRole('button', { name: 'Abrir' }));
    await screen.findByText('Nenhuma spec neste projeto ainda.');

    await userEvent.click(screen.getByRole('button', { name: 'Nova spec' }));
    await userEvent.type(screen.getByLabelText('Nome da spec'), 'nova');
    await userEvent.click(screen.getByRole('button', { name: 'Criar' }));

    expect(await screen.findByLabelText('Conteúdo')).toHaveValue('# nova\n\n> Status: rascunho (2026-08-09).\n');
  });

  function withEditorHandlers(extra: (url: string, init?: RequestInit) => Response | Promise<Response> | undefined) {
    return vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (url === '/workspaces/7') return jsonResponse(200, { clientId: 1 });
      if (url === '/workspaces/7/spec-projects') return jsonResponse(200, ['checkout']);
      if (url === '/workspaces/7/spec-projects/checkout/specs' && (!init || init.method === undefined)) return jsonResponse(200, [SPEC_ITEM]);
      if (url === '/workspaces/7/spec-projects/checkout/specs/checkout.md' && (!init || init.method === undefined)) return textResponse(200, SPEC_CONTENT);
      const result = extra(url, init);
      if (result) return result;
      throw new Error(`unexpected request: ${url} ${init?.method ?? 'GET'}`);
    });
  }

  it('loads the spec content into the editor and saves edits via PUT', async () => {
    const fetchMock = withEditorHandlers((url, init) => {
      if (url === '/workspaces/7/spec-projects/checkout/specs/checkout.md' && init?.method === 'PUT') {
        const body = JSON.parse(init.body as string);
        expect(body).toEqual({ content: 'texto editado' });
        return jsonResponse(200, { saved: true });
      }
      return undefined;
    });
    vi.stubGlobal('fetch', fetchMock);

    await openProjectAndSpec(fetchMock);
    const textarea = screen.getByLabelText('Conteúdo');
    expect(textarea).toHaveValue(SPEC_CONTENT);

    await userEvent.clear(textarea);
    await userEvent.type(textarea, 'texto editado');
    await userEvent.click(screen.getByRole('button', { name: 'Salvar' }));

    expect(await screen.findByText('Spec salva.')).toBeInTheDocument();
  });

  it('publishes via Subir US and shows the created reference', async () => {
    const fetchMock = withEditorHandlers((url, init) => {
      if (url === '/workspaces/7/spec-projects/checkout/specs/checkout.md/subir-us' && init?.method === 'POST') {
        return jsonResponse(201, { pipeline_instance: { externalRef: '42' } });
      }
      return undefined;
    });
    vi.stubGlobal('fetch', fetchMock);

    await openProjectAndSpec(fetchMock);
    await userEvent.click(screen.getByRole('button', { name: 'Subir US' }));

    expect(await screen.findByText('US criada: #42')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'US enviada' })).toBeDisabled();
  });

  it('shows the pendências inline when the DoR gate reports dor_atendido: false', async () => {
    const fetchMock = withEditorHandlers((url, init) => {
      if (url === '/workspaces/7/spec-projects/checkout/specs/checkout.md/subir-us' && init?.method === 'POST') {
        return jsonResponse(200, { dor_atendido: false, pendencias: ['falta WBS'] });
      }
      return undefined;
    });
    vi.stubGlobal('fetch', fetchMock);

    await openProjectAndSpec(fetchMock);
    await userEvent.click(screen.getByRole('button', { name: 'Subir US' }));

    expect(await screen.findByText('falta WBS')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Subir US' })).not.toBeDisabled();
  });

  it('sends a chat message to the specs skill and appends the reply', async () => {
    const fetchMock = withEditorHandlers((url, init) => {
      if (url === '/workspaces/7/spec-projects/checkout/specs/checkout.md/chat' && init?.method === 'POST') {
        const body = JSON.parse(init.body as string);
        expect(body.messages).toEqual([{ role: 'user', content: 'Sugira riscos.' }]);
        return jsonResponse(200, { reply: 'Aqui vai uma sugestão de riscos.' });
      }
      return undefined;
    });
    vi.stubGlobal('fetch', fetchMock);

    await openProjectAndSpec(fetchMock);
    await userEvent.type(screen.getByLabelText('Mensagem'), 'Sugira riscos.');
    await userEvent.click(screen.getByRole('button', { name: 'Enviar' }));

    expect(await screen.findByText('Aqui vai uma sugestão de riscos.')).toBeInTheDocument();
    expect(screen.getByText('Sugira riscos.')).toBeInTheDocument();
  });

  it('shows a retry error when the chat call fails, without losing the typed message from history', async () => {
    const fetchMock = withEditorHandlers((url, init) => {
      if (url === '/workspaces/7/spec-projects/checkout/specs/checkout.md/chat' && init?.method === 'POST') {
        return jsonResponse(502, {});
      }
      return undefined;
    });
    vi.stubGlobal('fetch', fetchMock);

    await openProjectAndSpec(fetchMock);
    await userEvent.type(screen.getByLabelText('Mensagem'), 'oi');
    await userEvent.click(screen.getByRole('button', { name: 'Enviar' }));

    expect(await screen.findByText('Não foi possível falar com a skill. Tente novamente.')).toBeInTheDocument();
    expect(screen.getByText('oi')).toBeInTheDocument();
  });

  it('"trocar projeto" and "trocar spec" navigate back up the hierarchy', async () => {
    const fetchMock = withEditorHandlers(() => undefined);
    vi.stubGlobal('fetch', fetchMock);

    await openProjectAndSpec(fetchMock);
    await userEvent.click(screen.getByRole('button', { name: 'trocar spec' }));

    expect(await screen.findByRole('button', { name: 'Nova spec' })).toBeInTheDocument();
    expect(screen.queryByLabelText('Conteúdo')).not.toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'trocar projeto' }));

    expect(await screen.findByRole('heading', { name: 'Projeto' })).toBeInTheDocument();
  });

  it('resets the whole hierarchy when the workspace changes elsewhere in the app', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (url === '/workspaces/7') return jsonResponse(200, { clientId: 1 });
      if (url === '/workspaces/7/spec-projects') return jsonResponse(200, ['checkout']);
      if (url === '/workspaces/8') return jsonResponse(200, { clientId: 2 });
      if (url === '/workspaces/8/spec-projects') return jsonResponse(200, []);
      throw new Error(`unexpected request: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    render(
      <WorkspaceProvider>
        <WorkspaceSwitcher to={8} />
        <SpecsPage />
      </WorkspaceProvider>,
    );
    await userEvent.click(await screen.findByRole('button', { name: 'Abrir' }));

    await userEvent.click(screen.getByRole('button', { name: 'Switch workspace' }));

    await waitFor(() => expect(fetchMock).toHaveBeenCalledWith('/workspaces/8/spec-projects', expect.anything()));
    expect(await screen.findByText('Nenhum projeto ainda.')).toBeInTheDocument();
  });
});
