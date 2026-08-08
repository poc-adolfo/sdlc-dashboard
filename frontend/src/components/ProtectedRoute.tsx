import { Navigate, Outlet, useLocation } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';

export function ProtectedRoute() {
  const { status, retry } = useAuth();
  const location = useLocation();

  if (status === 'checking') return <p role="status">Carregando...</p>;

  if (status === 'error') {
    return (
      <div role="alert" className="session-error">
        <p>Não foi possível confirmar sua sessão. Verifique sua conexão e tente novamente.</p>
        <button type="button" onClick={retry}>
          Tentar novamente
        </button>
      </div>
    );
  }

  if (status === 'anonymous') return <Navigate to="/login" state={{ from: location }} replace />;

  return <Outlet />;
}
