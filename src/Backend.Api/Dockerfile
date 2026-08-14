# Build context is the repo root (see deploy/README.md) - needs both Backend.Api and
# Backend.Persistence to restore/publish. Restoring the single Api project (not Backend.sln) is
# deliberate: the .sln also references the test projects under tests/, which aren't copied into this
# stage, so `dotnet restore Backend.sln` fails here with "project file not found" - restoring the Api
# project pulls in its Backend.Persistence project reference without needing the test projects at all.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/Backend.Api/Backend.Api.csproj src/Backend.Api/
COPY src/Backend.Persistence/Backend.Persistence.csproj src/Backend.Persistence/
RUN dotnet restore src/Backend.Api/Backend.Api.csproj
COPY src/ src/
RUN dotnet publish src/Backend.Api/Backend.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
# Non-root: the base image ships a "app" user (uid/gid 1654, confirmed via `id app` on the base image)
# for exactly this purpose since .NET 8.
USER app
COPY --from=build --chown=app:app /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
# No default DB path baked in here - the Deployment sets ConnectionStrings__Default to point at the
# PVC mount (deploy/03-backend.yaml) so the SQLite file survives pod restarts (seção 11: SQLite chosen
# deliberately, no external DB this phase).
EXPOSE 8080
ENTRYPOINT ["dotnet", "Backend.Api.dll"]
