import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { WorkspaceProvider, useWorkspace } from '../workspace/WorkspaceContext';
import { SpecsPage } from './SpecsPage';

function jsonResponse(status: number, body: unknown) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
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

const SPEC_A = { path: 'specs/checkout.md', title: 'Checkout', status: 'rascunho', version: 1, updatedAt: '2026-08-05T00:00:00Z', subirUsPath: 'checkout.md' };

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

  it('lists specs in rascunho for the current workspace', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (url === '/workspaces/7/specs?status=rascunho') return jsonResponse(200, [SPEC_A]);
      throw new Error(`unexpected request: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    renderPage();

    expect(await screen.findByText('Checkout')).toBeInTheDocument();
    expect(screen.getByText('specs/checkout.md')).toBeInTheDocument();
  });

  it('shows an empty state when there are no drafts', async () => {
    const fetchMock = vi.fn(async () => jsonResponse(200, []));
    vi.stubGlobal('fetch', fetchMock);

    renderPage();

    expect(await screen.findByText('Nenhuma spec em rascunho encontrada.')).toBeInTheDocument();
  });

  it('shows a retryable error when the listing fails (502, repo unreachable)', async () => {
    const fetchMock = vi.fn(async () => jsonResponse(502, {}));
    vi.stubGlobal('fetch', fetchMock);

    renderPage();

    expect(await screen.findByText('Não foi possível acessar o repositório de specs.')).toBeInTheDocument();

    const retryFetch = vi.fn(async () => jsonResponse(200, [SPEC_A]));
    vi.stubGlobal('fetch', retryFetch);
    await userEvent.click(screen.getByRole('button', { name: 'Tentar novamente' }));

    expect(await screen.findByText('Checkout')).toBeInTheDocument();
  });

  it('publishes via the subirUsPath from the listing item, not the full path', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (url === '/workspaces/7/specs?status=rascunho') return jsonResponse(200, [SPEC_A]);
      if (url === '/workspaces/7/specs/checkout.md/subir-us') return jsonResponse(201, { pipeline_instance: { externalRef: '42' } });
      throw new Error(`unexpected request: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    renderPage();
    await userEvent.click(await screen.findByRole('button', { name: 'Subir US' }));

    expect(await screen.findByText('US criada: #42')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'US enviada' })).toBeDisabled();
  });

  it('shows the pendências inline when the DoR gate reports dor_atendido: false', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (url === '/workspaces/7/specs?status=rascunho') return jsonResponse(200, [SPEC_A]);
      if (url === '/workspaces/7/specs/checkout.md/subir-us') return jsonResponse(200, { dor_atendido: false, pendencias: ['falta WBS'] });
      throw new Error(`unexpected request: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    renderPage();
    await userEvent.click(await screen.findByRole('button', { name: 'Subir US' }));

    expect(await screen.findByText('falta WBS')).toBeInTheDocument();
    // Not disabled/consumed - the operator can fix the spec and try again.
    expect(screen.getByRole('button', { name: 'Subir US' })).not.toBeDisabled();
  });

  it('shows a retry-later error when publishing itself fails (502)', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (url === '/workspaces/7/specs?status=rascunho') return jsonResponse(200, [SPEC_A]);
      if (url === '/workspaces/7/specs/checkout.md/subir-us') return jsonResponse(502, {});
      throw new Error(`unexpected request: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    renderPage();
    await userEvent.click(await screen.findByRole('button', { name: 'Subir US' }));

    expect(await screen.findByText('Falha ao publicar no repositório. Tente novamente mais tarde.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Subir US' })).not.toBeDisabled();
  });

  it('URL-encodes subirUsPath segments while preserving "/" as a path separator', async () => {
    const nested = { ...SPEC_A, path: 'specs/a b.md', subirUsPath: 'a b.md' };
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (url === '/workspaces/7/specs?status=rascunho') return jsonResponse(200, [nested]);
      if (url === '/workspaces/7/specs/a%20b.md/subir-us') return jsonResponse(201, { pipeline_instance: { externalRef: '1' } });
      throw new Error(`unexpected request: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    renderPage();
    await userEvent.click(await screen.findByRole('button', { name: 'Subir US' }));

    expect(await screen.findByText('US criada: #1')).toBeInTheDocument();
  });

  it('resets the listing when the workspace changes elsewhere in the app', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (url === '/workspaces/7/specs?status=rascunho') return jsonResponse(200, [SPEC_A]);
      if (url === '/workspaces/8/specs?status=rascunho') return jsonResponse(200, []);
      throw new Error(`unexpected request: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    render(
      <WorkspaceProvider>
        <WorkspaceSwitcher to={8} />
        <SpecsPage />
      </WorkspaceProvider>,
    );

    expect(await screen.findByText('Checkout')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Switch workspace' }));

    await waitFor(() => expect(fetchMock).toHaveBeenCalledWith('/workspaces/8/specs?status=rascunho', expect.anything()));
    expect(await screen.findByText('Nenhuma spec em rascunho encontrada.')).toBeInTheDocument();
    expect(screen.queryByText('Checkout')).not.toBeInTheDocument();
  });

  it('discards a stale, out-of-order listing response from a workspace switched away from', async () => {
    // Revisor finding on PR #24: workspace 7's listing is still in flight when the operator switches to
    // workspace 8 - if workspace 7's (now stale) response resolves after workspace 8's, it must not
    // overwrite what's on screen.
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
      if (url === '/workspaces/7/specs?status=rascunho') return workspace7Pending;
      if (url === '/workspaces/8/specs?status=rascunho') return workspace8Pending;
      throw new Error(`unexpected request: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    render(
      <WorkspaceProvider>
        <WorkspaceSwitcher to={8} />
        <SpecsPage />
      </WorkspaceProvider>,
    );
    await waitFor(() => expect(fetchMock).toHaveBeenCalledWith('/workspaces/7/specs?status=rascunho', expect.anything()));

    await userEvent.click(screen.getByRole('button', { name: 'Switch workspace' }));
    await waitFor(() => expect(fetchMock).toHaveBeenCalledWith('/workspaces/8/specs?status=rascunho', expect.anything()));

    // The newer request (workspace 8) resolves first, as it normally would.
    const WORKSPACE_8_SPEC = { ...SPEC_A, path: 'specs/onboarding.md', title: 'Onboarding' };
    resolveWorkspace8(jsonResponse(200, [WORKSPACE_8_SPEC]));
    expect(await screen.findByText('Onboarding')).toBeInTheDocument();

    // The older, now-stale request resolves after - it must not clobber workspace 8's listing.
    resolveWorkspace7(jsonResponse(200, [SPEC_A]));
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(screen.queryByText('Checkout')).not.toBeInTheDocument();
    expect(screen.getByText('Onboarding')).toBeInTheDocument();
  });
});
