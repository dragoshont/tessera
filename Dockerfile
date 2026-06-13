# Tessera — secretless, identity-aware credential broker.
#
# Runtime is Python-stdlib-only (no pip deps), so the image is just python-slim
# plus the source. Multi-arch (python:slim publishes amd64 + arm64). The process
# runs as a non-root user on a read-only root filesystem; it writes nothing to
# disk (audit goes to stdout) and only needs an emptyDir at /tmp.
FROM python:3.12-slim

ENV PYTHONUNBUFFERED=1 \
    PYTHONDONTWRITEBYTECODE=1 \
    TESSERA_SERVER_HOST=0.0.0.0 \
    TESSERA_SERVER_PORT=8080

WORKDIR /app
COPY pyproject.toml README.md LICENSE ./
COPY src ./src

# Install the package itself (pulls no third-party deps).
RUN pip install --no-cache-dir . \
    && useradd --uid 1000 --create-home --shell /usr/sbin/nologin tessera

USER tessera
EXPOSE 8080

# Healthy = the HTTP server answers /healthz.
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD python -c "import urllib.request,os,sys; sys.exit(0 if urllib.request.urlopen('http://127.0.0.1:'+os.environ.get('TESSERA_SERVER_PORT','8080')+'/healthz', timeout=3).status==200 else 1)"

# Config + grants are mounted (e.g. from a ConfigMap) at /config.
ENTRYPOINT ["tessera", "serve", "--config", "/config/tessera.toml", "--grants", "/config/grants.toml"]
