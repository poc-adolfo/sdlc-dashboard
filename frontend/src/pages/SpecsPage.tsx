import { useCallback, useEffect, useState } from 'react';
import { api, ApiError } from '../api/client';
import { useWorkspace } from '../workspace/WorkspaceContext';

interface SpecListItem {
  path: string;
  title: string;
  status: string;
  version: number;
  updatedAt: string;
  subirUsPath: string;
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

// {subirUsPath} can contain "/" for nested paths - that's the point of the catch-all route it targets
// (SpecUsEndpoints), but individual segments still need escaping (spaces, accents, ...).
function subirUsUrl(workspaceId: number, subirUsPath: string): string {
  const encoded = subirUsPath.split('/').map(encodeURIComponent).join('/');
  return `/workspaces/${workspaceId}/specs/${encoded}/subir-us`;
}

export function SpecsPage() {
  const { workspaceId } = useWorkspace();
  const [specs, setSpecs] = useState<SpecListItem[] | null>(null);
  const [loading, setLoading] = useState(false);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [outcomes, setOutcomes] = useState<Record<string, SubirUsOutcome>>({});
  const [submittingPath, setSubmittingPath] = useState<string | null>(null);

  const loadSpecs = useCallback(async () => {
    if (workspaceId === null) return;
    setLoading(true);
    setLoadError(null);
    try {
      const data = await api.get<SpecListItem[]>(`/workspaces/${workspaceId}/specs?status=rascunho`);
      setSpecs(data);
    } catch (error) {
      setSpecs(null);
      setLoadError(error instanceof ApiError && error.status === 502 ? 'Não foi possível acessar o repositório de specs.' : 'Não foi possível carregar as specs. Tente novamente.');
    } finally {
      setLoading(false);
    }
  }, [workspaceId]);

  // seção 5.2: this workspace's own listing - a workspace switch elsewhere in the app (Layout's
  // picker) must not leave a stale listing/outcomes from the previous one on screen (QA finding on
  // PR #22, same class of bug in AssessmentPage).
  useEffect(() => {
    setSpecs(null);
    setOutcomes({});
    loadSpecs();
  }, [loadSpecs]);

  async function handleSubirUs(spec: SpecListItem) {
    if (workspaceId === null) return;
    setSubmittingPath(spec.path);
    try {
      const result = await api.post<SubirUsSuccessResponse | SubirUsPendingResponse>(subirUsUrl(workspaceId, spec.subirUsPath));
      if ('dor_atendido' in result) {
        setOutcomes((prev) => ({ ...prev, [spec.path]: { kind: 'pendencias', pendencias: result.pendencias } }));
      } else {
        setOutcomes((prev) => ({ ...prev, [spec.path]: { kind: 'success', externalRef: result.pipeline_instance.externalRef } }));
      }
    } catch (error) {
      const message = error instanceof ApiError && error.status === 502 ? 'Falha ao publicar no repositório. Tente novamente mais tarde.' : 'Não foi possível subir a US. Tente novamente.';
      setOutcomes((prev) => ({ ...prev, [spec.path]: { kind: 'error', message } }));
    } finally {
      setSubmittingPath(null);
    }
  }

  if (workspaceId === null) {
    return (
      <section>
        <h1>Specs</h1>
        <p>Selecione um workspace acima para começar.</p>
      </section>
    );
  }

  return (
    <section>
      <h1>Specs em rascunho</h1>

      {loading && <p role="status">Carregando...</p>}
      {loadError && (
        <div role="alert">
          <p>{loadError}</p>
          <button type="button" onClick={loadSpecs}>
            Tentar novamente
          </button>
        </div>
      )}

      {specs !== null && specs.length === 0 && !loading && <p>Nenhuma spec em rascunho encontrada.</p>}

      {specs !== null && specs.length > 0 && (
        <ul className="spec-list">
          {specs.map((spec) => {
            const outcome = outcomes[spec.path];
            const alreadyPublished = outcome?.kind === 'success';
            return (
              <li key={spec.path} className="spec-list-item">
                <p className="spec-title">{spec.title}</p>
                <p className="spec-path">{spec.path}</p>
                <button type="button" onClick={() => handleSubirUs(spec)} disabled={submittingPath === spec.path || alreadyPublished}>
                  {submittingPath === spec.path ? 'Enviando...' : alreadyPublished ? 'US enviada' : 'Subir US'}
                </button>

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
              </li>
            );
          })}
        </ul>
      )}
    </section>
  );
}
