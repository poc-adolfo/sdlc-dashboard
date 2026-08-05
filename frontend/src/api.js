const base = (import.meta.env.VITE_API_BASE_URL || '').replace(/\/$/, '');
export const apiConfig = { key: import.meta.env.VITE_API_KEY || '', tenant: import.meta.env.VITE_TENANT_ID || '' };
export async function request(path, options = {}) {
  const response = await fetch(`${base}${path}`, { ...options, headers: { 'Content-Type': 'application/json', 'X-API-Key': apiConfig.key, 'X-Tenant-Id': apiConfig.tenant, ...(options.headers || {}) } });
  if (!response.ok) throw new Error((await response.text()) || `Erro ${response.status}`);
  return response.status === 204 ? null : response.json();
}
export const api = {
  workspaces: () => request('/api/workspaces'),
  createWorkspace: name => request('/api/workspaces', { method: 'POST', body: JSON.stringify({ name }) }),
  assessment: (id, markdown) => request(`/api/workspaces/${id}/assessment`, { method: 'PUT', body: JSON.stringify({ content: markdown }) }),
  specs: id => request(`/api/workspaces/${id}/specs`),
  raiseUs: (id, specId) => request(`/api/workspaces/${id}/specs/${specId}/raise-us`, { method: 'POST' }),
  dashboard: id => request(`/api/workspaces/${id}/dashboard`),
  credential: (id, value) => request(`/api/workspaces/${id}/credentials`, { method: 'POST', body: JSON.stringify(value) })
};
