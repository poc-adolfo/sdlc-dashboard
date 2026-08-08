// Thin fetch wrapper for the sdlc-dashboard backend (frontend-operacional-sdlc-hermes.md seção 14).
//
// Requests use relative paths and `credentials: 'include'` so the session cookie (HttpOnly, seção 11)
// is sent automatically - there is no token for this code to hold. In dev, Vite's server.proxy
// (vite.config.ts) forwards the known API path prefixes to the backend; in production the frontend
// build is served from the same origin as the backend (item 17), so no CORS configuration is needed
// either way.

export class ApiError extends Error {
  readonly status: number;
  readonly body: unknown;

  constructor(status: number, body: unknown) {
    super(`API request failed with status ${status}`);
    this.status = status;
    this.body = body;
  }
}

// Raised specifically for 401 so callers (AuthContext) can react to "session no longer valid"
// without string-matching on ApiError.
export class UnauthorizedError extends ApiError {
  constructor(body: unknown) {
    super(401, body);
  }
}

async function parseBody(response: Response): Promise<unknown> {
  const text = await response.text();
  if (text.length === 0) return null;
  try {
    return JSON.parse(text);
  } catch {
    return text;
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(path, {
    ...init,
    credentials: 'include',
    headers: {
      ...(init?.body ? { 'Content-Type': 'application/json' } : {}),
      ...init?.headers,
    },
  });

  const body = await parseBody(response);

  if (response.status === 401) throw new UnauthorizedError(body);
  if (!response.ok) throw new ApiError(response.status, body);

  return body as T;
}

export const api = {
  get: <T>(path: string) => request<T>(path),
  post: <T>(path: string, body?: unknown) =>
    request<T>(path, { method: 'POST', body: body !== undefined ? JSON.stringify(body) : undefined }),
  patch: <T>(path: string, body?: unknown) =>
    request<T>(path, { method: 'PATCH', body: body !== undefined ? JSON.stringify(body) : undefined }),
};
