import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { WorkspaceProvider } from '../workspace/WorkspaceContext';
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

  it('shows a success message when Concluir reports dor_atendido: true', async () => {
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

  it('shows the pendências inline when Concluir reports dor_atendido: false', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (url.startsWith('/clients?q=')) return jsonResponse(200, [{ id: 1, name: 'Acme Corp' }]);
      if (url === '/workspaces/7/assessments' && init?.method === 'POST') return jsonResponse(200, { id: 42, workspaceId: 7, clientId: 1, content: DEFAULT_CONTENT, status: 'em_andamento' });
      if (url === '/workspaces/7/assessments/42/concluir') return jsonResponse(200, { concluido: false, pendencias: ['falta stack utilizada'] });
      throw new Error(`unexpected request: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    await selectExistingClientAndLoadAssessment(fetchMock);

    await userEvent.click(screen.getByRole('button', { name: 'Concluir' }));

    expect(await screen.findByText('falta stack utilizada')).toBeInTheDocument();
    expect(screen.queryByText('Assessment concluído.')).not.toBeInTheDocument();
  });

  it('shows a retry-friendly error when the Analista gate is unreachable (502)', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (url.startsWith('/clients?q=')) return jsonResponse(200, [{ id: 1, name: 'Acme Corp' }]);
      if (url === '/workspaces/7/assessments' && init?.method === 'POST') return jsonResponse(200, { id: 42, workspaceId: 7, clientId: 1, content: DEFAULT_CONTENT, status: 'em_andamento' });
      if (url === '/workspaces/7/assessments/42/concluir') return jsonResponse(502, {});
      throw new Error(`unexpected request: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    await selectExistingClientAndLoadAssessment(fetchMock);

    await userEvent.click(screen.getByRole('button', { name: 'Concluir' }));

    expect(await screen.findByText('O serviço do Analista está indisponível. Tente novamente mais tarde.')).toBeInTheDocument();
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
});
