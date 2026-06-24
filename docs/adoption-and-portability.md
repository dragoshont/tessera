# Adoption & portability

> Audience: someone deciding **whether** to run Tessera, and **how** to run it
> without Azure. Honest about where Tessera fits and where something else is the
> right tool. Grounds the SDD-04 phase of the [liveness/portability roadmap](sdd/README.md)
> and answers the [gap analysis](research/liveness-ux-oss-gap-analysis.md) §III.

## 1. Adopt vs build — is Tessera even the right tool?

Tessera is **not** a general secrets manager. Use the right tool for the job:

| If you need… | Use | Not |
|---|---|---|
| To inject a **service** secret (DB password, API key) into your own app/pods | **HashiCorp Vault Agent / OpenBao Agent**, External Secrets Operator, or your cloud's secret store | Tessera |
| A human user's **OAuth/OIDC** login to a SaaS with a real API | The provider's **OAuth app** + a token store | Tessera |
| To let an **agent / MCP** act *as a specific person* against a service that has **no usable API** — or to broker **per-person, consent-gated** access an agent must never hold the credential for | **Tessera** | a secrets manager |

Tessera's niche is narrow and deliberate: **per-person delegation for agents/MCP against the un-API'd web**, where the credential must stay custodial (the agent gets a brokered *call*, never the secret). If Vault Agent already covers your case, adopt that — it is more mature, more portable, and not what Tessera competes with. Say this out loud so nobody adopts Tessera for the wrong reason.

## 2. The Tessera ↔ sessionkeeper boundary

Two components, two jobs — keep them straight:

- **Tessera (this repo, .NET):** the **broker**. It authenticates a caller, authorizes `(caller, on-behalf-of, target, action)`, injects the credential into the upstream call, and returns the result. It owns policy, audit, the portal, and — since ADR 0025 — **liveness truth** (the use-based verdict). It never hands the secret to the caller.
- **sessionkeeper (separate, Python):** the **harvester/rotator** for sessions that have no clean refresh (browser cookies, anisette, captcha-gated logins). It keeps a session *warm* and re-seeds it.

The seam: sessionkeeper **produces** a live session into the credential store; Tessera **consumes** it. Today they are wired as **sidecars** (Tessera's egress off for those targets); ADR 0002/0015 describe a future *dispatch* protocol. Until that lands, treat them as: *sessionkeeper writes the bundle, Tessera reads it, and ADR 0026's single-writer lease guarantees exactly one rotator.* If you only need providers with a real OAuth refresh, you do **not** need sessionkeeper at all.

## 3. The non-Azure golden path

Tessera's defaults lean Azure (Entra OIDC + Key Vault), but **nothing in the core requires Azure** — the store and the identity plane are both pluggable (ADR 0003). A fully self-hosted stack:

| Plane | Azure default | Self-hosted equivalent |
|---|---|---|
| Caller identity (`identity.mode`) | Entra OIDC | **Authentik** or **Dex** (any standards OIDC issuer) — set `identity.oidc.issuer` |
| Credential store (`ICredentialStore`) | Azure Key Vault | **OpenBao** (or HashiCorp Vault) — a small `ICredentialStore`/`ICredentialWriter` adapter |
| Single-rotator lease (ADR 0026) | — | **Kubernetes `Lease`** (etcd) or the in-process default for one replica |

Reference shape (compose; illustrative, not a turnkey file):

```yaml
# Non-Azure Tessera — illustrative topology, adapt to your network/secrets posture.
services:
  authentik:        # OIDC issuer for caller identity (or dex)
    image: ghcr.io/goauthentik/server
  openbao:          # the credential store (Vault-compatible)
    image: openbao/openbao
  tessera:
    image: ghcr.io/dragoshont/tessera
    environment:
      Identity__Mode: oidc
      Identity__Oidc__Issuer: https://authentik.example/application/o/tessera/
      # Store: point an OpenBao-backed ICredentialStore adapter at openbao:8200
    # egress.enabled stays false until you opt a target in (fail-closed).
```

What is **shipped** vs **plan-only**: the *seams* (pluggable store + OIDC issuer) exist today; the **OpenBao `ICredentialStore` adapter** and a packaged compose stack are **plan-only** follow-ons (new infra — a human stands them up). The point of this section is that the path is real and dim only because it is undocumented, not because the core is Azure-bound.

## 4. Recipes — the contribution path

A **recipe** describes how to talk to one provider (its base URL, the tools/actions it exposes, injection kind, optional refresh spec). Recipes are the main thing the community can contribute portably, because they carry **no secrets** — only the public shape of a provider.

To contribute a recipe:
1. Copy an existing recipe of the same **injection kind** (cookies / bearer / header).
2. Fill the `target`, `upstreamBaseUrl`, and the minimal `tools` (least-privilege: expose only the actions an agent needs; mark writes `stepUp`).
3. Add a `refreshSpec` **only** if the provider has a real refresh endpoint (otherwise the session is harvester-owned — see §2).
4. Keep outputs classed (`metadata` vs full) so a list/search can't spill more than ids + snippets.
5. Submit it with an example policy binding, **never** a real credential.

A genuinely reusable **reference MCP** is the standard-CalDAV `apple-mcp` (ADR 0022): a recipe + MCP that any CalDAV/CardDAV provider can reuse, not a one-off.

## 5. ToS & at-your-own-risk posture

Be honest about the edges:

- **libgsa (Apple GrandSlam):** SRP-6a + anisette, **reverse-engineered**, and in **ToS-grey** territory. It is fenced as **advanced / at-your-own-risk**: it is not part of the supported golden path, and running it is the operator's call against Apple's terms. Do not present it as a blessed integration.
- **Harvested web sessions generally:** brokering a human's logged-in session can conflict with a provider's terms. Tessera's design keeps the human the custodian and the agent credential-free, but **the operator owns the ToS decision** for each provider they add. The default-off egress posture exists precisely so adding a target is always a conscious, reviewable act.

## Where this leaves the roadmap

| Recommendation (gap analysis §V) | Status |
|---|---|
| Adopt-vs-build + niche stated loudly | **This doc §1** |
| Tessera↔sessionkeeper boundary clarified | **This doc §2** |
| Non-Azure golden path | **This doc §3** (seams shipped; OpenBao adapter + compose = plan-only) |
| Recipe-contribution path + reusable reference MCP | **This doc §4** |
| ToS / at-your-own-risk fencing | **This doc §5** |
