import { useCallback, useEffect, useRef, useState, type FormEvent, type ReactNode } from 'react';
import { useNavigate } from 'react-router-dom';
import { api, ApiError } from '../api/client';
import { useWorkspace, useWorkspaceList } from '../workspace/WorkspaceContext';

// seção 4/5.1/8: onboarding de um workspace consolidado numa tela só - dados do workspace, assessment e
// credenciais de perfil, empilhados em vez de 3 telas separadas que o operador tinha que lembrar de
// visitar em sequência. Sem workspace selecionado, só a primeira seção (criação) aparece; as outras duas
// precisam de um workspace já existente (assessment/credencial são sempre de um workspace).

// ---------------------------------------------------------------------------
// 1. Workspace - identificação, cliente e assessment vivem juntos num único bloco (o operador preenche
//    tudo e conclui de uma vez, em vez de 3 salvamentos separados). "Concluir" cria/atualiza o workspace,
//    grava o assessment e o conclui, então segue para specs.
// ---------------------------------------------------------------------------

interface WorkspaceDetails {
  id: number;
  name: string;
  platform: 'github' | 'azure_devops';
  platformRef: string;
}

interface ClientOption {
  id: number;
  name: string;
}

// Mesmo texto de AssessmentEndpoints.DefaultContent (seção 5.1) - o backend só aplica esse fallback
// quando `content` chega null, mas esta tela sempre manda uma string (mesmo vazia), então esse
// fallback nunca disparava de verdade; o template precisa aparecer aqui, do lado do cliente, sempre
// que ainda não existe nenhum assessment salvo para o workspace.
const DEFAULT_ASSESSMENT_CONTENT =
  '## Linha de negocio do cliente\n\n\n## Stack utilizada\n\n\n## Arquiteturas presentes\n\n\n## Constraints de seguranca\n\n\n## Observacoes adicionais\n';

interface Assessment {
  id: number;
  workspaceId: number;
  clientId: number;
  clientName: string;
  content: string;
  figmaProjectUrl: string | null;
  selectedDesignSystemProposalId: number | null;
  status: 'em_andamento' | 'concluido';
}

type SelectedClient = { kind: 'existing'; id: number; name: string } | { kind: 'new'; name: string };

type SearchState = 'idle' | 'loading' | 'success' | 'error';

function WorkspaceSection({
  workspaceId,
  onWorkspaceCreated,
}: {
  workspaceId: number | null;
  onWorkspaceCreated: (workspace: { id: number; name: string }) => void;
}) {
  const navigate = useNavigate();
  const { refresh: refreshWorkspaceList } = useWorkspaceList();

  const [name, setName] = useState('');
  const [platform, setPlatform] = useState<'github' | 'azure_devops'>('github');
  const [platformRef, setPlatformRef] = useState('');

  const [selectedClient, setSelectedClient] = useState<SelectedClient | null>(null);
  const [clientQuery, setClientQuery] = useState('');
  const [clientResults, setClientResults] = useState<ClientOption[]>([]);
  const [clientSearchState, setClientSearchState] = useState<SearchState>('idle');
  const clientSearchSeq = useRef(0);

  const [content, setContent] = useState('');
  const [figmaProjectUrl, setFigmaProjectUrl] = useState('');
  const [selectedDesignSystemName, setSelectedDesignSystemName] = useState<string | null>(null);

  const [loading, setLoading] = useState(false);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // Collapsible once there's something to collapse (an existing, already-filled-in workspace) - starts
  // open so create mode (nothing to hide yet) and first-time editing both show the fields right away.
  // Controlled (not just a static `open` attribute) so a re-render from unrelated state here (submitting,
  // editing a field) doesn't fight the operator's own manual toggle.
  const [detailsOpen, setDetailsOpen] = useState(true);
  // Same request-sequence guard used throughout this app: a slower response for a workspace switched
  // away from must not overwrite the fields for whichever one is on screen now.
  const loadSeq = useRef(0);

  // Loads both the workspace's own fields and its in-progress assessment (if any) together - a reload or
  // a trip to another tab must restore everything the operator already filled in, not just the workspace
  // identity, or "Concluir" would look like it silently lost the client/content.
  useEffect(() => {
    setError(null);
    if (workspaceId === null) {
      setName('');
      setPlatform('github');
      setPlatformRef('');
      setSelectedClient(null);
      setClientQuery('');
      setContent(DEFAULT_ASSESSMENT_CONTENT);
      setFigmaProjectUrl('');
      setSelectedDesignSystemName(null);
      return;
    }
    const seq = ++loadSeq.current;
    setLoading(true);
    setLoadError(null);
    Promise.all([
      api.get<WorkspaceDetails>(`/workspaces/${workspaceId}`),
      api.get<Assessment>(`/workspaces/${workspaceId}/assessments/current`).then(
        (assessment) => assessment,
        (err: unknown) => {
          if (err instanceof ApiError && err.status === 404) return null;
          throw err;
        },
      ),
    ]).then(
      ([workspace, assessment]) => {
        if (seq !== loadSeq.current) return;
        setName(workspace.name);
        setPlatform(workspace.platform);
        setPlatformRef(workspace.platformRef);
        if (assessment) {
          setSelectedClient({ kind: 'existing', id: assessment.clientId, name: assessment.clientName });
          setContent(assessment.content);
          setFigmaProjectUrl(assessment.figmaProjectUrl ?? '');
          if (assessment.selectedDesignSystemProposalId !== null) {
            const proposalId = assessment.selectedDesignSystemProposalId;
            api.get<{ id: number; nome: string }[]>(`/workspaces/${workspaceId}/design-system/proposals`).then(
              (proposals) => {
                if (seq !== loadSeq.current) return;
                setSelectedDesignSystemName(proposals.find((p) => p.id === proposalId)?.nome ?? null);
              },
              () => {
                if (seq !== loadSeq.current) return;
                setSelectedDesignSystemName(null);
              },
            );
          } else {
            setSelectedDesignSystemName(null);
          }
        } else {
          setSelectedClient(null);
          setContent(DEFAULT_ASSESSMENT_CONTENT);
          setFigmaProjectUrl('');
          setSelectedDesignSystemName(null);
        }
        setClientQuery('');
        setLoading(false);
      },
      () => {
        if (seq !== loadSeq.current) return;
        setLoading(false);
        setLoadError('Não foi possível carregar os dados do workspace. Tente novamente.');
      },
    );
  }, [workspaceId]);

  // seção 5.1: combobox "buscar ou criar" - searches as the operator types, debounced so every keystroke
  // doesn't fire a request. Picking a result (or offering to create a new client) is now purely local
  // state - the client is only actually persisted when the whole section is concluded.
  useEffect(() => {
    const trimmed = clientQuery.trim();
    if (trimmed.length === 0) {
      setClientResults([]);
      setClientSearchState('idle');
      clientSearchSeq.current += 1;
      return;
    }
    setClientSearchState('loading');
    const seq = ++clientSearchSeq.current;
    const timer = setTimeout(() => {
      api.get<ClientOption[]>(`/clients?q=${encodeURIComponent(trimmed)}`).then(
        (data) => {
          if (seq !== clientSearchSeq.current) return; // a newer search has since started - discard
          setClientResults(data);
          setClientSearchState('success');
        },
        () => {
          if (seq !== clientSearchSeq.current) return;
          setClientResults([]);
          setClientSearchState('error');
        },
      );
    }, 300);
    return () => clearTimeout(timer);
  }, [clientQuery]);

  function chooseClient(client: SelectedClient) {
    setSelectedClient(client);
    setClientQuery('');
    setClientResults([]);
    setClientSearchState('idle');
  }

  function trocarCliente() {
    setSelectedClient(null);
  }

  const trimmedClientQuery = clientQuery.trim();
  const exactClientMatch = clientResults.some((r) => r.name.toLowerCase() === trimmedClientQuery.toLowerCase());
  // QA finding on PR #22: offering "criar novo cliente" while the search is still pending, or after it
  // failed, risks creating a duplicate client during what might just be a transient outage or an answer
  // that hasn't arrived yet - only a *completed, successful* search with no exact match may offer it.
  const canOfferCreateClient = clientSearchState === 'success' && trimmedClientQuery.length > 0 && !exactClientMatch;

  const canSubmit = !submitting && !loading && name.trim().length > 0 && platformRef.trim().length > 0 && selectedClient !== null && content.trim().length > 0;

  // The one action for this whole section - saves the workspace identity, then the assessment (client +
  // content), then concludes it, then takes the operator straight to specs. Replaces what used to be 3
  // separate saves (workspace, assessment, concluir) the operator had to remember to do in order.
  async function handleConcluir() {
    setSubmitting(true);
    setError(null);
    try {
      let id = workspaceId;
      if (id === null) {
        const created = await api.post<{ id: number; name: string }>('/workspaces', { name, platform, platform_ref: platformRef });
        id = created.id;
        onWorkspaceCreated(created);
      } else {
        await api.patch(`/workspaces/${id}`, { name, platform, platform_ref: platformRef });
      }
      await refreshWorkspaceList();

      const clientBody = selectedClient!.kind === 'existing' ? { client_id: selectedClient.id } : { client_name: selectedClient!.name };
      const assessment = await api.post<Assessment>(`/workspaces/${id}/assessments`, {
        ...clientBody,
        content,
        figma_project_url: figmaProjectUrl.trim().length > 0 ? figmaProjectUrl.trim() : undefined,
      });
      await api.post<{ concluido: true }>(`/workspaces/${id}/assessments/${assessment.id}/concluir`);
      navigate('/specs');
    } catch (err) {
      // seção 4: platform/platform_ref ficam travados depois que já existe pipeline_instance vinculada -
      // o backend rejeita com 409, o formulário só repassa esse motivo em vez de adivinhar de antemão.
      setError(
        err instanceof ApiError && err.status === 409
          ? 'Plataforma e repositório não podem ser alterados depois que o ciclo já começou para este workspace.'
          : 'Não foi possível concluir. Verifique os campos e tente novamente.',
      );
      setSubmitting(false);
    }
  }

  if (workspaceId !== null && loading) return <p role="status">Carregando workspace...</p>;

  return (
    <div className="workspace-details-form">
      <details
        className="workspace-details-accordion"
        open={detailsOpen}
        onToggle={(e) => setDetailsOpen(e.currentTarget.open)}
      >
        <summary>
          <span>{workspaceId === null ? 'Novo workspace' : name || 'Workspace'}</span>
          {/* Concluir lives in the summary bar (not the collapsible body) so it stays visible - and
              actionable - whether or not the operator has this section expanded. preventDefault/
              stopPropagation keep the click from also triggering the native <details> toggle. */}
          <button
            type="button"
            className="btn-primary"
            onClick={(e) => {
              e.preventDefault();
              e.stopPropagation();
              handleConcluir();
            }}
            disabled={!canSubmit}
          >
            {submitting ? 'Concluindo...' : 'Concluir'}
          </button>
        </summary>
        <div className="workspace-details-accordion-body">
          {loadError && <p role="alert">{loadError}</p>}

          <div className="field-group">
            <span className="field-group-label">Identificação</span>
            <label htmlFor="workspace-name">Nome</label>
            <input id="workspace-name" value={name} onChange={(e) => setName(e.target.value)} disabled={submitting} required />
          </div>

          <div className="field-group">
            <span className="field-group-label">Publicação</span>
            <div className="field-group-row">
              <div>
                <label htmlFor="workspace-platform">Plataforma</label>
                <select id="workspace-platform" value={platform} onChange={(e) => setPlatform(e.target.value as 'github' | 'azure_devops')} disabled={submitting}>
                  <option value="github">GitHub</option>
                  <option value="azure_devops">Azure DevOps</option>
                </select>
              </div>
              <div>
                <label htmlFor="workspace-platform-ref">Repositório/Projeto</label>
                <input
                  id="workspace-platform-ref"
                  value={platformRef}
                  onChange={(e) => setPlatformRef(e.target.value)}
                  placeholder="org/repo"
                  disabled={submitting}
                  required
                />
              </div>
            </div>
          </div>

          <div className="field-group">
            <span className="field-group-label">Cliente</span>
            {selectedClient === null ? (
              <>
                <label htmlFor="client-search">Cliente</label>
                <input
                  id="client-search"
                  value={clientQuery}
                  onChange={(e) => setClientQuery(e.target.value)}
                  placeholder="Buscar cliente..."
                  disabled={submitting}
                />
                {clientSearchState === 'loading' && <p role="status">Buscando...</p>}
                {clientSearchState === 'error' && <p role="alert">Não foi possível buscar clientes. Tente novamente.</p>}
                <ul className="client-results">
                  {clientResults.map((client) => (
                    <li key={client.id}>
                      <button type="button" onClick={() => chooseClient({ kind: 'existing', id: client.id, name: client.name })} disabled={submitting}>
                        {client.name}
                      </button>
                    </li>
                  ))}
                  {canOfferCreateClient && (
                    <li>
                      <button type="button" onClick={() => chooseClient({ kind: 'new', name: trimmedClientQuery })} disabled={submitting}>
                        Criar novo cliente: "{trimmedClientQuery}"
                      </button>
                    </li>
                  )}
                </ul>
              </>
            ) : (
              <p>
                Cliente: <strong>{selectedClient.name}</strong>{' '}
                <button type="button" className="link-button" onClick={trocarCliente} disabled={submitting}>
                  trocar
                </button>
              </p>
            )}
          </div>

          <div className="field-group">
            <span className="field-group-label">Assessment</span>
            <label htmlFor="assessment-content">Conteúdo</label>
            <textarea id="assessment-content" value={content} onChange={(e) => setContent(e.target.value)} rows={16} disabled={submitting} />

            <label htmlFor="assessment-figma-url">Projeto no Figma (opcional)</label>
            <input
              id="assessment-figma-url"
              type="url"
              value={figmaProjectUrl}
              onChange={(e) => setFigmaProjectUrl(e.target.value)}
              placeholder="https://www.figma.com/files/..."
              disabled={submitting}
            />

            {selectedDesignSystemName && (
              <p className="assessment-design-system-selected">
                Design system selecionado: <strong>{selectedDesignSystemName}</strong>
              </p>
            )}
          </div>

          {error && <p role="alert">{error}</p>}
        </div>
      </details>
    </div>
  );
}

// ---------------------------------------------------------------------------
// 2. Credenciais - lista das 7 perfis Hermes (seção 8), cadastradas ou não, com edição inline por linha.
// ---------------------------------------------------------------------------

interface Credential {
  id: number;
  perfil: string;
  platformUsername: string;
  scopes: string | null;
  status: 'active' | 'revoked' | 'pending';
  createdAt: string;
  rotatedAt: string | null;
}

const PERFIS: readonly [value: string, label: string][] = [
  ['analista_requisitos', 'Analista de Requisitos'],
  ['arquiteto', 'Arquiteto'],
  ['dev', 'Dev'],
  ['revisor', 'Revisor de Código'],
  ['qa', 'QA'],
  ['seguranca', 'Segurança'],
  ['release_deploy', 'Release/Deploy'],
];
const PERFIL_LABELS: Record<string, string> = Object.fromEntries(PERFIS);

const STATUS_LABELS: Record<Credential['status'], string> = {
  active: 'Ativa',
  revoked: 'Revogada',
  pending: 'Pendente',
};

function InlineCredentialForm({
  perfil,
  workspaceId,
  onSaved,
  onCancel,
}: {
  perfil: string;
  workspaceId: number;
  onSaved: () => void;
  onCancel: () => void;
}) {
  const [platformUsername, setPlatformUsername] = useState('');
  const [token, setToken] = useState('');
  const [scopes, setScopes] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [formErrors, setFormErrors] = useState<string[]>([]);
  // Revisor finding on PR #26 (adapted for this row-scoped form): the operator can switch workspace
  // while this POST is still in flight. A workspace switch resets the section's `credentials` state,
  // which unmounts this row (and this form with it) before the request settles - mountedRef catches
  // that so a stale response never calls onSaved() against a closure still pointing at the old workspace.
  const mountedRef = useRef(true);
  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
    };
  }, []);

  async function handleSubmit(event: FormEvent) {
    event.preventDefault();
    setSubmitting(true);
    setFormErrors([]);
    try {
      await api.post(`/workspaces/${workspaceId}/credenciais`, {
        perfil,
        platform_username: platformUsername,
        token,
        scopes: scopes.trim().length > 0 ? scopes : undefined,
      });
      if (!mountedRef.current) return;
      onSaved();
    } catch (error) {
      if (!mountedRef.current) return;
      if (error instanceof ApiError && error.status === 422) {
        const body = error.body as { errors?: string[] } | null;
        setFormErrors(body?.errors ?? ['Não foi possível cadastrar a credencial.']);
      } else if (error instanceof ApiError && error.status === 409) {
        setFormErrors(['Já existe um cadastro concorrente para este perfil. Tente novamente.']);
      } else {
        setFormErrors(['Não foi possível cadastrar a credencial. Tente novamente.']);
      }
    } finally {
      if (mountedRef.current) setSubmitting(false);
    }
  }

  return (
    <form onSubmit={handleSubmit} className="credential-inline-form">
      <label htmlFor="credential-username">Usuário na plataforma</label>
      <input id="credential-username" value={platformUsername} onChange={(e) => setPlatformUsername(e.target.value)} required />

      <label htmlFor="credential-token">Token</label>
      <input id="credential-token" type="password" autoComplete="off" value={token} onChange={(e) => setToken(e.target.value)} required />

      <label htmlFor="credential-scopes">Escopos (opcional)</label>
      <input id="credential-scopes" value={scopes} onChange={(e) => setScopes(e.target.value)} placeholder="ex: Contents:RW, PRs:RW" />

      {formErrors.length > 0 && (
        <ul role="alert" className="form-error">
          {formErrors.map((message) => (
            <li key={message}>{message}</li>
          ))}
        </ul>
      )}

      <div className="credential-inline-form-actions">
        <button type="submit" className="btn-primary" disabled={submitting}>
          {submitting ? 'Salvando...' : 'Salvar'}
        </button>
        <button type="button" onClick={onCancel} disabled={submitting}>
          Cancelar
        </button>
      </div>
    </form>
  );
}

function CredentialsSection({ workspaceId }: { workspaceId: number }) {
  const [credentials, setCredentials] = useState<Credential[] | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [editingPerfil, setEditingPerfil] = useState<string | null>(null);
  const [formSuccess, setFormSuccess] = useState<string | null>(null);
  const loadSeq = useRef(0);

  const load = useCallback(async () => {
    const seq = ++loadSeq.current;
    setLoadError(null);
    try {
      const data = await api.get<Credential[]>(`/workspaces/${workspaceId}/credenciais`);
      if (seq !== loadSeq.current) return;
      setCredentials(data);
    } catch {
      if (seq !== loadSeq.current) return;
      setCredentials(null);
      setLoadError('Não foi possível carregar as credenciais. Tente novamente.');
    }
  }, [workspaceId]);

  useEffect(() => {
    setCredentials(null);
    setEditingPerfil(null);
    setFormSuccess(null);
    load();
  }, [load]);

  function handleSaved(perfil: string) {
    setEditingPerfil(null);
    setFormSuccess(`Credencial para ${PERFIL_LABELS[perfil]} cadastrada.`);
    load();
  }

  return (
    <>
      {loadError && (
        <div role="alert">
          <p>{loadError}</p>
          <button type="button" onClick={load}>
            Tentar novamente
          </button>
        </div>
      )}
      {formSuccess && <p role="status">{formSuccess}</p>}

      {credentials !== null && (
        <ul className="credential-list">
          {PERFIS.map(([value, label]) => {
            const active = credentials.find((c) => c.perfil === value && c.status === 'active') ?? null;
            const isEditing = editingPerfil === value;
            return (
              <li key={value} className={`credential-list-item${active ? ' credential-list-item--active' : ' credential-list-item--pending'}`}>
                <div className="credential-row">
                  <div>
                    <p className="credential-perfil">{label}</p>
                    {active ? (
                      <>
                        <p className="credential-detail">
                          {active.platformUsername} · {STATUS_LABELS[active.status]}
                        </p>
                        {active.scopes && <p className="credential-detail">Escopos: {active.scopes}</p>}
                      </>
                    ) : (
                      <p className="credential-detail">Não cadastrado</p>
                    )}
                  </div>
                  {!isEditing && (
                    <button type="button" onClick={() => setEditingPerfil(value)}>
                      {active ? 'Rotacionar' : 'Cadastrar'}
                    </button>
                  )}
                </div>

                {isEditing && (
                  <InlineCredentialForm
                    perfil={value}
                    workspaceId={workspaceId}
                    onSaved={() => handleSaved(value)}
                    onCancel={() => setEditingPerfil(null)}
                  />
                )}
              </li>
            );
          })}
        </ul>
      )}
    </>
  );
}

// ---------------------------------------------------------------------------
// 3. Design System - gate-ux-figma.md seção 4.2: trigger independente do gate por-spec, a partir do
//    assessment. Gera 3 alternativas (perfil Hermes `ux`), o operador seleciona uma ou pede pra renovar.
// ---------------------------------------------------------------------------

interface DesignSystemProposal {
  id: number;
  nome: string;
  paleta: string[];
  tipografia: string;
  estilo: string;
  justificativa: string;
  selecionado: boolean;
}

type ExploreJobStatus = { status: 'done'; proposals: DesignSystemProposal[] } | { status: 'error' } | { status: 'pending' };

function DesignSystemSection({ workspaceId }: { workspaceId: number }) {
  const [proposals, setProposals] = useState<DesignSystemProposal[] | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [exploring, setExploring] = useState(false);
  const [exploreError, setExploreError] = useState<string | null>(null);
  const pollSeq = useRef(0);

  const load = useCallback(async () => {
    try {
      const data = await api.get<DesignSystemProposal[]>(`/workspaces/${workspaceId}/design-system/proposals`);
      setProposals(data);
      setLoadError(null);
    } catch {
      setLoadError('Não foi possível carregar as propostas de design system.');
    }
  }, [workspaceId]);

  useEffect(() => {
    setProposals(null);
    setLoadError(null);
    load();
  }, [load]);

  async function handleExplore() {
    setExploring(true);
    setExploreError(null);
    const seq = ++pollSeq.current;
    try {
      const { requestId } = await api.post<{ requestId: string }>(`/workspaces/${workspaceId}/design-system/explore`, {});
      while (true) {
        await new Promise((resolve) => setTimeout(resolve, 1500));
        if (seq !== pollSeq.current) return;
        const job = await api.get<ExploreJobStatus>(`/workspaces/${workspaceId}/design-system/explore/${requestId}`);
        if (job.status === 'done') {
          if (seq !== pollSeq.current) return;
          await load();
          setExploring(false);
          return;
        }
        if (job.status === 'error') {
          if (seq !== pollSeq.current) return;
          setExploreError('Não foi possível gerar as alternativas de design system.');
          setExploring(false);
          return;
        }
      }
    } catch {
      if (seq !== pollSeq.current) return;
      setExploreError('Não foi possível gerar as alternativas de design system.');
      setExploring(false);
    }
  }

  async function handleSelect(proposalId: number) {
    try {
      await api.post<DesignSystemProposal>(`/workspaces/${workspaceId}/design-system/proposals/${proposalId}/select`, {});
      await load();
    } catch {
      setLoadError('Não foi possível selecionar essa alternativa. Tente novamente.');
    }
  }

  const all = proposals ?? [];
  const selected = all.find((p) => p.selecionado) ?? null;

  return (
    <div className="design-system-section">
      {loadError && <p role="alert">{loadError}</p>}
      {selected && (
        <p className="design-system-selected">
          Selecionado: <strong>{selected.nome}</strong>
        </p>
      )}

      <button type="button" className="btn-primary" onClick={handleExplore} disabled={exploring}>
        {exploring ? 'Gerando...' : all.length > 0 ? 'Gerar novamente' : 'Explorar design system'}
      </button>
      {exploreError && <p role="alert">{exploreError}</p>}

      {/* Todas as alternativas (selecionada incluída) ficam sempre visíveis na mesma lista - trocar a
          seleção só troca qual card tem o selo "Selecionada", nenhum card aparece/desaparece da lista.
          Antes disso, trocar a seleção fazia a alternativa antes selecionada "reaparecer" no lugar da
          nova, o que parecia (incorretamente) uma geração nova. */}
      {all.length > 0 && (
        <ul className="design-system-options">
          {all.map((p) => (
            <li key={p.id} className={`design-system-option-card${p.selecionado ? ' design-system-option-card--selected' : ''}`}>
              <h3>{p.nome}</h3>
              <div className="design-system-palette">
                {p.paleta.map((cor, i) => (
                  <span key={i} className="design-system-swatch" style={{ backgroundColor: cor }} title={cor} />
                ))}
              </div>
              <p>
                <strong>Tipografia:</strong> {p.tipografia}
              </p>
              <p>
                <strong>Estilo:</strong> {p.estilo}
              </p>
              <p className="design-system-justificativa">{p.justificativa}</p>
              {p.selecionado ? (
                <span className="design-system-option-badge">✓ Selecionada</span>
              ) : (
                <button type="button" onClick={() => handleSelect(p.id)}>
                  Selecionar
                </button>
              )}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------
// 4. SectionAccordion - the same collapsible pattern WorkspaceDetailsSection already used for its own
//    "Novo workspace"/name box, promoted to every top-level region of this page (Assessment, Credenciais)
//    so each one reads as its own clearly delimited block instead of a heading followed by loose content.
// ---------------------------------------------------------------------------

function SectionAccordion({ title, children }: { title: string; children: ReactNode }) {
  const [open, setOpen] = useState(true);
  return (
    <details className="section-accordion" open={open} onToggle={(e) => setOpen(e.currentTarget.open)}>
      <summary>
        <h2>{title}</h2>
      </summary>
      <div className="section-accordion-body">{children}</div>
    </details>
  );
}

// ---------------------------------------------------------------------------

export function WorkspacePage() {
  const { workspaceId, setWorkspaceId } = useWorkspace();

  function handleWorkspaceCreated(workspace: { id: number; name: string }) {
    setWorkspaceId(workspace.id);
  }

  return (
    <section className="workspace-page">
      <h1>Workspace</h1>

      <WorkspaceSection workspaceId={workspaceId} onWorkspaceCreated={handleWorkspaceCreated} />

      {workspaceId !== null && (
        <SectionAccordion title="Design System">
          <DesignSystemSection workspaceId={workspaceId} />
        </SectionAccordion>
      )}

      {workspaceId !== null && (
        <SectionAccordion title="Credenciais">
          <CredentialsSection workspaceId={workspaceId} />
        </SectionAccordion>
      )}
    </section>
  );
}
