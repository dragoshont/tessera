# Security model and threats

This page summarises the security invariants and the threat model. The full threat
table and the deeper discussion live in the architecture document:

> **Read the full security model:**
> [docs/architecture.md §6](../../architecture.md#6-security-model-summary-full-threat-model-below)

---

## The five invariants

These carry the whole design. Every other rule supports one of them.

1. **Verified identity, or denied.** A caller proves who it is (a client certificate
   / SPIFFE SVID, or a signed app-only token); an end-user proves who they are (a
   signed OIDC token). The tenant is derived from that proof, never from a header.
2. **Fail closed.** The default policy is *deny*. A surface that is not fully wired
   refuses rather than guesses. When in doubt, say no.
3. **Inject, never hand over.** The caller never receives the secret. The caller's
   token is never passed through to an upstream.
4. **Secretless transit.** The credential store is reached with Managed Identity or
   Workload Identity Federation — there is no store password to leak.
5. **Per-tenant isolation.** Each tenant's secrets are separated; medical accounts get
   a dedicated instance with its own store.

---

## The threat model (summary)

| Threat | How Tessera mitigates it |
|---|---|
| **Identity spoof / confused deputy** at the boundary | Cryptographically verified caller + end-user; identity never read from a plain header. |
| **Blast radius** (one broker, many secrets) | Per-tenant isolation; a dedicated instance + isolated store for medical. |
| **Token passthrough / long-lived secret leak** | Injection, not relaying; no client secret for store access (MI/WIF). |
| **Prompt injection / excessive agency** | Least-privilege per (caller, user, target, action); step-up for write/pay/book. |
| **Replay / session fixation** | Short-lived, single-use assertions; rotate harvested sessions. |
| **SSRF on egress** | Host allow-list; connect-time IP pinning; metadata/link-local ranges blocked; no redirects. |
| **Non-repudiation gap** | Secret-free, append-only audit of every decision. |

---

## Secure-by-default switches

Tessera is built to be safe the moment it is deployed:

- **Egress is off by default.** Deploying the broker opens no path to any upstream.
  An operator turns egress on deliberately, and only with a non-empty SSRF allow-list.
- **The caller plane fails closed.** Until a caller authenticator is configured,
  `/v1/broker` refuses every call.
- **Validation rejects fail-open config.** `policy.default = allow` is refused; a
  `dev` (unverified) identity mode is allowed only on loopback.

These are enforced by the configuration validator — see
[Configuration reference](../reference/configuration.md).

---

## The two SSRF layers (defense in depth)

Egress is the most safety-sensitive control point. Tessera guards it in two layers:

1. **Host + scheme** (`SsrfGuard`): only allow-listed hosts; HTTPS by default; plain
   HTTP only to allow-listed internal hosts when explicitly opted in.
2. **Resolved IP** (`AddressGuard`, at connect time): the host is resolved once and
   the connection is **pinned** to that IP, with link-local / cloud-metadata
   (`169.254.169.254`) / loopback / multicast blocked. This closes the DNS-rebind
   (time-of-check vs time-of-use) gap that a host-name check alone cannot.

The transport also refuses redirects, uses no proxy, and sends no ambient cookies.

---

## Where to go next

- The full threat table and discussion: [architecture §6](../../architecture.md#6-security-model-summary-full-threat-model-below).
- Why the shape is correct: [Standards alignment](standards-alignment.md).
- Turn egress on the safe way: [Enable egress safely](../how-to/enable-egress-safely.md).
