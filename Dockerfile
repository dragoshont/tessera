# Tessera — secretless, identity-aware credential broker (.NET 10).
#
# Multi-stage: build the .NET broker with the SDK, build the admin-portal SPA with
# Node, then run both on the ASP.NET runtime. Runs as a non-root user; product
# state is written only to the explicitly mounted /data volume and audit goes to
# stdout. Config + grants are mounted (e.g.
# from a ConfigMap) at /config. The built SPA is baked in at /app/wwwroot and served
# by the broker at / (ADR 0016) when TESSERA_WEB_ROOT points at it.

# ---- build: .NET broker ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against the lock of central package management first (better layer cache).
COPY global.json Directory.Build.props Directory.Packages.props Tessera.slnx ./
COPY src/ ./src/
RUN dotnet restore src/Tessera.Cli/Tessera.Cli.csproj \
 && dotnet restore src/Tessera.Plugins.Gmail/Tessera.Plugins.Gmail.csproj \
 && dotnet restore src/Tessera.Plugins.GitHub/Tessera.Plugins.GitHub.csproj \
 && dotnet restore src/Tessera.Plugins.OneDrive/Tessera.Plugins.OneDrive.csproj \
 && dotnet restore src/Tessera.Plugins.ReginaMaria/Tessera.Plugins.ReginaMaria.csproj

# Publish framework-dependent (no apphost — run via `dotnet tessera.dll`, portable
# across architectures so the image is trivially multi-arch).
RUN dotnet publish src/Tessera.Cli/Tessera.Cli.csproj \
      -c Release -o /app --no-restore -p:UseAppHost=false

# First-party integrations remain optional plugin assemblies. The host has no
# project reference to them: package them beside a generated immutable hash
# catalog and let the generic runtime discover/validate them at startup.
RUN dotnet build src/Tessera.Plugins.Gmail/Tessera.Plugins.Gmail.csproj -c Release --no-restore \
 && dotnet build src/Tessera.Plugins.GitHub/Tessera.Plugins.GitHub.csproj -c Release --no-restore \
 && dotnet build src/Tessera.Plugins.OneDrive/Tessera.Plugins.OneDrive.csproj -c Release --no-restore \
 && dotnet build src/Tessera.Plugins.ReginaMaria/Tessera.Plugins.ReginaMaria.csproj -c Release --no-restore \
 && mkdir -p /plugin-artifacts/modules \
 && cp src/Tessera.Plugins.Gmail/bin/Release/net10.0/Tessera.Plugins.Gmail.dll /plugin-artifacts/modules/ \
 && cp src/Tessera.Plugins.GitHub/bin/Release/net10.0/Tessera.Plugins.GitHub.dll /plugin-artifacts/modules/ \
 && cp src/Tessera.Plugins.OneDrive/bin/Release/net10.0/Tessera.Plugins.OneDrive.dll /plugin-artifacts/modules/ \
 && cp src/Tessera.Plugins.ReginaMaria/bin/Release/net10.0/Tessera.Plugins.ReginaMaria.dll /plugin-artifacts/modules/ \
 && gmail_sha="$(sha256sum /plugin-artifacts/modules/Tessera.Plugins.Gmail.dll | cut -d ' ' -f 1)" \
 && github_sha="$(sha256sum /plugin-artifacts/modules/Tessera.Plugins.GitHub.dll | cut -d ' ' -f 1)" \
 && onedrive_sha="$(sha256sum /plugin-artifacts/modules/Tessera.Plugins.OneDrive.dll | cut -d ' ' -f 1)" \
 && rm_sha="$(sha256sum /plugin-artifacts/modules/Tessera.Plugins.ReginaMaria.dll | cut -d ' ' -f 1)" \
 && printf '[{"PluginId":"gmail","Version":"1.0.0","AssemblyFileName":"Tessera.Plugins.Gmail.dll","AssemblySha256":"%s","TrustState":"BUILT_IN"},{"PluginId":"github","Version":"1.0.0","AssemblyFileName":"Tessera.Plugins.GitHub.dll","AssemblySha256":"%s","TrustState":"BUILT_IN"},{"PluginId":"onedrive","Version":"1.0.0","AssemblyFileName":"Tessera.Plugins.OneDrive.dll","AssemblySha256":"%s","TrustState":"BUILT_IN"},{"PluginId":"regina-maria","Version":"1.0.0","AssemblyFileName":"Tessera.Plugins.ReginaMaria.dll","AssemblySha256":"%s","TrustState":"BUILT_IN"}]\n' "$gmail_sha" "$github_sha" "$onedrive_sha" "$rm_sha" > /plugin-artifacts/modules.json

# ---- build: admin-portal SPA ----
# Built from source + the committed lockfile so the image is reproducible and the
# local node_modules/dist never enter the build context (.dockerignore).
FROM node:22-alpine AS web
WORKDIR /workspace
# Skip Playwright's browser download (a heavy devDependency postinstall we never
# need to *build* the SPA — Playwright is only for local e2e screenshots).
ENV PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1
COPY packages/tessera-client/package.json packages/tessera-client/package-lock.json packages/tessera-client/tsconfig.json ./packages/tessera-client/
COPY packages/tessera-client/src/ ./packages/tessera-client/src/
COPY web/package.json web/package-lock.json ./web/
RUN npm --prefix web ci
COPY web/ ./web/
# Vite production build → /workspace/web/dist (the same artifact `npm run build` produces).
RUN npm --prefix web run build

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
COPY --from=web /workspace/web/dist ./wwwroot
COPY plugins ./plugins
COPY --from=build /plugin-artifacts/modules ./plugins/modules
COPY --from=build /plugin-artifacts/modules.json ./plugins/modules.json

# Run as a non-root UID. The aspnet image already reserves UID 1000; reference it
# numerically (works with or without a passwd entry, and matches the Kubernetes
# securityContext runAsUser: 1000). /data and /tmp are explicit pod mounts.
USER 1000

EXPOSE 8080

# Config + grants are mounted at /config (e.g. from a ConfigMap).
ENTRYPOINT ["dotnet", "/app/tessera.dll", "serve", "--config", "/config/tessera.json", "--grants", "/config/grants.json"]
