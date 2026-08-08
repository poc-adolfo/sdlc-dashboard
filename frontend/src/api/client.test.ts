import { describe, expect, it, vi } from 'vitest';
import { api, ApiError, UnauthorizedError } from './client';

function jsonResponse(status: number, body: unknown) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

describe('api client', () => {
  it('sends credentials: include and a JSON content-type when a body is present', async () => {
    const fetchMock = vi.fn(async (_input: RequestInfo | URL, _init?: RequestInit) => jsonResponse(200, { ok: true }));
    vi.stubGlobal('fetch', fetchMock);

    await api.post('/workspaces', { name: 'Acme' });

    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe('/workspaces');
    expect(init).toBeDefined();
    expect(init!.credentials).toBe('include');
    expect((init!.headers as Record<string, string>)['Content-Type']).toBe('application/json');
    expect(init!.body).toBe(JSON.stringify({ name: 'Acme' }));
  });

  it('returns the parsed JSON body on success', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => jsonResponse(200, { id: 1, name: 'Acme' })),
    );

    const result = await api.get<{ id: number; name: string }>('/workspaces/1');

    expect(result).toEqual({ id: 1, name: 'Acme' });
  });

  it('throws UnauthorizedError specifically on a 401 response', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => jsonResponse(401, { error: 'no session' })),
    );

    await expect(api.get('/workspaces/1')).rejects.toBeInstanceOf(UnauthorizedError);
  });

  it('throws a plain ApiError with the status for any other non-2xx response', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => jsonResponse(404, { error: 'not found' })),
    );

    let error: unknown;
    try {
      await api.get('/workspaces/999');
    } catch (e) {
      error = e;
    }

    expect(error).toBeInstanceOf(ApiError);
    expect(error).not.toBeInstanceOf(UnauthorizedError);
    expect((error as ApiError).status).toBe(404);
    expect((error as ApiError).body).toEqual({ error: 'not found' });
  });
});
