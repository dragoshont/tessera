# Tessera — secretless, identity-aware credential broker (.NET 10).
#
# Multi-stage: build with the .NET SDK, run on the ASP.NET runtime. Runs as a
# non-root user; writes nothing to disk (audit -> stdout), so it is compatible with
# a read-only root filesystem + an emptyDir at /tmp. Config + grants are mounted
# (e.g. from a ConfigMap) at /config.

# ---- build ----
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

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

ENV DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_EnableDiagnostics=0 \
    TESSERA_SERVER_HOST=0.0.0.0 \
    TESSERA_SERVER_PORT=8080

WORKDIR /app
COPY --from=build /app ./

RUN useradd --uid 1000 --create-home --shell /usr/sbin/nologin tessera
USER 1000

EXPOSE 8080

# Config + grants are mounted at /config (e.g. from a ConfigMap).
ENTRYPOINT ["dotnet", "/app/tessera.dll", "serve", "--config", "/config/tessera.json", "--grants", "/config/grants.json"]
