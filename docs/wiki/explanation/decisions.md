# Architecture decision records (ADRs)

A **decision record** captures one load-bearing choice and *why* it was made, so the
reasoning survives past the conversation that produced it. Each record is immutable
once accepted; a new record supersedes an old one rather than editing it.

> The full, authoritative index: [docs/adr/README.md](../../adr/README.md).

---

## The decisions, in plain language

| ADR | The decision, in one line |
|---|---|
| [0001](../../adr/0001-language-and-runtime.md) | Build it in **.NET 10**. |
| [0002](../../adr/0002-broker-worker-topology.md) | A **broker** plus **relocatable harvest workers** (one container, or split — same client contract). |
| [0003](../../adr/0003-credential-store-pluggable.md) | The credential store is **pluggable**; Key Vault is the default, reached without a stored secret. |
| [0004](../../adr/0004-tenancy-and-isolation.md) | **Multitenant by default**, with a **dedicated instance for medical**. |
| [0005](../../adr/0005-identity-first-fail-closed.md) | **Verified identity first, fail-closed**; the tenant comes from proven identity. |
| [0006](../../adr/0006-harvest-drivers.md) | **Pluggable harvest drivers** (browser now; Android/desktop later). |
| [0007](../../adr/0007-worker-transport.md) | Broker ⇄ worker transport is **gRPC + mTLS**. |
| [0008](../../adr/0008-policy-and-identity-administration.md) | Policy is **file-first + GitOps**; the UI is a thin layer. |
| [0009](../../adr/0009-end-user-identity-propagation.md) | **Per-call end-user delegation** is required; the chat forwards a signed token. |
| [0010](../../adr/0010-chat-client.md) | The chat client is a **fork of LibreChat**. |
| [0011](../../adr/0011-identity-provider-sso.md) | The identity provider is **Microsoft Entra** (or Authentik federating it). |
| [0012](../../adr/0012-chat-login-microsoft-only.md) | Chat login is **Microsoft-only** (one IdP can forward a broker-acceptable token). |
| [0013](../../adr/0013-per-user-access-tiers.md) | **Default-deny for sensitive tools**, per user, in two layers. |
| [0014](../../adr/0014-http-injectable-provider-egress.md) | **HTTP-injectable provider egress** with a single session-owner. |
| [0015](../../adr/0015-mcp-egress-through-tessera.md) | **Domain MCPs egress through Tessera** (they keep their tools but hold no secret). |
| [0016](../../adr/0016-admin-portal.md) | A **headless-first admin portal** (convenience, never the source of truth). |
| [0017](../../adr/0017-awareness-dashboard.md) | A **read-only awareness dashboard** ("who may act as me?"). |
| [0018](../../adr/0018-access-gateway-and-action-broker.md) | The **access gateway is outside Tessera**; Tessera is the **action broker**. |
| [0019](../../adr/0019-app-integrations-and-user-delegated-actions.md) | App-to-app stays direct; **user-delegated actions go through Tessera** (read/use/manage planes). |
| [0020](../../adr/0020-credential-ownership.md) | **Credential ownership**: user vs service vs dependent. |
| [0021](../../adr/0021-caller-authentication-plane.md) | A **caller authentication plane** for non-human callers (the `/v1/broker` door). |

---

## How to read an ADR

Each record follows a light structure:

1. **Context** — the situation and the forces at play.
2. **Decision** — what was chosen.
3. **Consequences** — what follows, good and bad.
4. **Alternatives** — what was rejected, and why.

If you are new, the most useful records to read first are **0005**
(identity-first, fail-closed), **0014** (provider egress), **0018** (gateway vs
broker), and **0021** (the caller plane).

---

## Where to go next

- The authoritative index: [docs/adr/README.md](../../adr/README.md).
- The standards these decisions encode: [Standards alignment](standards-alignment.md).
