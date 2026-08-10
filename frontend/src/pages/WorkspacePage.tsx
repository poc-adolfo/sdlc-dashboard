import { useCallback, useEffect, useRef, useState, type FormEvent } from 'react';
import { api, ApiError } from '../api/client';
import { useWorkspace, useWorkspaceList } from '../workspace/WorkspaceContext';

// seção 4/5.1/8: onboarding de um workspace consolidado numa tela só - dados do workspace, assessment e
// credenciais de perfil, empilhados em vez de 3 telas separadas que o operador tinha que lembrar de
// visitar em sequência. Sem workspace selecionado, só a primeira seção (criação) aparece; as outras duas
// precisam de um workspace já existente (assessment/credencial são sempre de um workspace).

// ---------------------------------------------------------------------------
// 1. Dados do workspace - cria quando não há workspace selecionado, edita quando há.
// ---------------------------------------------------------------------------

interface WorkspaceDetails {
  id: number;
  name: string;
  platform: 'github' | 'azure_devops';
  platformRef: string;
}

function WorkspaceDetailsSection({
  workspaceId,
  onCreated,
}: {
  workspaceId: number | null;
  onCreated: (workspace: { id: number; name: string }) => void;
}) {
  const { refresh: refreshWorkspaceList } = useWorkspaceList();
  const [name, setName] = useState('');
  const [platform, setPlatform] = useState<'github' | 'azure_devops'>('github');
  const [platformRef, setPlatformRef] = useState('');
  const [loadingExisting, setLoadingExisting] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);
  // Same request-sequence guard used throughout this app: a slower response for a workspace switched
  // away from must not overwrite the fields for whichever one is on screen now.
  const loadSeq = useRef(0);

  useEffect(() => {
    setError(null);
    setSaved(false);
    if (workspaceId === null) {
      setName('');
      setPlatform('github');
      setPlatformRef('');
      return;
    }
    const seq = ++loadSeq.current;
    setLoadingExisting(true);
    api.get<WorkspaceDetails>(`/workspaces/${workspaceId}`).then(
      (data) => {
        if (seq !== loadSeq.current) return;
        setName(data.name);
        setPlatform(data.platform);
        setPlatformRef(data.platformRef);
        setLoadingExisting(false);
      },
      () => {
        if (seq !== loadSeq.current) return;
        setLoadingExisting(false);
      },
    );
  }, [workspaceId]);

  async function handleSubmit(event: FormEvent) {
    event.preventDefault();
    setSubmitting(true);
    setError(null);
    setSaved(false);
    try {
      if (workspaceId === null) {
        const workspace = await api.post<{ id: number; name: string }>('/workspaces', { name, platform, platform_ref: platformRef });
        await refreshWorkspaceList();
        onCreated(workspace);
      } else {
        await api.patch(`/workspaces/${workspaceId}`, { name, platform, platform_ref: platformRef });
        await refreshWorkspaceList();
        setSaved(true);
      }
    } catch (err) {
      // seção 4: platform/platform_ref ficam travados depois que já existe pipeline_instance vinculada -
      // o backend rejeita com 409, o formulário só repassa esse motivo em vez de adivinhar de antemão.
      setError(
        err instanceof ApiError && err.status === 409
          ? 'Plataforma e repositório não podem ser alterados depois que o ciclo já começou para este workspace.'
          : 'Não foi possível salvar o workspace. Verifique os campos e tente novamente.',
      );
    } finally {
      setSubmitting(false);
    }
  }

  if (workspaceId !== null && loadingExisting) return <p role="status">Carregando workspace...</p>;

  return (
    <form onSubmit={handleSubmit} className="workspace-details-form">
      <label htmlFor="workspace-name">Nome</label>
      <input id="workspace-name" value={name} onChange={(e) => setName(e.target.value)} disabled={submitting} required />

      <label htmlFor="workspace-platform">Plataforma</label>
      <select id="workspace-platform" value={platform} onChange={(e) => setPlatform(e.target.value as 'github' | 'azure_devops')} disabled={submitting}>
        <option value="github">GitHub</option>
        <option value="azure_devops">Azure DevOps</option>
      </select>

      <label htmlFor="workspace-platform-ref">Repositório/Projeto</label>
      <input
        id="workspace-platform-ref"
        value={platformRef}
        onChange={(e) => setPlatformRef(e.target.value)}
        placeholder="org/repo"
        disabled={submitting}
        required
      />

      {error && <p role="alert">{error}</p>}
      {saved && <p role="status">Workspace atualizado.</p>}

      <button type="submit" className="btn-primary" disabled={submitting}>
        {submitting ? 'Salvando...' : workspaceId === null ? 'Criar workspace' : 'Salvar'}
      </button>
    </form>
  );
}

// ---------------------------------------------------------------------------
// 2. Assessment - mesmo comportamento que já existia em AssessmentPage, só sem o guard de "selecione um
//    workspace" (o pai só renderiza esta seção quando já há um).
// ---------------------------------------------------------------------------

interface ClientOption {
  id: number;
  name: string;
}

interface Assessment {
  id: number;
  workspaceId: number;
  clientId: number;
  content: string;
  status: 'em_andamento' | 'concluido';
}

type SelectedClient = { kind: 'existing'; id: number; name: string } | { kind: 'new'; name: string };

type SearchState = 'idle' | 'loading' | 'success' | 'error';

function ClientPicker({ workspaceId, onAssessmentReady }: { workspaceId: number; onAssessmentReady: (assessment: Assessment, client: SelectedClient) => void }) {
  const [query, setQuery] = useState('');
  const [results, setResults] = useState<ClientOption[]>([]);
  const [searchState, setSearchState] = useState<SearchState>('idle');
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // QA finding on PR #22: a naive debounce only cancels the *timer*, not an already-in-flight request -
  // a slower response for an earlier keystroke (e.g. "Ac") can still land after a faster response for a
  // later one ("Acme") and silently overwrite it with stale results. Bumped on every search this effect
  // starts (not just ones that actually fire past the debounce) so a response can tell whether it's
  // still the latest.
  const searchSeq = useRef(0);

  // seção 5.1: combobox "buscar ou criar" - searches as the operator types, debounced so every
  // keystroke doesn't fire a request.
  useEffect(() => {
    const trimmed = query.trim();
    if (trimmed.length === 0) {
      setResults([]);
      setSearchState('idle');
      searchSeq.current += 1;
      return;
    }
    setSearchState('loading');
    const seq = ++searchSeq.current;
    const timer = setTimeout(() => {
      api.get<ClientOption[]>(`/clients?q=${encodeURIComponent(trimmed)}`).then(
        (data) => {
          if (seq !== searchSeq.current) return; // a newer search has since started - discard
          setResults(data);
          setSearchState('success');
        },
        () => {
          if (seq !== searchSeq.current) return;
          setResults([]);
          setSearchState('error');
        },
      );
    }, 300);
    return () => clearTimeout(timer);
  }, [query]);

  async function choose(client: SelectedClient) {
    setSubmitting(true);
    setError(null);
    try {
      const body = client.kind === 'existing' ? { client_id: client.id } : { client_name: client.name };
      const assessment = await api.post<Assessment>(`/workspaces/${workspaceId}/assessments`, body);
      onAssessmentReady(assessment, client);
    } catch {
      setError('Não foi possível salvar o cliente. Tente novamente.');
    } finally {
      setSubmitting(false);
    }
  }

  const trimmedQuery = query.trim();
  const exactMatch = results.some((r) => r.name.toLowerCase() === trimmedQuery.toLowerCase());
  // QA finding on PR #22: offering "criar novo cliente" while the search is still pending, or after it
  // failed, risks creating a duplicate client during what might just be a transient outage or an answer
  // that hasn't arrived yet - only a *completed, successful* search with no exact match may offer it.
  const canOfferCreate = searchState === 'success' && trimmedQuery.length > 0 && !exactMatch;

  return (
    <div>
      <label htmlFor="client-search">Cliente</label>
      <input
        id="client-search"
        value={query}
        onChange={(e) => setQuery(e.target.value)}
        placeholder="Buscar cliente..."
        disabled={submitting}
      />
      {searchState === 'loading' && <p role="status">Buscando...</p>}
      {searchState === 'error' && <p role="alert">Não foi possível buscar clientes. Tente novamente.</p>}
      <ul className="client-results">
        {results.map((client) => (
          <li key={client.id}>
            <button type="button" onClick={() => choose({ kind: 'existing', id: client.id, name: client.name })} disabled={submitting}>
              {client.name}
            </button>
          </li>
        ))}
        {canOfferCreate && (
          <li>
            <button type="button" onClick={() => choose({ kind: 'new', name: trimmedQuery })} disabled={submitting}>
              Criar novo cliente: "{trimmedQuery}"
            </button>
          </li>
        )}
      </ul>
      {error && <p role="alert">{error}</p>}
    </div>
  );
}

function AssessmentSection({ workspaceId }: { workspaceId: number }) {
  const [selectedClient, setSelectedClient] = useState<SelectedClient | null>(null);
  const [assessment, setAssessment] = useState<Assessment | null>(null);
  const [content, setContent] = useState('');
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [concluding, setConcluding] = useState(false);
  const [concluded, setConcluded] = useState(false);
  const [concludeError, setConcludeError] = useState<string | null>(null);

  function resetAssessmentState() {
    setSelectedClient(null);
    setAssessment(null);
    setContent('');
    setSaveError(null);
    setConcluded(false);
    setConcludeError(null);
  }

  // QA finding on PR #22: the workspace picker can change `workspaceId` at any time, but the loaded
  // assessment/client were kept as-is - Salvar/Concluir would then hit the *new* workspaceId while still
  // holding the *old* workspace's assessment id and client. Any workspace change must start over from
  // the client picker, never carry stale state across it.
  useEffect(() => {
    resetAssessmentState();
  }, [workspaceId]);

  function handleAssessmentReady(loaded: Assessment, client: SelectedClient) {
    setSelectedClient(client);
    setAssessment(loaded);
    setContent(loaded.content);
    setConcluded(false);
    setConcludeError(null);
  }

  function trocarCliente() {
    resetAssessmentState();
  }

  async function handleSave(event: FormEvent) {
    event.preventDefault();
    if (!assessment) return;
    setSaving(true);
    setSaveError(null);
    try {
      const updated = await api.post<Assessment>(`/workspaces/${workspaceId}/assessments`, {
        assessment_id: assessment.id,
        client_id: assessment.clientId,
        content,
      });
      setAssessment(updated);
    } catch {
      setSaveError('Não foi possível salvar o assessment. Tente novamente.');
    } finally {
      setSaving(false);
    }
  }

  async function handleConclude() {
    if (!assessment) return;
    setConcluding(true);
    setConcludeError(null);
    setConcluded(false);
    try {
      await api.post<{ concluido: true }>(`/workspaces/${workspaceId}/assessments/${assessment.id}/concluir`);
      setConcluded(true);
    } catch {
      setConcludeError('Não foi possível concluir o assessment. Tente novamente.');
    } finally {
      setConcluding(false);
    }
  }

  return assessment === null ? (
    <ClientPicker workspaceId={workspaceId} onAssessmentReady={handleAssessmentReady} />
  ) : (
    <form onSubmit={handleSave}>
      <p>
        Cliente: <strong>{selectedClient?.name}</strong>{' '}
        <button type="button" className="link-button" onClick={trocarCliente}>
          trocar
        </button>
      </p>

      <label htmlFor="assessment-content">Conteúdo</label>
      <textarea id="assessment-content" value={content} onChange={(e) => setContent(e.target.value)} rows={16} />
      {saveError && <p role="alert">{saveError}</p>}

      <div className="assessment-actions">
        <button type="submit" className="btn-primary" disabled={saving}>
          {saving ? 'Salvando...' : 'Salvar'}
        </button>
        <button type="button" onClick={handleConclude} disabled={concluding || saving}>
          {concluding ? 'Concluindo...' : 'Concluir'}
        </button>
      </div>

      {concludeError && <p role="alert">{concludeError}</p>}
      {concluded && <p role="status">Assessment concluído.</p>}
    </form>
  );
}

// ---------------------------------------------------------------------------
// 3. Credenciais - lista das 7 perfis Hermes (seção 8), cadastradas ou não, com edição inline por linha.
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
  useEffect(() => () => {
    mountedRef.current = false;
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

export function WorkspacePage() {
  const { workspaceId, setWorkspaceId } = useWorkspace();

  function handleCreated(workspace: { id: number; name: string }) {
    setWorkspaceId(workspace.id);
  }

  return (
    <section>
      <h1>Workspace</h1>
      <WorkspaceDetailsSection workspaceId={workspaceId} onCreated={handleCreated} />

      {workspaceId !== null && (
        <>
          <h2>Assessment</h2>
          <AssessmentSection workspaceId={workspaceId} />

          <h2>Credenciais</h2>
          <CredentialsSection workspaceId={workspaceId} />
        </>
      )}
    </section>
  );
}
