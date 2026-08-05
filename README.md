# SDLC Dashboard — Hermes

Frontend operacional do pipeline SDLC Hermes: assessment, gênese de ciclo, credenciais de perfil e dashboard agregado.

## Stack
- Frontend: React + TypeScript + Vite, mobile-first
- Backend: .NET 8 Minimal API + EF Core SQLite

## Desenvolvimento

```bash
cd backend && dotnet run
cd frontend && npm install && npm run dev
```

A API usa SQLite em `backend/data/dashboard.db`. Integrações de plataforma e secrets manager são abstraídas por interfaces; a implementação local de desenvolvimento não persiste tokens.
