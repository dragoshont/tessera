# ADR 0019 - Keep app-to-app integrations direct; broker user-delegated actions through Tessera

- **Status:** Accepted (2026-06-14; implementation phased)
- **Deciders:** maintainer
- **Relates to:** [ADR 0014](0014-http-injectable-provider-egress.md)
  (provider egress), [ADR 0015](0015-mcp-egress-through-tessera.md) (domain MCP
  egress), [ADR 0018](0018-access-gateway-and-action-broker.md) (access gateway
  outside Tessera; Tessera as action broker).
- **Detailed design:** [service-access adversarial design](../specs/service-access-adversarial-design.md).

## Context

Media applications already have native API-to-API relationships:

- Prowlarr syncs indexers and app configuration to Sonarr and Radarr.
- Sonarr sends TV downloads to qBittorrent.
- Radarr sends movie downloads to qBittorrent.
- Seerr can hand approved requests to Sonarr or Radarr.

Those relationships are service integrations. The service owns the target API key
or configured credential, knows the target's operational semantics, and encodes
the expected retries, categories, labels, queue state, and failure handling. There
is normally no per-episode human authorization question when Sonarr sends a
download to qBittorrent.

Tessera's broker model targets a different class of request: a human, AI tool,
MCP, script, or Tessera UI asks to perform a privileged action against a provider
using a hidden upstream credential. That request needs verified user identity,
action-level policy, optional just-in-time elevation, safe execution, and
semantic audit.

The same upstream API may appear in both worlds. qBittorrent can receive routine
requests from Sonarr directly, while Tessera can later expose a controlled action
such as "pause this torrent" to a user or assistant. The difference is the actor
and authorization question, not merely the protocol.

## Decision

Keep routine app-to-app runtime integrations direct. Route user-delegated
privileged actions through Tessera.

Direct service integrations remain:

```text
Prowlarr -> Sonarr/Radarr
Seerr -> Sonarr/Radarr
Sonarr/Radarr -> qBittorrent
```

Tessera-mediated action paths are:

```text
User / assistant / MCP / script / Tessera UI -> Tessera -> provider API/worker
```

Use this rule:

| Question | Path |
|---|---|
| Does an app need another app to function normally? | Keep direct. |
| Is a user/tool asking to perform a provider action with a privileged credential? | Use Tessera. |
| Is the request a browser visit to an app UI? | Use the access gateway from [ADR 0018](0018-access-gateway-and-action-broker.md). |

Examples:

| Scenario | Path | Why |
|---|---|---|
| Sonarr sends episode download to qBittorrent. | Sonarr -> qBittorrent. | Routine service integration. |
| Radarr sends movie download to qBittorrent. | Radarr -> qBittorrent. | Routine service integration. |
| User asks assistant to pause large downloads. | User/tool -> Tessera -> qBittorrent API. | User-delegated privileged action. |
| User opens Seerr UI. | Browser -> Traefik/access gateway -> Seerr. | Browser access policy. |
| User asks assistant to approve a Seerr request. | User/tool -> Tessera -> Seerr API. | Action-level authorization and audit. |
| RM MCP uses Regina Maria session. | Domain MCP -> Tessera -> RM provider. | Domain tool egress through Tessera; MCP holds no upstream secret. |

Domain MCP egress through Tessera ([ADR 0015](0015-mcp-egress-through-tessera.md))
is not an exception to this rule. A domain MCP is a user/tool-facing domain
adapter. It should not own long-lived provider credentials. It delegates
credentialed egress to Tessera while keeping domain-specific tool ergonomics and
result shaping.

## Action planes (read · use · manage)

The actor split above answers *whether* a request is brokered. Within the
brokered set, classify every capability on **two independent axes**.

**Axis 1 — plane** (what the action touches):

| Plane | Meaning | Examples |
|---|---|---|
| **read** | Observe state; change nothing. | List torrents, search a library, server status, get an entity state. |
| **use** / operate | Exercise the service in normal operation (the data/operational plane). | Turn on a light, play media, request a movie, pause a download, approve a request, trigger a search. |
| **manage** / configure | Reshape the service itself (the control plane). | Remove a device, create an automation, edit quality profiles, add/remove an indexer, change settings, regenerate a key, add a user. |

**Axis 2 — risk** (is a human required in the loop): a **step-up** flag on any
capability, in any plane, forces an explicit human confirmation that echoes the
request before it proceeds.

The axes are **orthogonal**. Management is *almost always* step-up, but a
use-plane action can also be step-up (unlock a door), and even a read can be
sensitive (another person's calendar body). So plane and risk are recorded
separately, not collapsed into one "write = dangerous" bit.

**Rules:**

1. **Verbs are namespaced by plane:** `read:<resource>`, `use:<resource>`,
   `manage:<resource>`.
2. **The manage plane is default-deny even when `use` is granted.** A grant of
   `use:*` never implies `manage:*`; management must be granted explicitly. This
   gives a coarse, legible boundary: *"this assistant may operate my home, but
   never reconfigure it."*
3. **Management defaults to step-up** (`manageRequiresStepUp: true`), loosened
   only per-deployment and per-capability, never globally by default.
4. A capability is step-up-gated when **either** the recipe tool declares
   `stepUp` **or** the grant lists it in `stepUpActions`. The plane drives the
   *default*; the recipe flag and the grant decide *enforcement*.
5. The plane is surfaced in the **consent receipt** and the **awareness
   dashboard** ([ADR 0017](0017-awareness-dashboard.md)) so a person can see
   "may use, not manage" at a glance.

This composes with [ADR 0013](0013-per-user-access-tiers.md) (per-user tiers,
default-deny for sensitive) and reuses the [ADR 0014](0014-http-injectable-provider-egress.md)
step-up decision already in `ProviderEgress`. It is a **naming + default
convention** over the existing `Grant.Actions` ⊕ `Grant.StepUpActions` mechanism,
plus a small `plane` field on the recipe tool — not a new engine. The detailed
per-service mapping for the deployed media and home-automation stack is in the
[service-access spec](../specs/service-access-adversarial-design.md#action-planes-read--use--manage).

A fourth, orthogonal axis — **who owns the credential** (user / service /
dependent) — drives seeding, reveal, revocation, and consent, and is decided in
[ADR 0020](0020-credential-ownership.md).

**Multi-semantic endpoints** (one upstream call, many meanings — e.g. Home
Assistant `POST /api/services/{domain}/{service}`, or qBittorrent's command API)
do not fit per-path classification. Two resolutions: (a) **curate** distinct,
pre-classified recipe tools (`ha_light_on` = `use:light`; `ha_lock_unlock` =
`use:lock` + step-up) rather than exposing a generic "call any service" verb; or
(b) **argument-aware classification** (inspect the body to pick the plane/step-up)
— a deliberate future add, not in the base engine today.

## Consequences

**Positive**

- Preserves reliability of existing media automation flows.
- Avoids placing Tessera in hot paths where it adds latency and new failure
  modes without improving user-level policy.
- Keeps Tessera focused on high-value action authorization: hidden credential,
  policy, JIT/elevation, and audit.
- Makes qBittorrent, Sonarr, Radarr, Seerr, Prowlarr, RM, Google, Apple, and Plex
  all fit one provider-connection model when the actor is a user/tool.
- Avoids confusing app-owned credentials with user-delegated credentials.

**Negative / cost**

- Credentials may exist in two legitimate places for different purposes: an app's
  own integration config and Tessera's brokered action credential store.
- Operators must understand that direct app-to-app logs and Tessera action audit
  are different audit streams.
- If an upstream action can be performed both by an app and by Tessera, policy and
  naming must make the actor explicit.

## Rejected alternatives

- **Route all Sonarr/Radarr/qBittorrent/Seerr traffic through Tessera.** Rejected.
  It turns Tessera into a service mesh/API bus and weakens reliability.
- **Let AI tools call app APIs directly with app API keys.** Rejected. It exposes
  standing credentials to tools and loses action-level policy, JIT, and audit.
- **Remove app-native integrations and use Tessera as the only automation engine.**
  Rejected. Sonarr/Radarr/Prowlarr/Seerr already encode domain-specific runtime
  behavior that Tessera should not reimplement.
- **Treat app-owned API keys and Tessera-owned provider credentials as the same
  thing.** Rejected. App-owned keys are service integration material; Tessera-held
  credentials are delegated-action material.
