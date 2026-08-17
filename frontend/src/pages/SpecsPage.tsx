import {
  forwardRef,
  useCallback,
  useEffect,
  useImperativeHandle,
  useRef,
  useState,
  type FormEvent,
  type KeyboardEvent,
  type ReactNode,
} from 'react';
import { useOutletContext } from 'react-router-dom';
import { api, ApiError } from '../api/client';
import { useAuth } from '../auth/AuthContext';
import { useWorkspace } from '../workspace/WorkspaceContext';
import type { LayoutContext } from '../components/Layout';

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

// Ícone do botão "ver" (seção 5.2/5.4) - herda a cor do texto via currentColor, então segue o tema claro/
// escuro automaticamente sem precisar de tokens próprios.
function EyeIcon() {
  return (
    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d="M1 12s4-7 11-7 11 7 11 7-4 7-11 7-11-7-11-7Z" />
      <circle cx="12" cy="12" r="3" />
    </svg>
  );
}

// Status da spec (texto livre vindo do blockquote "> Status: ...", seção 5.2) - um marcador icônico no
// lugar do texto por extenso evita que o nome do arquivo quebre linha em specs com status longo (ex:
// "pronta para revisao"). O texto completo continua acessível via title/aria-label no hover.
const READY_STATUS_HINTS = ['pronta', 'pronto', 'aprovad', 'implementad', 'concluid', 'producao'];

function SpecStatusIcon({ status }: { status: string }) {
  const ready = READY_STATUS_HINTS.some((hint) => status.includes(hint));
  return (
    <span className={`spec-tree-file-status${ready ? ' spec-tree-file-status--ready' : ''}`} title={status} aria-label={`Status: ${status}`}>
      {ready ? (
        <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
          <path d="M20 6 9 17l-5-5" />
        </svg>
      ) : (
        <svg width="8" height="8" viewBox="0 0 8 8" fill="currentColor" aria-hidden="true">
          <circle cx="4" cy="4" r="4" />
        </svg>
      )}
    </span>
  );
}

// ---------------------------------------------------------------------------
// Treeview: projeto -> specs -> *.md - toda a hierarquia de uma vez (escala do piloto é pequena,
// seção 15), em vez do navegador nível-a-nível de antes. Specs de cada projeto carregam em paralelo,
// eager, assim que a lista de projetos chega.
// ---------------------------------------------------------------------------

interface ProjectNode {
  status: 'loading' | 'success' | 'error';
  specs?: SpecFileItem[];
}

export interface SpecTreeHandle {
  openCreateProject: () => void;
}

const SpecTree = forwardRef<
  SpecTreeHandle,
  {
    workspaceId: number;
    selected: { projeto: string; fileName: string } | null;
    onSelectSpec: (projeto: string, fileName: string) => void;
    onViewSpec: (projeto: string, fileName: string) => void;
  }
>(function SpecTree({ workspaceId, selected, onSelectSpec, onViewSpec }, ref) {
  const [projects, setProjects] = useState<string[] | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [nodes, setNodes] = useState<Record<string, ProjectNode>>({});
  const [creatingProject, setCreatingProject] = useState(false);
  const [projectName, setProjectName] = useState('');
  const [projectCreateError, setProjectCreateError] = useState<string | null>(null);
  const [projectSubmitting, setProjectSubmitting] = useState(false);
  const [creatingSpecIn, setCreatingSpecIn] = useState<string | null>(null);
  const [specName, setSpecName] = useState('');
  const [specCreateError, setSpecCreateError] = useState<string | null>(null);
  const [specSubmitting, setSpecSubmitting] = useState(false);
  const loadSeq = useRef(0);

  // "Novo projeto" agora fica no cabeçalho da barra lateral, alinhado ao título "Specs" - a árvore só
  // expõe como abrir o próprio formulário, que continua vivendo aqui perto da lista.
  useImperativeHandle(ref, () => ({
    openCreateProject: () => setCreatingProject(true),
  }));

  const loadSpecsFor = useCallback(
    async (projeto: string) => {
      setNodes((prev) => ({ ...prev, [projeto]: { status: 'loading' } }));
      try {
        const data = await api.get<SpecFileItem[]>(`/workspaces/${workspaceId}/spec-projects/${encodeURIComponent(projeto)}/specs`);
        setNodes((prev) => ({ ...prev, [projeto]: { status: 'success', specs: data } }));
      } catch {
        setNodes((prev) => ({ ...prev, [projeto]: { status: 'error' } }));
      }
    },
    [workspaceId],
  );

  const loadProjects = useCallback(async () => {
    const seq = ++loadSeq.current;
    setLoadError(null);
    try {
      const data = await api.get<string[]>(`/workspaces/${workspaceId}/spec-projects`);
      if (seq !== loadSeq.current) return;
      setProjects(data);
      data.forEach((projeto) => void loadSpecsFor(projeto));
    } catch {
      if (seq !== loadSeq.current) return;
      setProjects(null);
      setLoadError('Não foi possível carregar os projetos. Tente novamente.');
    }
  }, [workspaceId, loadSpecsFor]);

  useEffect(() => {
    setProjects(null);
    setNodes({});
    setCreatingProject(false);
    setCreatingSpecIn(null);
    loadProjects();
  }, [loadProjects]);

  async function handleCreateProject(event: FormEvent) {
    event.preventDefault();
    setProjectSubmitting(true);
    setProjectCreateError(null);
    try {
      await api.post(`/workspaces/${workspaceId}/spec-projects`, { name: projectName });
      const created = projectName;
      setCreatingProject(false);
      setProjectName('');
      await loadProjects();
      await loadSpecsFor(created);
    } catch (error) {
      setProjectCreateError(error instanceof ApiError && error.status === 422 ? 'Nome inválido.' : 'Não foi possível criar o projeto. Tente novamente.');
    } finally {
      setProjectSubmitting(false);
    }
  }

  async function handleCreateSpec(event: FormEvent, projeto: string) {
    event.preventDefault();
    const finalName = specName.trim().endsWith('.md') ? specName.trim() : `${specName.trim()}.md`;
    setSpecSubmitting(true);
    setSpecCreateError(null);
    try {
      await api.put(`/workspaces/${workspaceId}/spec-projects/${encodeURIComponent(projeto)}/specs/${encodeURIComponent(finalName)}`, {
        content: defaultSpecTemplate(specName.trim()),
      });
      setCreatingSpecIn(null);
      setSpecName('');
      await loadSpecsFor(projeto);
      onSelectSpec(projeto, finalName);
    } catch {
      setSpecCreateError('Não foi possível criar a spec. Tente novamente.');
    } finally {
      setSpecSubmitting(false);
    }
  }

  if (loadError) {
    return (
      <div role="alert">
        <p>{loadError}</p>
        <button type="button" onClick={loadProjects}>
          Tentar novamente
        </button>
      </div>
    );
  }

  if (projects === null) return <p role="status">Carregando...</p>;

  return (
    <div className="spec-tree">
      {projects.length === 0 && !creatingProject && (
        <div className="empty-state">
          <p>Nenhum projeto ainda.</p>
          <button type="button" className="btn-primary" onClick={() => setCreatingProject(true)}>
            Novo projeto
          </button>
        </div>
      )}

      {projects.length > 0 && (
        <ul className="spec-tree-projects">
          {projects.map((projeto) => {
            const node = nodes[projeto];
            return (
              <li key={projeto} className="spec-tree-project">
                <p className="spec-tree-node spec-tree-node--project">{projeto}</p>
                <ul className="spec-tree-branch">
                  {node?.status === 'loading' && (
                    <li>
                      <p role="status" className="spec-tree-node">
                        Carregando...
                      </p>
                    </li>
                  )}
                  {node?.status === 'error' && (
                    <li>
                      <div role="alert" className="spec-tree-node">
                        <p>Não foi possível carregar as specs.</p>
                        <button type="button" onClick={() => loadSpecsFor(projeto)}>
                          Tentar novamente
                        </button>
                      </div>
                    </li>
                  )}
                  {node?.status === 'success' && node.specs!.length === 0 && (
                    <li>
                      <p className="spec-tree-node spec-tree-empty">Nenhuma spec ainda.</p>
                    </li>
                  )}
                  {node?.status === 'success' &&
                    node.specs!.map((spec) => {
                      const isSelected = selected?.projeto === projeto && selected.fileName === spec.fileName;
                      return (
                        <li key={spec.fileName} className="spec-tree-file-row">
                          {/* Clicar no nome abre o conteúdo direto no modal (seção 5.2) - o botão "ver"
                              separado virou redundante assim que essa passou a ser a única ação útil
                              pra uma spec já existente na árvore; entrar em modo conversa continua
                              acontecendo automaticamente ao criar uma spec nova (handleCreateSpec
                              abaixo chama onSelectSpec), só não é mais alcançável daqui. */}
                          <button
                            type="button"
                            className={`spec-tree-file${isSelected ? ' spec-tree-file--active' : ''}`}
                            onClick={() => onViewSpec(projeto, spec.fileName)}
                          >
                            {spec.fileName}
                          </button>
                          {/* Coluna própria (não dentro do botão do nome) pra ficar sempre na mesma
                              posição horizontal entre as linhas, independente do tamanho do nome do
                              arquivo - ver .spec-tree-file-row abaixo. */}
                          <span className="spec-tree-file-status-col">{spec.status && <SpecStatusIcon status={spec.status} />}</span>
                        </li>
                      );
                    })}
                  <li>
                    {creatingSpecIn === projeto ? (
                      <form onSubmit={(e) => handleCreateSpec(e, projeto)} className="workspace-details-form">
                        <label htmlFor={`new-spec-name-${projeto}`}>Nome da spec</label>
                        <input
                          id={`new-spec-name-${projeto}`}
                          value={specName}
                          onChange={(e) => setSpecName(e.target.value)}
                          placeholder="minha-spec"
                          disabled={specSubmitting}
                          required
                        />
                        {specCreateError && <p role="alert">{specCreateError}</p>}
                        <div className="credential-inline-form-actions">
                          <button type="submit" className="btn-primary" disabled={specSubmitting}>
                            {specSubmitting ? 'Criando...' : 'Criar'}
                          </button>
                          <button
                            type="button"
                            onClick={() => {
                              setCreatingSpecIn(null);
                              setSpecCreateError(null);
                            }}
                            disabled={specSubmitting}
                          >
                            Cancelar
                          </button>
                        </div>
                      </form>
                    ) : (
                      <button
                        type="button"
                        className="link-button spec-tree-node"
                        onClick={() => {
                          setCreatingSpecIn(projeto);
                          setSpecName('');
                        }}
                      >
                        + Nova spec
                      </button>
                    )}
                  </li>
                </ul>
              </li>
            );
          })}
        </ul>
      )}

      {creatingProject && (
        <form onSubmit={handleCreateProject} className="workspace-details-form">
          <label htmlFor="new-project-name">Nome do projeto</label>
          <input id="new-project-name" value={projectName} onChange={(e) => setProjectName(e.target.value)} disabled={projectSubmitting} required />
          {projectCreateError && <p role="alert">{projectCreateError}</p>}
          <div className="credential-inline-form-actions">
            <button type="submit" className="btn-primary" disabled={projectSubmitting}>
              {projectSubmitting ? 'Criando...' : 'Criar'}
            </button>
            <button
              type="button"
              onClick={() => {
                setCreatingProject(false);
                setProjectCreateError(null);
              }}
              disabled={projectSubmitting}
            >
              Cancelar
            </button>
          </div>
        </form>
      )}
    </div>
  );
});

// ---------------------------------------------------------------------------
// Nível 3: editor de conteúdo + Subir US
// ---------------------------------------------------------------------------

function SpecEditor({
  workspaceId,
  projeto,
  fileName,
  onUpdateInChat,
}: {
  workspaceId: number;
  projeto: string;
  fileName: string;
  onUpdateInChat: (projeto: string, fileName: string) => void;
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
    <div className="spec-editor">
      <h2>{fileName}</h2>
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
            {/* Volta pra conversa com a skill specs (seção 5.2/5.4) - o backend já manda o conteúdo
                salvo desta spec como contexto em toda mensagem do chat, então isso funciona como um
                "continuar de onde parou" em vez de recomeçar do zero. */}
            <button type="button" onClick={() => onUpdateInChat(projeto, fileName)}>
              Atualizar spec
            </button>
          </div>

          {outcome?.kind === 'success' && (
            <div className="subir-us-result subir-us-result--success" role="status">
              <span className="subir-us-result-icon" aria-hidden="true">
                ✓
              </span>
              <div>
                <p className="subir-us-result-title">US criada</p>
                <p className="subir-us-result-detail">#{outcome.externalRef}</p>
              </div>
            </div>
          )}
          {outcome?.kind === 'pendencias' && (
            <div className="subir-us-result subir-us-result--pending" role="alert">
              <span className="subir-us-result-icon" aria-hidden="true">
                !
              </span>
              <div>
                <p className="subir-us-result-title">O Analista apontou pendências</p>
                <ul className="subir-us-result-list">
                  {outcome.pendencias.map((p) => (
                    <li key={p}>{p}</li>
                  ))}
                </ul>
              </div>
            </div>
          )}
          {outcome?.kind === 'error' && (
            <div className="subir-us-result subir-us-result--error" role="alert">
              <span className="subir-us-result-icon" aria-hidden="true">
                ×
              </span>
              <p className="subir-us-result-title">{outcome.message}</p>
            </div>
          )}
        </form>
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Modal genérico (fecha no X, no backdrop ou em Escape) - usado pelo botão "ver" ao lado de cada spec
// na árvore, que agora é a única forma de abrir o conteúdo bruto (a antiga região sempre-visível/
// accordion foi removida - o destaque da tela principal é a conversa, seção 5.2/5.4).
// ---------------------------------------------------------------------------

function Modal({ onClose, children }: { onClose: () => void; children: ReactNode }) {
  useEffect(() => {
    function handleKeyDown(event: globalThis.KeyboardEvent) {
      if (event.key === 'Escape') onClose();
    }
    document.addEventListener('keydown', handleKeyDown);
    return () => document.removeEventListener('keydown', handleKeyDown);
  }, [onClose]);

  // Sem isso, o body por trás do overlay continua rolável (position:fixed não trava scroll de página
  // sozinho) - com conteúdo de spec longo, sobra um scrollbar "fora" da caixa da modal, por trás dela.
  useEffect(() => {
    const previousOverflow = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
    return () => {
      document.body.style.overflow = previousOverflow;
    };
  }, []);

  return (
    <div className="modal-overlay" onClick={onClose}>
      <div className="modal-content" onClick={(e) => e.stopPropagation()}>
        <button type="button" className="modal-close" onClick={onClose} aria-label="Fechar">
          ×
        </button>
        {children}
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------------
// Caixa de chat com a skill "specs" hospedada no Hermes (seção 5.2) - conversa livre e iterativa; a
// skill não tem acesso a ferramentas, então quem grava no blob é sempre o backend, nunca ela
// diretamente. Quando o SOUL decide que a spec está pronta, ele responde com um bloco reconhecido
// (```spec-final```) que o backend detecta, grava no lugar da spec, e devolve `finalized: true` - o chat
// então mostra só "Sua spec ficou pronta." + um botão pra abrir o conteúdo salvo, sem exigir
// copiar/colar manual do operador.
// ---------------------------------------------------------------------------

interface ChatMessage {
  role: 'user' | 'assistant';
  content: string;
  finalized?: boolean;
}

function SpecChatBox({
  workspaceId,
  projeto,
  fileName,
  onViewSpec,
}: {
  workspaceId: number;
  projeto: string;
  fileName: string;
  onViewSpec: (projeto: string, fileName: string) => void;
}) {
  // Rótulos dos balões (seção 5.4): o operador aparece pelo nome de usuário logado em vez de "Você"
  // genérico, e o outro lado é rotulado "Agente" em vez do nome interno da skill ("specs").
  const { username } = useAuth();
  const operatorLabel = username ?? 'Você';
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [draft, setDraft] = useState('');
  const [sending, setSending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const messagesEndRef = useRef<HTMLLIElement>(null);
  // Guarda contra respostas de um envio anterior/de outra spec chegando depois de trocar de spec (ver
  // efeito abaixo) - cada chamada a sendMessages incrementa isso, e o loop de polling abandona assim
  // que deixar de ser o mais recente.
  const pollSeq = useRef(0);

  useEffect(() => {
    setMessages([]);
    setDraft('');
    setError(null);
    pollSeq.current += 1;
  }, [workspaceId, projeto, fileName]);

  // Acompanha a conversa como qualquer chat moderno (seção 5.4) - rola pro fim a cada mensagem nova ou
  // enquanto a skill está "digitando", sem exigir scroll manual do operador.
  useEffect(() => {
    messagesEndRef.current?.scrollIntoView({ block: 'end' });
  }, [messages, sending]);

  // POST só enfileira o pedido (o backend responde na hora com um requestId - a chamada de verdade ao
  // Hermes roda em background) - sem isso, uma resposta rica que passe de ~30s derrubava a conversa com
  // "Não foi possível falar com a skill" mesmo a skill tendo respondido normalmente, só que tarde demais
  // pro HttpClient.Timeout síncrono de antes. Polling aqui troca aquele único request longo por vários
  // curtos, então não há mais timeout no meio do caminho.
  async function sendMessages(nextMessages: ChatMessage[]) {
    const seq = ++pollSeq.current;
    setSending(true);
    setError(null);
    const basePath = `/workspaces/${workspaceId}/spec-projects/${encodeURIComponent(projeto)}/specs/${encodeURIComponent(fileName)}/chat`;
    try {
      const { requestId } = await api.post<{ requestId: string }>(basePath, { messages: nextMessages });
      for (;;) {
        if (seq !== pollSeq.current) return; // a spec mudou enquanto esperava - descarta esta resposta
        const job = await api.get<{ status: 'pending' | 'done' | 'error'; reply?: string; finalized?: boolean }>(`${basePath}/${requestId}`);
        if (seq !== pollSeq.current) return;
        if (job.status === 'pending') {
          await new Promise((resolve) => setTimeout(resolve, 1500));
          continue;
        }
        if (job.status === 'done') {
          setMessages((prev) => [...prev, { role: 'assistant', content: job.reply ?? '', finalized: job.finalized === true }]);
        } else {
          setError('Não foi possível falar com a skill.');
        }
        break;
      }
    } catch {
      if (seq === pollSeq.current) setError('Não foi possível falar com a skill.');
    } finally {
      if (seq === pollSeq.current) setSending(false);
    }
  }

  async function handleSend() {
    const text = draft.trim();
    if (text.length === 0) return;
    const nextMessages: ChatMessage[] = [...messages, { role: 'user', content: text }];
    setMessages(nextMessages);
    setDraft('');
    await sendMessages(nextMessages);
  }

  // O pedido do operador já está em `messages` (a última mensagem, de role "user") - reenvia o mesmo
  // histórico sem duplicá-la.
  function handleRetry() {
    void sendMessages(messages);
  }

  // Mesmo comportamento da caixa de conversa do OpenWebUI (seção 5.4): Enter envia, Shift+Enter quebra
  // linha - o operador não precisa alcançar o botão pra manter o ritmo da conversa.
  function handleKeyDown(event: KeyboardEvent<HTMLTextAreaElement>) {
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault();
      if (!sending && draft.trim().length > 0) void handleSend();
    }
  }

  return (
    // Shell em coluna flex (seção 5.4) - mensagens e composer compartilham a mesma largura/centro,
    // em vez do composer antigo com position:fixed centralizado na viewport inteira (que ficava
    // desalinhado da coluna de conversa sempre que a barra lateral estava aberta).
    <div className="spec-chat-shell">
      {/* Só antes da primeira mensagem (seção 5.4) - o título ocupa o centro da tela, sem borda/caixa
          nenhuma; uma vez que a conversa começa, ele cede lugar às mensagens de verdade. */}
      {messages.length === 0 ? (
        <div className="spec-chat-intro">
          <h2>O que vamos especificar hoje?</h2>
        </div>
      ) : (
        <div className="spec-chat">
          <ul className="spec-chat-messages">
            {messages.map((message, index) => (
              <li key={index} className={`spec-chat-message spec-chat-message--${message.role}`}>
                <span className="spec-chat-message-avatar" aria-hidden="true">
                  {message.role === 'user' ? operatorLabel.slice(0, 2).toUpperCase() : 'Ag'}
                </span>
                <div className="spec-chat-message-body">
                  <span className="spec-chat-message-role">{message.role === 'user' ? operatorLabel : 'Agente'}</span>
                  <p>{message.content}</p>
                  {message.finalized && (
                    <button type="button" className="spec-chat-view-final" onClick={() => onViewSpec(projeto, fileName)}>
                      <EyeIcon /> Visualizar spec
                    </button>
                  )}
                </div>
              </li>
            ))}
            {sending && (
              <li className="spec-chat-message spec-chat-message--assistant spec-chat-message--typing">
                <span className="spec-chat-message-avatar" aria-hidden="true">
                  Ag
                </span>
                <div className="spec-chat-message-body">
                  <span className="spec-chat-message-role">Agente</span>
                  <span className="spec-chat-typing" role="status" aria-label="O agente está digitando">
                    <i />
                    <i />
                    <i />
                  </span>
                </div>
              </li>
            )}
            <li ref={messagesEndRef} className="spec-chat-messages-end" aria-hidden="true" />
          </ul>
        </div>
      )}
      {error && (
        <p role="alert" className="spec-chat-error">
          {error}{' '}
          <button type="button" className="link-button" onClick={handleRetry} disabled={sending}>
            Tente de Novo
          </button>
        </p>
      )}
      <form
        onSubmit={(e) => {
          e.preventDefault();
          void handleSend();
        }}
        className="spec-chat-form"
      >
        {/* O título "O que vamos especificar hoje?" já cumpre o papel do label visível (seção 5.4) -
            "Mensagem" continua existindo só para leitor de tela, via aria-label. */}
        <div className="spec-chat-input-wrap">
          <textarea
            id="spec-chat-input"
            aria-label="Mensagem"
            value={draft}
            onChange={(e) => setDraft(e.target.value)}
            onKeyDown={handleKeyDown}
            rows={2}
            disabled={sending}
          />
          <button
            type="submit"
            className="spec-chat-send"
            disabled={sending || draft.trim().length === 0}
            aria-label={sending ? 'Enviando...' : 'Enviar'}
          >
            {sending ? '…' : '↑'}
          </button>
        </div>
      </form>
    </div>
  );
}

// ---------------------------------------------------------------------------

export function SpecsPage() {
  const { workspaceId } = useWorkspace();
  const { setNavOpen } = useOutletContext<LayoutContext>();
  const [clientId, setClientId] = useState<number | null | undefined>(undefined);
  const [selected, setSelected] = useState<{ projeto: string; fileName: string } | null>(null);
  const [viewing, setViewing] = useState<{ projeto: string; fileName: string } | null>(null);
  const [sidebarOpen, setSidebarOpen] = useState(true);
  const treeRef = useRef<SpecTreeHandle>(null);
  const loadSeq = useRef(0);

  // Escolher uma spec entra no modo conversa (seção 5.2/5.4) - as duas barras laterais (nav principal e
  // a árvore desta página) só disputam espaço com o chat, então recolhem sozinhas nesse momento.
  function selectSpec(projeto: string, fileName: string) {
    setSelected({ projeto, fileName });
    setSidebarOpen(false);
    setNavOpen(false);
  }

  useEffect(() => {
    setSelected(null);
    setViewing(null);
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
    <section className={`specs-page${sidebarOpen ? '' : ' specs-page--sidebar-collapsed'}`}>
      {/* Segunda barra lateral, só desta tela (seção 5.2) - a árvore de projetos/specs fica sempre à
          vista (ou recolhida a um clique), deixando toda a largura restante do viewport para a conversa
          + conteúdo da spec aberta. */}
      <aside className={`specs-sidebar${sidebarOpen ? '' : ' specs-sidebar--collapsed'}`}>
        <div className="specs-sidebar-header">
          {sidebarOpen && <h1>Specs</h1>}
          <div className="specs-sidebar-actions">
            {sidebarOpen && (
              <button
                type="button"
                className="spec-tree-new-project"
                onClick={() => treeRef.current?.openCreateProject()}
                aria-label="Novo projeto"
              >
                +
              </button>
            )}
            <button
              type="button"
              className="link-button specs-sidebar-toggle"
              onClick={() => setSidebarOpen((open) => !open)}
              aria-label={sidebarOpen ? 'Recolher lista de specs' : 'Expandir lista de specs'}
              aria-expanded={sidebarOpen}
            >
              {sidebarOpen ? '‹' : '›'}
            </button>
          </div>
        </div>
        {sidebarOpen && (
          <SpecTree
            ref={treeRef}
            workspaceId={workspaceId}
            selected={selected}
            onSelectSpec={selectSpec}
            onViewSpec={(projeto, fileName) => setViewing({ projeto, fileName })}
          />
        )}
      </aside>

      <div className="specs-main">
        {selected === null ? (
          <p className="specs-main-placeholder">Selecione uma spec ao lado para começar.</p>
        ) : (
          <>
            {/* Com as duas barras recolhidas ao entrar no modo conversa (seção 5.4), o "Specs" do
                cabeçalho some da tela - este breadcrumb assume o papel de título, mostrando qual
                spec está aberta. */}
            <p className="specs-breadcrumb">
              {selected.projeto} / {selected.fileName}
            </p>
            {/* A conversa ocupa toda a região principal agora (seção 5.2/5.4) - o conteúdo bruto só abre
                sob demanda, no modal acionado pelo botão "ver" na árvore. */}
            <SpecChatBox
              workspaceId={workspaceId}
              projeto={selected.projeto}
              fileName={selected.fileName}
              onViewSpec={(projeto, fileName) => setViewing({ projeto, fileName })}
            />
          </>
        )}
      </div>

      {viewing !== null && (
        <Modal onClose={() => setViewing(null)}>
          <SpecEditor
            workspaceId={workspaceId}
            projeto={viewing.projeto}
            fileName={viewing.fileName}
            onUpdateInChat={(projeto, fileName) => {
              setViewing(null);
              selectSpec(projeto, fileName);
            }}
          />
        </Modal>
      )}
    </section>
  );
}
