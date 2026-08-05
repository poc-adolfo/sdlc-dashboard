# Testes de integração

O projeto de testes usa `WebApplicationFactory<Program>` para exercitar a API via `HttpClient` real. Os cenários devem configurar SQLite isolado, enviar `X-Api-Key` + `X-Tenant-Id`, verificar 401 sem credenciais e confirmar que um workspace de outro tenant retorna 404/não aparece na listagem. Os contratos de credencial também devem afirmar que o JSON não contém `token`.
