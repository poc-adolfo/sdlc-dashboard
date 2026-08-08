import { Navigate, Outlet, useLocation } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';

export function ProtectedRoute() {
  const { status } = useAuth();
  const location = useLocation();

  if (status === 'checking') return <p role="status">Carregando...</p>;
  if (status === 'anonymous') return <Navigate to="/login" state={{ from: location }} replace />;

  return <Outlet />;
}
