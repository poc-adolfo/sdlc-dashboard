import { useEffect, useRef, useState, type FormEvent } from 'react';
import { api, ApiError } from '../api/client';
import { useWorkspace } from '../workspace/WorkspaceContext';

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

type ConcludeResult = { concluido: true } | { concluido: false; pendencias: string[] };

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
  // failed, risks creating a duplicate client during what might just be a transient outage or an
  // answer that hasn't arrived yet - only a *completed, successful* search with no exact match may
  // offer it.
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

export function AssessmentPage() {
  const { workspaceId } = useWorkspace();
  const [selectedClient, setSelectedClient] = useState<SelectedClient | null>(null);
  const [assessment, setAssessment] = useState<Assessment | null>(null);
  const [content, setContent] = useState('');
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [concluding, setConcluding] = useState(false);
  const [concludeResult, setConcludeResult] = useState<ConcludeResult | null>(null);
  const [concludeError, setConcludeError] = useState<string | null>(null);

  function resetAssessmentState() {
    setSelectedClient(null);
    setAssessment(null);
    setContent('');
    setSaveError(null);
    setConcludeResult(null);
    setConcludeError(null);
  }

  // QA finding on PR #22: Layout's workspace picker can change `workspaceId` at any time (it's not
  // scoped to this page), but the loaded assessment/client were kept as-is - Salvar/Concluir would
  // then hit the *new* workspaceId in the URL while still holding the *old* workspace's assessment id
  // and client, updating or concluding the wrong resource. Any workspace change must start over from
  // the client picker, never carry stale state across it.
  useEffect(() => {
    resetAssessmentState();
  }, [workspaceId]);

  function handleAssessmentReady(loaded: Assessment, client: SelectedClient) {
    setSelectedClient(client);
    setAssessment(loaded);
    setContent(loaded.content);
    setConcludeResult(null);
    setConcludeError(null);
  }

  function trocarCliente() {
    resetAssessmentState();
  }

  async function handleSave(event: FormEvent) {
    event.preventDefault();
    if (!workspaceId || !assessment) return;
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
    if (!workspaceId || !assessment) return;
    setConcluding(true);
    setConcludeError(null);
    setConcludeResult(null);
    try {
      const result = await api.post<ConcludeResult>(`/workspaces/${workspaceId}/assessments/${assessment.id}/concluir`);
      setConcludeResult(result);
    } catch (error) {
      // seção 6.1, ponto 5: falha na chamada em si (o gate de DoR do Analista fora do ar) é
      // "tudo-ou-nada" - nada muda de estado, erro reportado para retry manual, não é o mesmo caso
      // que dor_atendido: false (que vem como 200 com pendências, tratado acima).
      setConcludeError(error instanceof ApiError && error.status === 502 ? 'O serviço do Analista está indisponível. Tente novamente mais tarde.' : 'Não foi possível concluir o assessment. Tente novamente.');
    } finally {
      setConcluding(false);
    }
  }

  if (workspaceId === null) {
    return (
      <section>
        <h1>Assessment</h1>
        <p>Selecione um workspace acima para começar.</p>
      </section>
    );
  }

  return (
    <section>
      <h1>Assessment</h1>

      {assessment === null ? (
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
            <button type="submit" disabled={saving}>
              {saving ? 'Salvando...' : 'Salvar'}
            </button>
            <button type="button" onClick={handleConclude} disabled={concluding || saving}>
              {concluding ? 'Concluindo...' : 'Concluir'}
            </button>
          </div>

          {concludeError && <p role="alert">{concludeError}</p>}
          {concludeResult?.concluido === true && <p role="status">Assessment concluído.</p>}
          {concludeResult?.concluido === false && (
            <div role="alert">
              <p>O Analista apontou pendências:</p>
              <ul>
                {concludeResult.pendencias.map((p) => (
                  <li key={p}>{p}</li>
                ))}
              </ul>
            </div>
          )}
        </form>
      )}
    </section>
  );
}
