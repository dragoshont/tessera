# Tessera — secretless, identity-aware credential broker (.NET 10).
#
# Multi-stage: build the .NET broker with the SDK, build the admin-portal SPA with
# Node, then run both on the ASP.NET runtime. Runs as a non-root user; writes
# nothing to disk (audit -> stdout), so it is compatible with a read-only root
# filesystem + an emptyDir at /tmp. Config + grants are mounted (e.g. from a
# ConfigMap) at /config. The built SPA is baked in at /app/wwwroot and served by
# the broker at / (ADR 0016) when TESSERA_WEB_ROOT points at it.

# ---- build: .NET broker ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against the lock of central package management first (better layer cache).
COPY global.json Directory.Build.props Directory.Packages.props Tessera.slnx ./
COPY src/ ./src/
RUN dotnet restore src/Tessera.Cli/Tessera.Cli.csproj

# Publish framework-dependent (no apphost — run via `dotnet tessera.dll`, portable
# across architectures so the image is trivially multi-arch).
RUN dotnet publish src/Tessera.Cli/Tessera.Cli.csproj \
      -c Release -o /app --no-restore -p:UseAppHost=false

# ---- build: admin-portal SPA ----
# Built from source + the committed lockfile so the image is reproducible and the
# local node_modules/dist never enter the build context (.dockerignore).
FROM node:22-alpine AS web
WORKDIR /web
# Skip Playwright's browser download (a heavy devDependency postinstall we never
# need to *build* the SPA — Playwright is only for local e2e screenshots).
ENV PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
# Vite production build → /web/dist (the same artifact `npm run build` produces).
RUN npm run build

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

ENV DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_EnableDiagnostics=0 \
    TESSERA_SERVER_HOST=0.0.0.0 \
    TESSERA_SERVER_PORT=8080 \
    TESSERA_WEB_ROOT=/app/wwwroot

WORKDIR /app
COPY --from=build /app ./
# Bake the built SPA in; the broker serves it at / when TESSERA_WEB_ROOT is set
# (default above). Unset TESSERA_WEB_ROOT to run API-only.
COPY --from=web /web/dist ./wwwroot

# Run as a non-root UID. The aspnet image already reserves UID 1000; reference it
# numerically (works with or without a passwd entry, and matches the Kubernetes
# securityContext runAsUser: 1000). The broker writes nothing to disk (audit ->
# stdout); /tmp is an emptyDir in the pod.
USER 1000

EXPOSE 8080

# Config + grants are mounted at /config (e.g. from a ConfigMap).
ENTRYPOINT ["dotnet", "/app/tessera.dll", "serve", "--config", "/config/tessera.json", "--grants", "/config/grants.json"]
