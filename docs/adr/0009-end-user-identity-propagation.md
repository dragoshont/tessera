# ADR 0009 — End-user identity propagation & the chat client

- **Status:** Accepted (2026-06-13)
- **Deciders:** maintainer (Dragoș)
- **Relates to:** [ADR 0005](0005-identity-first-fail-closed.md) (verified identity),
  [ADR 0004](0004-tenancy-and-isolation.md) (tenancy)

## Context

The hardest real-world scenario: a chat app (e.g. **LibreChat**) deploys **one**
MCP server shared by **all** users. The MCP's workload badge says only *"I am
`calendar-mcp`"* — the **same for everyone**. It cannot, by itself, tell Alice
from Bob. So the **end-user identity must change per interaction**, and reach
Tessera as proof, not as a claim.

If the shared MCP forwarded a plaintext *"this is Alice"*, a prompt-injected tool
inside that shared MCP could impersonate anyone — the confused-deputy hole
([ADR 0005](0005-identity-first-fail-closed.md)).

**Known constraint:** LibreChat v0.8.0 does **not** natively forward a *signed*
per-user identity to MCP servers. (Evidenced by the per-user MCP gating that had to
be added as a fork in prior work; the user id available to the MCP layer was an
internal identifier, not a verifiable end-user assertion.) So "shared MCP, true
per-user delegation" is not available out of the box.

## Decision

**Per-call end-user delegation is a hard requirement.** When a call is made on
behalf of a human, Tessera requires the end-user's **own, cryptographically signed
token** (OIDC / RFC 8693 `subject_token`), forwarded per call and **independently
verified** by Tessera (sig, `aud`, `exp`, `jti`, issuer). Two supported patterns:

1. **Shared MCP + delegated identity (target).** One MCP for everyone; each tool
   call carries the *current user's* signed token, forwarded chat → MCP → Tessera.
   Tessera verifies **both** badges — workload (the MCP) and end-user (the human) —
   and authorizes the triple `(mcp, user, target, action)`. This is the elegant,
   scales-to-many-users answer.

2. **Instance-per-user (fallback / high isolation).** A separate MCP deployment per
   person, each with its **own** workload badge that maps 1:1 to one human. Here the
   workload badge *already means* the person, so no per-call token propagation is
   needed. This is the [ADR 0004](0004-tenancy-and-isolation.md) dedicated-instance
   tier — and the right default for the **medical** account.

```mermaid
sequenceDiagram
    participant Alice
    participant Chat
    participant MCP as shared MCP (one for all)
    participant T as Tessera
    Alice->>Chat: sign in (OIDC) → Alice holds a signed token
    Alice->>Chat: "add this to my calendar"
    Chat->>MCP: invoke tool + FORWARD Alice's signed token
    MCP->>T: mTLS(WHO=mcp badge) + (FOR WHOM=Alice's signed token)
    T->>T: verify both; authorize (mcp, Alice, calendar, write:event)
    T-->>MCP: result only (Alice's calendar, never Bob's)
```

### The chat client

Because pattern 1 needs the chat to **propagate each user's signed identity** to
the MCP, the project delivers a chat surface that does. **Decision recorded in
[ADR 0010](0010-chat-client.md): fork LibreChat.** LibreChat already provides the
primitive — `OPENID_REUSE_TOKENS` makes the user's token provider-issued, and
YAML-defined MCP servers forward it via `{{LIBRECHAT_OPENID_*}}` headers (plus
Azure on-behalf-of). The maintainer's existing `dragoshont/LibreChat` fork already
carries WebRTC voice and `MCP_USER_GATE`, so we harden that rather than build new.

Per the project's single-iteration scope (see [roadmap](../roadmap.md)), a chat
surface with correct per-user delegation **and WebRTC voice** is **in scope for the
first iteration**, not deferred.

## Consequences

- **Positive:** one shared MCP can safely serve many users, each strictly isolated;
  voice/text/CLI all become the same delegated-identity consumer.
- **Negative:** requires owning (forking or building) the chat's identity
  propagation — a real workstream, not free.
- **Mitigation:** the instance-per-user pattern (already proven in the maintainer's
  homelab) is the immediate fallback and the medical-tier default, so isolation is
  never blocked on the chat work landing.

## Rejected alternatives

- **Trust a plaintext `on_behalf_of` from the shared MCP** — rejected: confused
  deputy; the end-user identity must be a verified signed assertion, not a claim.
- **One global service identity for all users** — rejected: cannot isolate users;
  exactly the shared-key problem Tessera exists to remove.
