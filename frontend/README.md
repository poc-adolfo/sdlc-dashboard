# sdlc-dashboard frontend

React (mobile-first) + TypeScript + Vite. See `sdlc-agentico/specs/frontend-operacional-sdlc-hermes.md`
for the product spec this implements.

## Development

```sh
npm install
npm run dev
```

The dev server proxies the API path prefixes from the contract in seção 14 (`/auth`, `/clients`,
`/workspaces`, `/pipeline-instances`) to the backend, so the frontend always calls relative paths and
never needs CORS configuration. The backend has no fixed dev port (`src/Backend.Api` has no
`launchSettings.json`); if `dotnet run` picks something other than `5000`, point the proxy at it with:

```sh
VITE_BACKEND_PORT=5041 npm run dev
```

## Scripts

- `npm run dev` - Vite dev server with the backend proxy.
- `npm run build` - type-check (`tsc -b`) then production build to `dist/`.
- `npm run test` - Vitest (jsdom + Testing Library).
- `npm run lint` - oxlint.

## Structure

- `src/api/client.ts` - fetch wrapper (`credentials: 'include'` for the session cookie, typed
  `ApiError`/`UnauthorizedError`).
- `src/auth/` - session state. There's no whoami endpoint in the API contract, so `AuthProvider` probes
  `GET /clients?q=` once on load to tell "logged in" from "logged out" (the session cookie itself is
  HttpOnly and unreadable from JS).
- `src/components/` - `Layout` (mobile-first shell with a bottom tab bar) and `ProtectedRoute`
  (redirects to `/login` when the session probe fails).
- `src/pages/` - one file per route. `LoginPage` is fully implemented; `AssessmentPage`, `SpecsPage`,
  `DashboardPage`, `CredentialsPage` are placeholders for WBS items 11-14.
