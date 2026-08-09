import { useCallback, useEffect, useRef, useState, type FormEvent } from 'react';
import { api, ApiError } from '../api/client';
import { useWorkspace } from '../workspace/WorkspaceContext';

interface Credential {
  id: number;
  perfil: string;
  platformUsername: string;
  scopes: string | null;
  status: 'active' | 'revoked' | 'pending';
  createdAt: string;
  rotatedAt: string | null;
}

// seção 8: os 7 perfis Hermes do pipeline.
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

export function CredentialsPage() {
  const { workspaceId } = useWorkspace();
  const [credentials, setCredentials] = useState<Credential[] | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const loadSeq = useRef(0);

  const [perfil, setPerfil] = useState(PERFIS[0][0]);
  const [platformUsername, setPlatformUsername] = useState('');
  const [token, setToken] = useState('');
  const [scopes, setScopes] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [formErrors, setFormErrors] = useState<string[]>([]);
  const [formSuccess, setFormSuccess] = useState<string | null>(null);

  const load = useCallback(async () => {
    // Same request-sequence guard as the other screens (Revisor/QA findings on PR #22/#24/#25): bump
    // before the early return too, so deselecting the workspace still invalidates whatever load was in
    // flight for the previous one.
    const seq = ++loadSeq.current;
    if (workspaceId === null) return;
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
    setFormSuccess(null);
    setFormErrors([]);
    load();
  }, [load]);

  async function handleSubmit(event: FormEvent) {
    event.preventDefault();
    if (workspaceId === null) return;
    setSubmitting(true);
    setFormErrors([]);
    setFormSuccess(null);
    try {
      await api.post(`/workspaces/${workspaceId}/credenciais`, {
        perfil,
        platform_username: platformUsername,
        token,
        scopes: scopes.trim().length > 0 ? scopes : undefined,
      });
      // Write-only (seção 8): the token never lingers in state longer than the single request that
      // sends it, and is never shown again anywhere in this UI.
      setToken('');
      setPlatformUsername('');
      setScopes('');
      setFormSuccess(`Credencial para ${PERFIL_LABELS[perfil]} cadastrada.`);
      await load();
    } catch (error) {
      if (error instanceof ApiError && error.status === 422) {
        const body = error.body as { errors?: string[] } | null;
        setFormErrors(body?.errors ?? ['Não foi possível cadastrar a credencial.']);
      } else if (error instanceof ApiError && error.status === 409) {
        setFormErrors(['Já existe um cadastro concorrente para este perfil. Tente novamente.']);
      } else {
        setFormErrors(['Não foi possível cadastrar a credencial. Tente novamente.']);
      }
    } finally {
      setSubmitting(false);
    }
  }

  if (workspaceId === null) {
    return (
      <section>
        <h1>Credenciais</h1>
        <p>Selecione um workspace acima para começar.</p>
      </section>
    );
  }

  return (
    <section>
      <h1>Credenciais</h1>

      <form onSubmit={handleSubmit} className="credential-form">
        <label htmlFor="credential-perfil">Perfil</label>
        <select id="credential-perfil" value={perfil} onChange={(e) => setPerfil(e.target.value)}>
          {PERFIS.map(([value, label]) => (
            <option key={value} value={value}>
              {label}
            </option>
          ))}
        </select>

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
        {formSuccess && <p role="status">{formSuccess}</p>}

        <button type="submit" disabled={submitting}>
          {submitting ? 'Cadastrando...' : 'Cadastrar / rotacionar'}
        </button>
      </form>

      <h2>Cadastradas</h2>
      {loadError && (
        <div role="alert">
          <p>{loadError}</p>
          <button type="button" onClick={load}>
            Tentar novamente
          </button>
        </div>
      )}
      {credentials !== null && credentials.length === 0 && <p>Nenhuma credencial cadastrada ainda.</p>}
      {credentials !== null && credentials.length > 0 && (
        <ul className="credential-list">
          {credentials.map((credential) => (
            <li key={credential.id} className={`credential-list-item credential-list-item--${credential.status}`}>
              <p className="credential-perfil">{PERFIL_LABELS[credential.perfil] ?? credential.perfil}</p>
              <p className="credential-detail">
                {credential.platformUsername} · {STATUS_LABELS[credential.status]}
              </p>
              {credential.scopes && <p className="credential-detail">Escopos: {credential.scopes}</p>}
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
