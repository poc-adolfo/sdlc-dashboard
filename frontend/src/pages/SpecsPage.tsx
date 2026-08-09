import { useCallback, useEffect, useRef, useState, type FormEvent } from 'react';
import { api, ApiError } from '../api/client';
import { useWorkspace } from '../workspace/WorkspaceContext';

// seção 5.2 (atualização 2026-08-09): specs vivem em blob storage agora, não no repositório Git do
// workspace - estrutura {client_id}/{projeto}/{nome_spec}. "projeto" não tem linha no banco, é só um
// prefixo no storage; por isso esta tela precisa navegar em 3 níveis (projeto -> spec -> conteúdo),
// diferente da listagem plana de antes.

interface WorkspaceSummary {
  clientId: number | null;
}

interface SpecFileItem {
  fileName: string;
  title: string;
  status: string | null;
  version: number;
  updatedAt: string | null;
}

interface SubirUsSuccessResponse {
  pipeline_instance: { externalRef: string };
}

interface SubirUsPendingResponse {
  dor_atendido: false;
  pendencias: string[];
}

type SubirUsOutcome =
  | { kind: 'success'; externalRef: string }
  | { kind: 'pendencias'; pendencias: string[] }
  | { kind: 'error'; message: string };

function defaultSpecTemplate(title: string): string {
  const today = new Date().toISOString().slice(0, 10);
  return `# ${title}\n\n> Status: rascunho (${today}).\n\n## User Story\n**Como** , **quero** , **para** \n\n## Criterios de aceite\n- [ ] \n\n## WBS - Plano de implementacao\n1. \n`;
}

// ---------------------------------------------------------------------------
// Nível 1: projetos (prefixos no storage do client_id do workspace)
// ---------------------------------------------------------------------------

function ProjectPicker({ workspaceId, onSelect }: { workspaceId: number; onSelect: (projeto: string) => void }) {
  const [projects, setProjects] = useState<string[] | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);
  const [name, setName] = useState('');
  const [createError, setCreateError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const loadSeq = useRef(0);

  const load = useCallback(async () => {
    const seq = ++loadSeq.current;
    setLoadError(null);
    try {
      const data = await api.get<string[]>(`/workspaces/${workspaceId}/spec-projects`);
      if (seq !== loadSeq.current) return;
      setProjects(data);
    } catch {
      if (seq !== loadSeq.current) return;
      setProjects(null);
      setLoadError('Não foi possível carregar os projetos. Tente novamente.');
    }
  }, [workspaceId]);

  useEffect(() => {
    setProjects(null);
    load();
  }, [load]);

  async function handleCreate(event: FormEvent) {
    event.preventDefault();
    setSubmitting(true);
    setCreateError(null);
    try {
      await api.post(`/workspaces/${workspaceId}/spec-projects`, { name });
      setCreating(false);
      setName('');
      await load();
      onSelect(name);
    } catch (error) {
      setCreateError(error instanceof ApiError && error.status === 422 ? 'Nome inválido.' : 'Não foi possível criar o projeto. Tente novamente.');
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <div>
      <h2>Projeto</h2>
      {loadError && (
        <div role="alert">
          <p>{loadError}</p>
          <button type="button" onClick={load}>
            Tentar novamente
          </button>
        </div>
      )}
      {projects !== null && (
        <>
          {projects.length === 0 && <p>Nenhum projeto ainda.</p>}
          <ul className="spec-list">
            {projects.map((projeto) => (
              <li key={projeto} className="spec-list-item">
                <p className="spec-title">{projeto}</p>
                <button type="button" className="btn-primary" onClick={() => onSelect(projeto)}>
                  Abrir
                </button>
              </li>
            ))}
          </ul>
        </>
      )}

      {!creating && (
        <button type="button" onClick={() => setCreating(true)}>
          Novo projeto
        </button>
      )}
      {creating && (
        <form onSubmit={handleCreate} className="workspace-details-form">
          <label htmlFor="new-project-name">Nome do projeto</label>
          <input id="new-project-name" value={name} onChange={(e) => setName(e.target.value)} disabled={submitting} required />
          {createError && <p role="alert">{createError}</p>}
          <div className="credential-inline-form-actions">
            <button type="submit" className="btn-primary" disabled={submitting}>
              {submitting ? 'Criando...' : 'Criar'}
            </button>
            <button type="button" onClick={() => { setCreating(false); setCreateError(null); }} disabled={submitting}>
              Cancelar
            </button>
          </div>
        </form>
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Nível 2: specs dentro de um projeto
// ---------------------------------------------------------------------------

function SpecFilePicker({
  workspaceId,
  projeto,
  onSelect,
  onTrocarProjeto,
}: {
  workspaceId: number;
  projeto: string;
  onSelect: (fileName: string) => void;
  onTrocarProjeto: () => void;
}) {
  const [specs, setSpecs] = useState<SpecFileItem[] | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);
  const [fileName, setFileName] = useState('');
  const [createError, setCreateError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const loadSeq = useRef(0);

  const load = useCallback(async () => {
    const seq = ++loadSeq.current;
    setLoadError(null);
    try {
      const data = await api.get<SpecFileItem[]>(`/workspaces/${workspaceId}/spec-projects/${encodeURIComponent(projeto)}/specs`);
      if (seq !== loadSeq.current) return;
      setSpecs(data);
    } catch {
      if (seq !== loadSeq.current) return;
      setSpecs(null);
      setLoadError('Não foi possível carregar as specs. Tente novamente.');
    }
  }, [workspaceId, projeto]);

  useEffect(() => {
    setSpecs(null);
    setCreating(false);
    load();
  }, [load]);

  async function handleCreate(event: FormEvent) {
    event.preventDefault();
    const finalName = fileName.trim().endsWith('.md') ? fileName.trim() : `${fileName.trim()}.md`;
    setSubmitting(true);
    setCreateError(null);
    try {
      await api.put(`/workspaces/${workspaceId}/spec-projects/${encodeURIComponent(projeto)}/specs/${encodeURIComponent(finalName)}`, {
        content: defaultSpecTemplate(fileName.trim()),
      });
      setCreating(false);
      setFileName('');
      onSelect(finalName);
    } catch {
      setCreateError('Não foi possível criar a spec. Tente novamente.');
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <div>
      <h2>
        Specs em <strong>{projeto}</strong>{' '}
        <button type="button" className="link-button" onClick={onTrocarProjeto}>
          trocar projeto
        </button>
      </h2>
      {loadError && (
        <div role="alert">
          <p>{loadError}</p>
          <button type="button" onClick={load}>
            Tentar novamente
          </button>
        </div>
      )}
      {specs !== null && (
        <>
          {specs.length === 0 && <p>Nenhuma spec neste projeto ainda.</p>}
          <ul className="spec-list">
            {specs.map((spec) => (
              <li key={spec.fileName} className="spec-list-item">
                <p className="spec-title">{spec.title}</p>
                <p className="spec-path">
                  {spec.fileName}
                  {spec.status ? ` · ${spec.status}` : ''}
                </p>
                <button type="button" className="btn-primary" onClick={() => onSelect(spec.fileName)}>
                  Abrir
                </button>
              </li>
            ))}
          </ul>
        </>
      )}

      {!creating && (
        <button type="button" onClick={() => setCreating(true)}>
          Nova spec
        </button>
      )}
      {creating && (
        <form onSubmit={handleCreate} className="workspace-details-form">
          <label htmlFor="new-spec-name">Nome da spec</label>
          <input id="new-spec-name" value={fileName} onChange={(e) => setFileName(e.target.value)} placeholder="minha-spec" disabled={submitting} required />
          {createError && <p role="alert">{createError}</p>}
          <div className="credential-inline-form-actions">
            <button type="submit" className="btn-primary" disabled={submitting}>
              {submitting ? 'Criando...' : 'Criar'}
            </button>
            <button type="button" onClick={() => { setCreating(false); setCreateError(null); }} disabled={submitting}>
              Cancelar
            </button>
          </div>
        </form>
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Nível 3: editor de conteúdo + Subir US
// ---------------------------------------------------------------------------

function SpecEditor({
  workspaceId,
  projeto,
  fileName,
  onTrocarSpec,
}: {
  workspaceId: number;
  projeto: string;
  fileName: string;
  onTrocarSpec: () => void;
}) {
  const [content, setContent] = useState<string | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);
  const [subirUsState, setSubirUsState] = useState<'idle' | 'submitting'>('idle');
  const [outcome, setOutcome] = useState<SubirUsOutcome | null>(null);
  const loadSeq = useRef(0);

  const basePath = `/workspaces/${workspaceId}/spec-projects/${encodeURIComponent(projeto)}/specs/${encodeURIComponent(fileName)}`;

  useEffect(() => {
    const seq = ++loadSeq.current;
    setContent(null);
    setLoadError(null);
    setSaved(false);
    setOutcome(null);
    api.get<string>(basePath).then(
      (data) => {
        if (seq !== loadSeq.current) return;
        setContent(data);
      },
      () => {
        if (seq !== loadSeq.current) return;
        setLoadError('Não foi possível carregar o conteúdo desta spec. Tente novamente.');
      },
    );
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [basePath]);

  async function handleSave(event: FormEvent) {
    event.preventDefault();
    if (content === null) return;
    setSaving(true);
    setSaveError(null);
    setSaved(false);
    try {
      await api.put(basePath, { content });
      setSaved(true);
    } catch {
      setSaveError('Não foi possível salvar a spec. Tente novamente.');
    } finally {
      setSaving(false);
    }
  }

  async function handleSubirUs() {
    setSubirUsState('submitting');
    setOutcome(null);
    try {
      const result = await api.post<SubirUsSuccessResponse | SubirUsPendingResponse>(`${basePath}/subir-us`);
      if ('dor_atendido' in result) {
        setOutcome({ kind: 'pendencias', pendencias: result.pendencias });
      } else {
        setOutcome({ kind: 'success', externalRef: result.pipeline_instance.externalRef });
      }
    } catch (error) {
      setOutcome({ kind: 'error', message: error instanceof ApiError && error.status === 502 ? 'Falha ao publicar no repositório. Tente novamente mais tarde.' : 'Não foi possível subir a US. Tente novamente.' });
    } finally {
      setSubirUsState('idle');
    }
  }

  return (
    <div>
      <h2>
        {fileName}{' '}
        <button type="button" className="link-button" onClick={onTrocarSpec}>
          trocar spec
        </button>
      </h2>
      {loadError && <p role="alert">{loadError}</p>}
      {content !== null && (
        <form onSubmit={handleSave}>
          <label htmlFor="spec-content">Conteúdo</label>
          <textarea id="spec-content" value={content} onChange={(e) => setContent(e.target.value)} rows={16} />
          {saveError && <p role="alert">{saveError}</p>}
          {saved && <p role="status">Spec salva.</p>}

          <div className="assessment-actions">
            <button type="submit" className="btn-primary" disabled={saving}>
              {saving ? 'Salvando...' : 'Salvar'}
            </button>
            <button type="button" onClick={handleSubirUs} disabled={subirUsState === 'submitting' || outcome?.kind === 'success'}>
              {subirUsState === 'submitting' ? 'Enviando...' : outcome?.kind === 'success' ? 'US enviada' : 'Subir US'}
            </button>
          </div>

          {outcome?.kind === 'success' && <p role="status">US criada: #{outcome.externalRef}</p>}
          {outcome?.kind === 'pendencias' && (
            <div role="alert">
              <p>O Analista apontou pendências:</p>
              <ul>
                {outcome.pendencias.map((p) => (
                  <li key={p}>{p}</li>
                ))}
              </ul>
            </div>
          )}
          {outcome?.kind === 'error' && <p role="alert">{outcome.message}</p>}
        </form>
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Caixa de chat com a skill "specs" hospedada no Hermes (seção 5.2) - conversa livre, nunca escreve no
// storage sozinha; o operador decide o que aproveitar via o editor acima.
// ---------------------------------------------------------------------------

interface ChatMessage {
  role: 'user' | 'assistant';
  content: string;
}

function SpecChatBox({ workspaceId, projeto, fileName }: { workspaceId: number; projeto: string; fileName: string }) {
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [draft, setDraft] = useState('');
  const [sending, setSending] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    setMessages([]);
    setDraft('');
    setError(null);
  }, [workspaceId, projeto, fileName]);

  async function handleSend(event: FormEvent) {
    event.preventDefault();
    const text = draft.trim();
    if (text.length === 0) return;
    const nextMessages: ChatMessage[] = [...messages, { role: 'user', content: text }];
    setMessages(nextMessages);
    setDraft('');
    setSending(true);
    setError(null);
    try {
      const response = await api.post<{ reply: string }>(
        `/workspaces/${workspaceId}/spec-projects/${encodeURIComponent(projeto)}/specs/${encodeURIComponent(fileName)}/chat`,
        { messages: nextMessages },
      );
      setMessages((prev) => [...prev, { role: 'assistant', content: response.reply }]);
    } catch {
      setError('Não foi possível falar com a skill. Tente novamente.');
    } finally {
      setSending(false);
    }
  }

  return (
    <div className="spec-chat">
      <h2>Conversar com a skill specs</h2>
      <ul className="spec-chat-messages">
        {messages.map((message, index) => (
          <li key={index} className={`spec-chat-message spec-chat-message--${message.role}`}>
            <p>{message.content}</p>
          </li>
        ))}
      </ul>
      {error && <p role="alert">{error}</p>}
      <form onSubmit={handleSend} className="spec-chat-form">
        <label htmlFor="spec-chat-input">Mensagem</label>
        <textarea id="spec-chat-input" value={draft} onChange={(e) => setDraft(e.target.value)} rows={2} disabled={sending} />
        <button type="submit" className="btn-primary" disabled={sending || draft.trim().length === 0}>
          {sending ? 'Enviando...' : 'Enviar'}
        </button>
      </form>
    </div>
  );
}

// ---------------------------------------------------------------------------

export function SpecsPage() {
  const { workspaceId } = useWorkspace();
  const [clientId, setClientId] = useState<number | null | undefined>(undefined);
  const [projeto, setProjeto] = useState<string | null>(null);
  const [fileName, setFileName] = useState<string | null>(null);
  const loadSeq = useRef(0);

  useEffect(() => {
    setProjeto(null);
    setFileName(null);
    setClientId(undefined);
    if (workspaceId === null) return;
    const seq = ++loadSeq.current;
    api.get<WorkspaceSummary>(`/workspaces/${workspaceId}`).then(
      (data) => {
        if (seq !== loadSeq.current) return;
        setClientId(data.clientId);
      },
      () => {
        if (seq !== loadSeq.current) return;
        setClientId(null);
      },
    );
  }, [workspaceId]);

  if (workspaceId === null) {
    return (
      <section>
        <h1>Specs</h1>
        <p>Selecione um workspace acima para começar.</p>
      </section>
    );
  }

  if (clientId === undefined) {
    return (
      <section>
        <h1>Specs</h1>
        <p role="status">Carregando...</p>
      </section>
    );
  }

  if (clientId === null) {
    return (
      <section>
        <h1>Specs</h1>
        <p>Conclua o assessment deste workspace antes de acessar as specs (seção "Workspace").</p>
      </section>
    );
  }

  return (
    <section>
      <h1>Specs</h1>

      {projeto === null && <ProjectPicker workspaceId={workspaceId} onSelect={setProjeto} />}

      {projeto !== null && fileName === null && (
        <SpecFilePicker workspaceId={workspaceId} projeto={projeto} onSelect={setFileName} onTrocarProjeto={() => setProjeto(null)} />
      )}

      {projeto !== null && fileName !== null && (
        <>
          <SpecEditor workspaceId={workspaceId} projeto={projeto} fileName={fileName} onTrocarSpec={() => setFileName(null)} />
          <SpecChatBox workspaceId={workspaceId} projeto={projeto} fileName={fileName} />
        </>
      )}
    </section>
  );
}
