# SDLC Dashboard — Hermes

Frontend operacional do pipeline SDLC Hermes: assessment, gênese de ciclo, credenciais de perfil e dashboard agregado.

## Stack
- Frontend: React + TypeScript + Vite, mobile-first
- Backend: .NET 8 Minimal API + EF Core SQLite

## Desenvolvimento

A API é protegida por `X-API-Key` e `X-Tenant-Id`. Ela falha fechada: configure `Security:ApiKey` e ao menos uma origem em `Security:AllowedOrigins` (variáveis de ambiente equivalentes: `Security__ApiKey` e `Security__AllowedOrigins__0`) antes de iniciar.

```bash
cd backend && dotnet run
cd frontend && npm install && npm run dev
```

A API usa SQLite em `backend/data/dashboard.db`. Integrações de plataforma e secrets manager são abstraídas por interfaces; a implementação local de desenvolvimento não persiste tokens.
