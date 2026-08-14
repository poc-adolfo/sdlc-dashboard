import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Outlet, Route, Routes } from 'react-router-dom';
import { WorkspaceProvider, useWorkspace } from '../workspace/WorkspaceContext';
import type { LayoutContext } from '../components/Layout';
import { SpecsPage } from './SpecsPage';

function jsonResponse(status: number, body: unknown) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function textResponse(status: number, body: string) {
  return new Response(body, { status, headers: { 'Content-Type': 'text/markdown; charset=utf-8' } });
}

// Stands in for Layout's <Outlet context={{ setNavOpen }}> - SpecsPage reads this via useOutletContext()
// to collapse the main nav itself when a spec is selected (seção 5.2/5.4).
const setNavOpenSpy = vi.fn();

function TestLayout() {
  return <Outlet context={{ setNavOpen: setNavOpenSpy } satisfies LayoutContext} />;
}

function renderPage() {
  return render(
    <MemoryRouter initialEntries={['/specs']}>
      <WorkspaceProvider>
        <Routes>
          <Route element={<TestLayout />}>
            <Route path="/specs" element={<SpecsPage />} />
          </Route>
        </Routes>
      </WorkspaceProvider>
    </MemoryRouter>,
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

async function openSpecChat(fetchMock: ReturnType<typeof vi.fn>) {
  renderPage();
  await userEvent.click(await screen.findByRole('button', { name: (n) => n.startsWith('checkout.md') }));
  await screen.findByRole('heading', { name: 'O que vamos especificar hoje?' });
  return fetchMock;
}

async function openSpecModal(fetchMock: ReturnType<typeof vi.fn>) {
  renderPage();
  await userEvent.click(await screen.findByRole('button', { name: 'Visualizar checkout.md' }));
  await screen.findByLabelText('Conteúdo');
  return fetchMock;
}

describe('SpecsPage', () => {
  beforeEach(() => {
    localStorage.clear();
    localStorage.setItem('sdlc-dashboard:workspaceId', '7');
    setNavOpenSpy.mockClear();
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

  it('renders the tree (projeto -> specs -> *.md) with specs loaded eagerly for every project', async () => {
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
    expect(await screen.findByRole('button', { name: (n) => n.startsWith('checkout.md') })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Visualizar checkout.md' })).toBeInTheDocument();
  });

  it('creates a new project via the "+" button next to the Specs title', async () => {
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

    // Two "Novo projeto" triggers are visible when the tree is empty: the header "+" (first in the DOM)
    // and the empty-state's own CTA - this test targets the header one.
    const [headerButton] = screen.getAllByRole('button', { name: 'Novo projeto' });
    await userEvent.click(headerButton);
    await userEvent.type(screen.getByLabelText('Nome do projeto'), 'onboarding');
    await userEvent.click(screen.getByRole('button', { name: 'Criar' }));

    expect(await screen.findByText('onboarding')).toBeInTheDocument();
    expect(await screen.findByText('Nenhuma spec ainda.')).toBeInTheDocument();
  });

  it('creates a new spec with a default template and selects it for chat', async () => {
    let created = false;
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (url === '/workspaces/7') return jsonResponse(200, { clientId: 1 });
      if (url === '/workspaces/7/spec-projects') return jsonResponse(200, ['checkout']);
      if (url === '/workspaces/7/spec-projects/checkout/specs' && (!init || init.method === undefined)) {
        return jsonResponse(200, created ? [{ fileName: 'nova.md', title: 'nova', status: 'rascunho', version: 1, updatedAt: null }] : []);
      }
      if (url === '/workspaces/7/spec-projects/checkout/specs/nova.md' && init?.method === 'PUT') {
        const body = JSON.parse(init.body as string);
        expect(body.content).toContain('# nova');
        expect(body.content).toContain('> Status: rascunho');
        created = true;
        return jsonResponse(200, { saved: true });
      }
      throw new Error(`unexpected request: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    renderPage();
    await screen.findByText('Nenhuma spec ainda.');

    await userEvent.click(screen.getByRole('button', { name: '+ Nova spec' }));
    await userEvent.type(screen.getByLabelText('Nome da spec'), 'nova');
    await userEvent.click(screen.getByRole('button', { name: 'Criar' }));

    // Selecting the new spec for chat also collapses the sidebar (seção 5.4), so the tree (and its
    // "Visualizar nova.md" button) is no longer on screen to check directly.
    expect(await screen.findByRole('heading', { name: 'O que vamos especificar hoje?' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Visualizar nova.md' })).not.toBeInTheDocument();
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

  it('"ver" opens a modal with the spec content, and saves edits via PUT', async () => {
    const fetchMock = withEditorHandlers((url, init) => {
      if (url === '/workspaces/7/spec-projects/checkout/specs/checkout.md' && init?.method === 'PUT') {
        const body = JSON.parse(init.body as string);
        expect(body).toEqual({ content: 'texto editado' });
        return jsonResponse(200, { saved: true });
      }
      return undefined;
    });
    vi.stubGlobal('fetch', fetchMock);

    await openSpecModal(fetchMock);
    const textarea = screen.getByLabelText('Conteúdo');
    expect(textarea).toHaveValue(SPEC_CONTENT);
    expect(textarea).toBeVisible(); // inside the modal, not a collapsed accordion

    await userEvent.clear(textarea);
    await userEvent.type(textarea, 'texto editado');
    await userEvent.click(screen.getByRole('button', { name: 'Salvar' }));

    expect(await screen.findByText('Spec salva.')).toBeInTheDocument();
  });

  it('closes the view modal via the × button', async () => {
    const fetchMock = withEditorHandlers(() => undefined);
    vi.stubGlobal('fetch', fetchMock);

    await openSpecModal(fetchMock);
    await userEvent.click(screen.getByRole('button', { name: 'Fechar' }));

    expect(screen.queryByLabelText('Conteúdo')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Visualizar checkout.md' })).toBeInTheDocument();
  });

  it('closes the view modal on Escape', async () => {
    const fetchMock = withEditorHandlers(() => undefined);
    vi.stubGlobal('fetch', fetchMock);

    await openSpecModal(fetchMock);
    await userEvent.keyboard('{Escape}');

    expect(screen.queryByLabelText('Conteúdo')).not.toBeInTheDocument();
  });

  it('publishes via Subir US and shows the created reference', async () => {
    const fetchMock = withEditorHandlers((url, init) => {
      if (url === '/workspaces/7/spec-projects/checkout/specs/checkout.md/subir-us' && init?.method === 'POST') {
        return jsonResponse(201, { pipeline_instance: { externalRef: '42' } });
      }
      return undefined;
    });
    vi.stubGlobal('fetch', fetchMock);

    await openSpecModal(fetchMock);
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

    await openSpecModal(fetchMock);
    await userEvent.click(screen.getByRole('button', { name: 'Subir US' }));

    expect(await screen.findByText('falta WBS')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Subir US' })).not.toBeDisabled();
  });

  it('shows the chat prompt "O que vamos especificar hoje?" and sends a message on Enter (no Shift)', async () => {
    const fetchMock = withEditorHandlers((url, init) => {
      if (url === '/workspaces/7/spec-projects/checkout/specs/checkout.md/chat' && init?.method === 'POST') {
        const body = JSON.parse(init.body as string);
        expect(body.messages).toEqual([{ role: 'user', content: 'Sugira riscos.' }]);
        return jsonResponse(200, { reply: 'Aqui vai uma sugestão de riscos.' });
      }
      return undefined;
    });
    vi.stubGlobal('fetch', fetchMock);

    await openSpecChat(fetchMock);

    await userEvent.type(screen.getByLabelText('Mensagem'), 'Sugira riscos.{Enter}');

    expect(await screen.findByText('Aqui vai uma sugestão de riscos.')).toBeInTheDocument();
    expect(screen.getByText('Sugira riscos.')).toBeInTheDocument();
  });

  it('Shift+Enter inserts a newline instead of sending', async () => {
    const fetchMock = withEditorHandlers(() => undefined);
    vi.stubGlobal('fetch', fetchMock);

    await openSpecChat(fetchMock);
    const input = screen.getByLabelText('Mensagem');
    await userEvent.type(input, 'linha 1{Shift>}{Enter}{/Shift}linha 2');

    expect(input).toHaveValue('linha 1\nlinha 2');
    expect(screen.queryByText('linha 1')).not.toBeInTheDocument(); // not sent - still just draft text
  });

  it('shows a retry error when the chat call fails, without losing the typed message from history', async () => {
    const fetchMock = withEditorHandlers((url, init) => {
      if (url === '/workspaces/7/spec-projects/checkout/specs/checkout.md/chat' && init?.method === 'POST') {
        return jsonResponse(502, {});
      }
      return undefined;
    });
    vi.stubGlobal('fetch', fetchMock);

    await openSpecChat(fetchMock);
    await userEvent.type(screen.getByLabelText('Mensagem'), 'oi');
    await userEvent.click(screen.getByRole('button', { name: 'Enviar' }));

    expect(await screen.findByText('Não foi possível falar com a skill. Tente novamente.')).toBeInTheDocument();
    expect(screen.getByText('oi')).toBeInTheDocument();
  });

  it('collapses and expands the specs sidebar', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (url === '/workspaces/7') return jsonResponse(200, { clientId: 1 });
      if (url === '/workspaces/7/spec-projects') return jsonResponse(200, ['checkout']);
      if (url === '/workspaces/7/spec-projects/checkout/specs') return jsonResponse(200, [SPEC_ITEM]);
      throw new Error(`unexpected request: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    renderPage();
    await screen.findByRole('button', { name: (n) => n.startsWith('checkout.md') });
    expect(screen.getByRole('heading', { name: 'Specs' })).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Recolher lista de specs' }));

    expect(screen.queryByRole('heading', { name: 'Specs' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: (n) => n.startsWith('checkout.md') })).not.toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Expandir lista de specs' }));

    expect(await screen.findByRole('button', { name: (n) => n.startsWith('checkout.md') })).toBeInTheDocument();
  });

  it('resets the tree and clears the chat selection when the workspace changes elsewhere in the app', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (url === '/workspaces/7') return jsonResponse(200, { clientId: 1 });
      if (url === '/workspaces/7/spec-projects') return jsonResponse(200, ['checkout']);
      if (url === '/workspaces/7/spec-projects/checkout/specs') return jsonResponse(200, [SPEC_ITEM]);
      if (url === '/workspaces/8') return jsonResponse(200, { clientId: 2 });
      if (url === '/workspaces/8/spec-projects') return jsonResponse(200, []);
      throw new Error(`unexpected request: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    render(
      <MemoryRouter initialEntries={['/specs']}>
        <WorkspaceProvider>
          <WorkspaceSwitcher to={8} />
          <Routes>
            <Route element={<TestLayout />}>
              <Route path="/specs" element={<SpecsPage />} />
            </Route>
          </Routes>
        </WorkspaceProvider>
      </MemoryRouter>,
    );
    await userEvent.click(await screen.findByRole('button', { name: (n) => n.startsWith('checkout.md') }));
    await screen.findByRole('heading', { name: 'O que vamos especificar hoje?' });
    // Selecting a spec collapses the sidebar (seção 5.4) - this switch happens while it's collapsed.
    expect(setNavOpenSpy).toHaveBeenCalledWith(false);

    await userEvent.click(screen.getByRole('button', { name: 'Switch workspace' }));

    expect(await screen.findByText('Selecione uma spec ao lado para começar.')).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'O que vamos especificar hoje?' })).not.toBeInTheDocument();

    // Expand the sidebar back to confirm the tree itself was reset to workspace 8's (empty) data, not
    // just the chat selection.
    await userEvent.click(screen.getByRole('button', { name: 'Expandir lista de specs' }));
    await waitFor(() => expect(fetchMock).toHaveBeenCalledWith('/workspaces/8/spec-projects', expect.anything()));
    expect(await screen.findByText('Nenhum projeto ainda.')).toBeInTheDocument();
  });
});
