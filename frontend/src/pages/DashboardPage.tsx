import { useCallback, useEffect, useRef, useState } from 'react';
import { api } from '../api/client';
import { useWorkspace } from '../workspace/WorkspaceContext';

interface GatePendente {
  pipeline_instance_id: number;
  external_ref: string;
  fase_atual: string;
  transicao: string;
  aprovador_esperado: string;
  deep_link: string | null;
}

interface DashboardResponse {
  contagens: Record<string, number>;
  gates_pendentes: GatePendente[];
}

// Fixed order + Portuguese display labels for the 7 fases (tabela da seção 3 de
// especificacao-hermes-sdlc.md) - the backend's PipelinePhase enum names (seção 6/DashboardEndpoints.cs)
// aren't meant for display as-is (e.g. "CodeReview", "Qa", "Seguranca" without the accent).
const PHASES: readonly [key: string, label: string][] = [
  ['Requisitos', 'Requisitos'],
  ['Design', 'Design'],
  ['Dev', 'Dev'],
  ['CodeReview', 'Code Review'],
  ['Qa', 'QA'],
  ['Seguranca', 'Segurança'],
  ['Deploy', 'Deploy'],
];
const PHASE_LABELS: Record<string, string> = Object.fromEntries(PHASES);

export function DashboardPage() {
  const { workspaceId } = useWorkspace();
  const [data, setData] = useState<DashboardResponse | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // Same request-sequence guard as AssessmentPage/SpecsPage (Revisor/QA findings on PR #22/#24): a
  // slower response for a workspace switched away from must not overwrite what's on screen.
  const loadSeq = useRef(0);

  const load = useCallback(async () => {
    // QA finding on PR #25: bump the sequence *before* the early return, not after - otherwise
    // deselecting the workspace (workspaceId becomes null) never invalidates whatever load was still
    // in flight for the previous one, and its late response could still pass the seq check below.
    const seq = ++loadSeq.current;
    if (workspaceId === null) return;
    setLoading(true);
    setError(null);
    try {
      const result = await api.get<DashboardResponse>(`/workspaces/${workspaceId}/dashboard`);
      if (seq !== loadSeq.current) return;
      setData(result);
    } catch {
      if (seq !== loadSeq.current) return;
      setData(null);
      setError('Não foi possível carregar o dashboard. Tente novamente.');
    } finally {
      if (seq === loadSeq.current) setLoading(false);
    }
  }, [workspaceId]);

  useEffect(() => {
    setData(null);
    load();
  }, [load]);

  if (workspaceId === null) {
    return (
      <section>
        <h1>Dashboard</h1>
        <p>Selecione um workspace acima para começar.</p>
      </section>
    );
  }

  return (
    <section>
      <h1>Dashboard</h1>

      {loading && <p role="status">Carregando...</p>}
      {error && (
        <div role="alert">
          <p>{error}</p>
          <button type="button" onClick={load}>
            Tentar novamente
          </button>
        </div>
      )}

      {data && (
        <>
          {/* seção 6.3: contagem agregada por fase, não uma visão card a card. */}
          <dl className="phase-counts">
            {PHASES.map(([key, label]) => (
              <div key={key} className="phase-count">
                <dt>{label}</dt>
                <dd>{data.contagens[key] ?? 0}</dd>
              </div>
            ))}
          </dl>

          <h2>Gates pendentes</h2>
          {data.gates_pendentes.length === 0 ? (
            <p>Nenhum gate pendente.</p>
          ) : (
            <ul className="gate-list">
              {data.gates_pendentes.map((gate) => (
                <li key={gate.pipeline_instance_id} className="gate-list-item">
                  <p className="gate-transicao">{gate.transicao}</p>
                  <p className="gate-detail">
                    {PHASE_LABELS[gate.fase_atual] ?? gate.fase_atual} · #{gate.external_ref}
                  </p>
                  <p className="gate-detail">Aguardando: {gate.aprovador_esperado}</p>
                  {/* seção 6.4: o dashboard nunca implementa um botão de aprovação próprio - só um
                      deep-link para onde a aprovação de fato acontece (ou texto descritivo quando não
                      há um link real ainda, seção 6.1). */}
                  {gate.deep_link ? (
                    <a href={gate.deep_link} target="_blank" rel="noreferrer">
                      Ver PR/review
                    </a>
                  ) : (
                    <p className="gate-detail">Mecanismo de aprovação ainda não definido.</p>
                  )}
                </li>
              ))}
            </ul>
          )}
        </>
      )}
    </section>
  );
}
