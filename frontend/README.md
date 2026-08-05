# Frontend operacional SDLC Hermes

Aplicação React/Vite mobile-first para assessment, specs, dashboard de pipeline e cadastro de credenciais.

## Desenvolvimento

```bash
cd frontend
cp .env.example .env
npm install
npm run dev
npm test
```

A API recebe `X-API-Key` e `X-Tenant-Id` em todas as chamadas. A chave é lida somente de `VITE_API_KEY`; o valor real nunca deve ser commitado. O uso de uma API key compartilhada é uma simplificação conhecida do protótipo, não a autenticação final.
