import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { WorkspaceProvider, useWorkspace } from '../workspace/WorkspaceContext';
import { CredentialsPage } from './CredentialsPage';

function jsonResponse(status: number, body: unknown) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function renderPage() {
  return render(
    <WorkspaceProvider>
      <CredentialsPage />
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

const DEV_CREDENTIAL = {
  id: 1,
  perfil: 'dev',
  platformUsername: 'recolocarme-web',
  scopes: 'Contents:RW',
  status: 'active',
  createdAt: '2026-08-05T00:00:00Z',
  rotatedAt: null,
};

async function fillAndSubmitForm(overrides: { platformUsername?: string; token?: string } = {}) {
  await userEvent.type(screen.getByLabelText('Usuário na plataforma'), overrides.platformUsername ?? 'recolocarme-web');
  await userEvent.type(screen.getByLabelText('Token'), overrides.token ?? 'super-secret-token');
  await userEvent.click(screen.getByRole('button', { name: 'Cadastrar / rotacionar' }));
}

describe('CredentialsPage', () => {
  beforeEach(() => {
    localStorage.clear();
    localStorage.setItem('sdlc-dashboard:workspaceId', '7');
  });

  it('prompts to pick a workspace when none is selected', () => {
    localStorage.clear();
    renderPage();

    expect(screen.getByText('Selecione um workspace acima para começar.')).toBeInTheDocument();
  });

  it('lists registered credentials with perfil and status labels', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse(200, [DEV_CREDENTIAL])));

    renderPage();

    expect(await screen.findByText('Dev')).toBeInTheDocument();
    expect(screen.getByText('recolocarme-web · Ativa')).toBeInTheDocument();
    expect(screen.getByText('Escopos: Contents:RW')).toBeInTheDocument();
  });

  it('shows an empty state when there are no credentials yet', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse(200, [])));

    renderPage();

    expect(await screen.findByText('Nenhuma credencial cadastrada ainda.')).toBeInTheDocument();
  });

  it('registers a credential, clears the write-only token field, and reloads the list', async () => {
    let created = false;
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (url === '/workspaces/7/credenciais' && init?.method === 'POST') {
        const body = JSON.parse(init.body as string);
        expect(body).toEqual({ perfil: 'dev', platform_username: 'recolocarme-web', token: 'super-secret-token' });
        created = true;
        return jsonResponse(201, { ...DEV_CREDENTIAL });
      }
      if (url === '/workspaces/7/credenciais') return jsonResponse(200, created ? [DEV_CREDENTIAL] : []);
      throw new Error(`unexpected request: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    renderPage();
    await screen.findByText('Nenhuma credencial cadastrada ainda.');

    await userEvent.selectOptions(screen.getByLabelText('Perfil'), 'dev');
    await fillAndSubmitForm();

    expect(await screen.findByText('Credencial para Dev cadastrada.')).toBeInTheDocument();
    expect(screen.getByLabelText('Token')).toHaveValue('');
    expect(screen.getByLabelText('Usuário na plataforma')).toHaveValue('');
    expect(await screen.findByText('recolocarme-web · Ativa')).toBeInTheDocument();
  });

  it('never sends an empty scopes field when left blank', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (url === '/workspaces/7/credenciais' && init?.method === 'POST') {
        const body = JSON.parse(init.body as string);
        expect(body.scopes).toBeUndefined();
        return jsonResponse(201, DEV_CREDENTIAL);
      }
      return jsonResponse(200, []);
    });
    vi.stubGlobal('fetch', fetchMock);

    renderPage();
    await screen.findByText('Nenhuma credencial cadastrada ainda.');
    await fillAndSubmitForm();

    await screen.findByText('Credencial para Analista de Requisitos cadastrada.');
  });

  it('shows validation errors returned by the backend (422) without clearing the form', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (url === '/workspaces/7/credenciais' && init?.method === 'POST') return jsonResponse(422, { errors: ['token: is required'] });
      return jsonResponse(200, []);
    });
    vi.stubGlobal('fetch', fetchMock);

    renderPage();
    await screen.findByText('Nenhuma credencial cadastrada ainda.');
    await fillAndSubmitForm();

    expect(await screen.findByText('token: is required')).toBeInTheDocument();
    // The operator's input is preserved so they don't have to retype it.
    expect(screen.getByLabelText('Usuário na plataforma')).toHaveValue('recolocarme-web');
  });

  it('shows a retry message on a concurrent registration conflict (409)', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (url === '/workspaces/7/credenciais' && init?.method === 'POST') return jsonResponse(409, { error: 'conflict' });
      return jsonResponse(200, []);
    });
    vi.stubGlobal('fetch', fetchMock);

    renderPage();
    await screen.findByText('Nenhuma credencial cadastrada ainda.');
    await fillAndSubmitForm();

    expect(await screen.findByText('Já existe um cadastro concorrente para este perfil. Tente novamente.')).toBeInTheDocument();
  });

  it('shows a generic retry error on an unexpected failure (502)', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (url === '/workspaces/7/credenciais' && init?.method === 'POST') return jsonResponse(502, {});
      return jsonResponse(200, []);
    });
    vi.stubGlobal('fetch', fetchMock);

    renderPage();
    await screen.findByText('Nenhuma credencial cadastrada ainda.');
    await fillAndSubmitForm();

    expect(await screen.findByText('Não foi possível cadastrar a credencial. Tente novamente.')).toBeInTheDocument();
  });

  it('shows a retryable error when the list fails to load', async () => {
    const fetchMock = vi.fn(async () => jsonResponse(500, {}));
    vi.stubGlobal('fetch', fetchMock);

    renderPage();
    expect(await screen.findByText('Não foi possível carregar as credenciais. Tente novamente.')).toBeInTheDocument();

    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse(200, [DEV_CREDENTIAL])));
    await userEvent.click(screen.getByRole('button', { name: 'Tentar novamente' }));

    expect(await screen.findByText('recolocarme-web · Ativa')).toBeInTheDocument();
  });

  it('resets the list when the workspace changes elsewhere in the app', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (url === '/workspaces/7/credenciais') return jsonResponse(200, [DEV_CREDENTIAL]);
      if (url === '/workspaces/8/credenciais') return jsonResponse(200, []);
      throw new Error(`unexpected request: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    render(
      <WorkspaceProvider>
        <WorkspaceSwitcher to={8} />
        <CredentialsPage />
      </WorkspaceProvider>,
    );
    expect(await screen.findByText('recolocarme-web · Ativa')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Switch workspace' }));

    expect(await screen.findByText('Nenhuma credencial cadastrada ainda.')).toBeInTheDocument();
  });

  it('discards a stale response for a workspace deselected in the meantime', async () => {
    let resolveFirstLoad!: (response: Response) => void;
    const firstLoadPending = new Promise<Response>((resolve) => {
      resolveFirstLoad = resolve;
    });
    let callCount = 0;
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (url !== '/workspaces/7/credenciais') throw new Error(`unexpected request: ${url}`);
      callCount += 1;
      if (callCount === 1) return firstLoadPending;
      return jsonResponse(200, [DEV_CREDENTIAL]);
    });
    vi.stubGlobal('fetch', fetchMock);

    render(
      <WorkspaceProvider>
        <WorkspaceSwitcher to={null} label="Deselect" />
        <WorkspaceSwitcher to={7} label="Reselect 7" />
        <CredentialsPage />
      </WorkspaceProvider>,
    );
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1));

    await userEvent.click(screen.getByRole('button', { name: 'Deselect' }));
    expect(screen.getByText('Selecione um workspace acima para começar.')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Reselect 7' }));
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(2));
    expect(await screen.findByText('recolocarme-web · Ativa')).toBeInTheDocument();

    // The very first (now doubly-stale) request finally resolves with an empty list - it must not
    // overwrite the fresh second load's data.
    resolveFirstLoad(jsonResponse(200, []));
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(screen.getByText('recolocarme-web · Ativa')).toBeInTheDocument();
    expect(screen.queryByText('Nenhuma credencial cadastrada ainda.')).not.toBeInTheDocument();
  });
});
