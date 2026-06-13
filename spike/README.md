# Tessera — Python spike (archived)

This directory is the **archived Python v0.0.2 spike**. It proved the model
end-to-end and ran live, read-only, against a real Azure Key Vault (resolving an
existing session bundle and reporting only its status — no upstream call, no
login). It is **superseded by the .NET 10 implementation** in
[`../src`](../src) and is kept only for reference. No backwards compatibility is
intended ([ADR 0001](../docs/adr/0001-language-and-runtime.md)).

## What's here

| Path | What it was |
|---|---|
| `src/tessera/` | the stdlib-only broker core (model, fail-closed PDP, store, resolver, audit, serve) |
| `tests/` | 50 offline pytest tests |
| `pyproject.toml` | the Python package definition |
| `Dockerfile` | the python-slim image (ran the read-only self-test in-cluster) |
| `docker-compose.example.yml` | local run example |
| `tessera.example.toml`, `grants.example.toml`, `examples/` | example config |
| `ci.yml.python-spike` | the original GitHub Actions workflow (test + GHCR image) |

## The design it validated

The decision records and architecture it informed are the **current** design of
record for the .NET build:

- [docs/architecture.md](../docs/architecture.md)
- [docs/adr/](../docs/adr/README.md)
- archived spike write-ups: [README.python-spike.md](../README.python-spike.md) ·
  [architecture.python-spike.md](../docs/architecture.python-spike.md) ·
  [adversarial-p2.python-spike.md](../docs/adversarial-p2.python-spike.md)

> Do not build on this code. Start from [`../src`](../src) (the .NET 10 solution).
