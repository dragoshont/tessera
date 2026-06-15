# Architecture

This page is a short orientation. The full architecture document — with every
diagram (system, request lifecycle, deployment shapes, threat model) — is the design
of record:

> **Read the full architecture:**
> [docs/architecture.md](../../architecture.md)

---

## The mental model in one paragraph

A caller connects to **one** Tessera endpoint, carrying its own identity but no
secret. Tessera verifies *who* is calling and *for whom*, checks policy, fetches the
right credential from a pluggable store just-in-time, performs the upstream call on
the caller's behalf, and returns only the result. Credentials for services with no
API are kept warm by a separate worker tier that the broker reaches only through the
store. The broker is small and auditable; the messy automation is isolated behind it.

## The components

| Project | Responsibility |
|---|---|
| `Tessera.Core` | Identity, policy (the PDP), tenancy, recipe model. No input/output. |
| `Tessera.Broker` | The host: web server, egress, audit, the caller plane, the portal. |
| `Tessera.Identity` | OIDC/JWT validation; the token → identity mapping. |
| `Tessera.Providers` | The provider egress (inject, call, result classes). |
| `Tessera.Mcp` | The native MCP surface (`tessera_*` tools). |
| `Tessera.Stores.AzureKeyVault` | The default credential store (Managed Identity / WIF). |
| `Tessera.Cli` | `tessera validate` / `serve`. |

## The security boundary

The path **ingress → verify → policy → resolve → egress** is the security boundary,
and it is kept small. The harvest workers (for un-API'd providers) are a *separate*
trust zone; they never share the broker's process and meet it only at the store.

## Deployment shapes

Tessera can run as **one container** (broker plus an in-process worker) for a small
setup, or with **split workers** for scale and stronger isolation. The caller always
sees one endpoint either way. A **dedicated instance** with its own store is the
default for medical accounts.

---

## Where to go next

- The full document with all diagrams: [docs/architecture.md](../../architecture.md).
- Where Tessera sits next to your other tools: [Positioning](positioning.md).
- How one call flows through these components: [How a call works](how-a-call-works.md).
