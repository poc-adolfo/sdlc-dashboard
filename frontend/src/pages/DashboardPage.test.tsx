import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { WorkspaceProvider, useWorkspace } from '../workspace/WorkspaceContext';
import { DashboardPage } from './DashboardPage';

function jsonResponse(status: number, body: unknown) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function renderPage() {
  return render(
    <WorkspaceProvider>
      <DashboardPage />
    </WorkspaceProvider>,
  );
}

function WorkspaceSwitcher({ to, label = 'Switch workspace' }: { to: number | null; label?: string }) {
  const { setWorkspaceId } = useWorkspace();
  return (
    <button type="button" onClick={() => setWorkspaceId(to)}>
      {label}
    </button>
  );
}

const EMPTY_DASHBOARD = { contagens: {}, gates_pendentes: [] };

describe('DashboardPage', () => {
  beforeEach(() => {
    localStorage.clear();
    localStorage.setItem('sdlc-dashboard:workspaceId', '7');
  });

  it('prompts to pick a workspace when none is selected', () => {
    localStorage.clear();
    renderPage();

    expect(screen.getByText('Selecione um workspace acima para começar.')).toBeInTheDocument();
  });

  it('shows the phase counts in a fixed order with the right labels', async () => {
    const fetchMock = vi.fn(async () =>
      jsonResponse(200, {
        contagens: { Requisitos: 2, Design: 0, Dev: 5, CodeReview: 3, Qa: 1, Seguranca: 0, Deploy: 4 },
        gates_pendentes: [],
      }),
    );
    vi.stubGlobal('fetch', fetchMock);

    renderPage();

    expect(await screen.findByText('Code Review')).toBeInTheDocument();
    expect(screen.getByText('QA')).toBeInTheDocument();
    expect(screen.getByText('Segurança')).toBeInTheDocument();
    // Spot-check a couple of counts land next to the right label.
    const devCount = screen.getByText('Dev').closest('.phase-count');
    expect(devCount).toHaveTextContent('5');
  });

  it('shows a deep-link for a gate that has one', async () => {
    const fetchMock = vi.fn(async () =>
      jsonResponse(200, {
        ...EMPTY_DASHBOARD,
        gates_pendentes: [
          {
            pipeline_instance_id: 1,
            external_ref: '42',
            fase_atual: 'CodeReview',
            transicao: 'Code Review → QA',
            aprovador_esperado: 'Reviewer designado',
            deep_link: 'https://github.com/acme/platform/pull/42',
          },
        ],
      }),
    );
    vi.stubGlobal('fetch', fetchMock);

    renderPage();

    expect(await screen.findByText('Code Review → QA')).toBeInTheDocument();
    expect(screen.getByText('Aguardando: Reviewer designado')).toBeInTheDocument();
    const link = screen.getByRole('link', { name: 'Ver PR/review' });
    expect(link).toHaveAttribute('href', 'https://github.com/acme/platform/pull/42');
    expect(link).toHaveAttribute('target', '_blank');
  });

  it('shows descriptive text instead of a broken link when there is no deep_link yet', async () => {
    // seção 6.1: gates sem correspondência de PR/webhook (ex. Segurança → Deploy) mostram o texto
    // descritivo do mecanismo real em vez de um link quebrado.
    const fetchMock = vi.fn(async () =>
      jsonResponse(200, {
        ...EMPTY_DASHBOARD,
        gates_pendentes: [
          {
            pipeline_instance_id: 2,
            external_ref: '7',
            fase_atual: 'Seguranca',
            transicao: 'Segurança → Deploy',
            aprovador_esperado: 'AppSec + Release Manager',
            deep_link: null,
          },
        ],
      }),
    );
    vi.stubGlobal('fetch', fetchMock);

    renderPage();

    expect(await screen.findByText('Mecanismo de aprovação ainda não definido.')).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Ver PR/review' })).not.toBeInTheDocument();
  });

  it('shows an empty state when there are no pending gates', async () => {
    const fetchMock = vi.fn(async () => jsonResponse(200, EMPTY_DASHBOARD));
    vi.stubGlobal('fetch', fetchMock);

    renderPage();

    expect(await screen.findByText('Nenhum gate pendente.')).toBeInTheDocument();
  });

  it('shows a retryable error when the load fails', async () => {
    const fetchMock = vi.fn(async () => jsonResponse(500, {}));
    vi.stubGlobal('fetch', fetchMock);

    renderPage();

    expect(await screen.findByText('Não foi possível carregar o dashboard. Tente novamente.')).toBeInTheDocument();

    const retryFetch = vi.fn(async () => jsonResponse(200, EMPTY_DASHBOARD));
    vi.stubGlobal('fetch', retryFetch);
    await userEvent.click(screen.getByRole('button', { name: 'Tentar novamente' }));

    expect(await screen.findByText('Nenhum gate pendente.')).toBeInTheDocument();
  });

  it('resets when the workspace changes elsewhere in the app', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (url === '/workspaces/7/dashboard') return jsonResponse(200, { contagens: { Requisitos: 9 }, gates_pendentes: [] });
      if (url === '/workspaces/8/dashboard') return jsonResponse(200, EMPTY_DASHBOARD);
      throw new Error(`unexpected request: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    render(
      <WorkspaceProvider>
        <WorkspaceSwitcher to={8} />
        <DashboardPage />
      </WorkspaceProvider>,
    );

    await waitFor(() => {
      const requisitos = screen.getByText('Requisitos').closest('.phase-count');
      expect(requisitos).toHaveTextContent('9');
    });

    await userEvent.click(screen.getByRole('button', { name: 'Switch workspace' }));

    await waitFor(() => {
      const requisitos = screen.getByText('Requisitos').closest('.phase-count');
      expect(requisitos).toHaveTextContent('0');
    });
  });

  it('discards a stale, out-of-order dashboard response from a workspace switched away from', async () => {
    let resolveWorkspace7!: (response: Response) => void;
    const workspace7Pending = new Promise<Response>((resolve) => {
      resolveWorkspace7 = resolve;
    });
    let resolveWorkspace8!: (response: Response) => void;
    const workspace8Pending = new Promise<Response>((resolve) => {
      resolveWorkspace8 = resolve;
    });

    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (url === '/workspaces/7/dashboard') return workspace7Pending;
      if (url === '/workspaces/8/dashboard') return workspace8Pending;
      throw new Error(`unexpected request: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    render(
      <WorkspaceProvider>
        <WorkspaceSwitcher to={8} />
        <DashboardPage />
      </WorkspaceProvider>,
    );
    await waitFor(() => expect(fetchMock).toHaveBeenCalledWith('/workspaces/7/dashboard', expect.anything()));

    await userEvent.click(screen.getByRole('button', { name: 'Switch workspace' }));
    await waitFor(() => expect(fetchMock).toHaveBeenCalledWith('/workspaces/8/dashboard', expect.anything()));

    resolveWorkspace8(jsonResponse(200, { contagens: { Dev: 3 }, gates_pendentes: [] }));
    await waitFor(() => {
      const dev = screen.getByText('Dev').closest('.phase-count');
      expect(dev).toHaveTextContent('3');
    });

    resolveWorkspace7(jsonResponse(200, { contagens: { Dev: 99 }, gates_pendentes: [] }));
    await new Promise((resolve) => setTimeout(resolve, 50));
    const dev = screen.getByText('Dev').closest('.phase-count');
    expect(dev).toHaveTextContent('3');
    expect(dev).not.toHaveTextContent('99');
  });

  it('discards a stale response for a workspace deselected (set to null) in the meantime', async () => {
    // QA finding on PR #25: load() returned before bumping loadSeq.current when workspaceId was null,
    // so deselecting the workspace never invalidated whatever load was still in flight for the
    // previous one - reselecting the same workspace afterward could then have its fresh response
    // clobbered by that stale one resolving even later.
    let resolveFirstLoad!: (response: Response) => void;
    const firstLoadPending = new Promise<Response>((resolve) => {
      resolveFirstLoad = resolve;
    });
    let callCount = 0;
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (url !== '/workspaces/7/dashboard') throw new Error(`unexpected request: ${url}`);
      callCount += 1;
      if (callCount === 1) return firstLoadPending;
      return jsonResponse(200, { contagens: { Dev: 3 }, gates_pendentes: [] });
    });
    vi.stubGlobal('fetch', fetchMock);

    render(
      <WorkspaceProvider>
        <WorkspaceSwitcher to={null} label="Deselect" />
        <WorkspaceSwitcher to={7} label="Reselect 7" />
        <DashboardPage />
      </WorkspaceProvider>,
    );
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1));

    await userEvent.click(screen.getByRole('button', { name: 'Deselect' }));
    expect(screen.getByText('Selecione um workspace acima para começar.')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Reselect 7' }));
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(2));
    await waitFor(() => {
      const dev = screen.getByText('Dev').closest('.phase-count');
      expect(dev).toHaveTextContent('3');
    });

    // The very first (now doubly-stale) request finally resolves - it must not overwrite the fresh
    // second load's data.
    resolveFirstLoad(jsonResponse(200, { contagens: { Dev: 999 }, gates_pendentes: [] }));
    await new Promise((resolve) => setTimeout(resolve, 50));
    const dev = screen.getByText('Dev').closest('.phase-count');
    expect(dev).toHaveTextContent('3');
    expect(dev).not.toHaveTextContent('999');
  });
});
