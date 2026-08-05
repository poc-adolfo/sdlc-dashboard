# SDLC Dashboard API

Backend .NET 8 com SQLite e endpoints `/api`. Todos exigem `X-Api-Key` e `X-Tenant-Id`; configure `Auth:ApiKey` por variável de ambiente. Credenciais usam `ISecretStore` e somente a referência do Secret é retornada. Configure `GitHub:Owner`, `GitHub:Repo` e `GitHub:Token` para a gênese de ciclo; `Kubernetes:ApiUrl` e `Analyst:ApiUrl` habilitam as integrações externas.

Executar: `dotnet run --project backend/SdlcDashboard.Api.csproj`.
