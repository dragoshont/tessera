# Security Policy

Tessera brokers access to real accounts on behalf of callers. Security is the
product, so please treat this file as a contract, not boilerplate.

## Reporting a vulnerability

Please **do not** open a public issue for security problems. Email
`alice.hont@gmail.com` with details and a proof-of-concept if you have one.
You'll get an acknowledgement, and a fix or mitigation will be prioritized over
features.

## Threat model (summary)

The full threat model lives in [docs/architecture.md](docs/architecture.md#threat-model).
The load-bearing invariants:

1. **No caller is trusted by assertion.** A caller's identity (the *who*) and any
   end-user it acts for (the *for whom*) MUST be **cryptographically verifiable**
   (mTLS / SPIFFE X.509-SVID for the workload, signed OIDC/JWT for the end-user).
   A plaintext `X-User: alice` header is never sufficient — trusting one is the
   classic *confused-deputy* vulnerability.
2. **Fail closed.** If identity can't be verified or no policy explicitly allows
   the request, the answer is **deny**. The default policy is `deny`.
3. **Inject, never hand over.** Tessera authenticates to the upstream *on behalf
   of* the caller (credential **injection**). The caller never receives the
   secret. "Applications cannot leak what they don't have."
4. **No token passthrough.** Tessera never forwards a token it received from a
   caller to an upstream API (per the MCP authorization spec). Upstream
   credentials are separate and minted/held by Tessera.
5. **Least privilege + audit.** Every grant is scoped to the smallest set of
   actions; every decision is auditable to `(workload, end-user, target, action,
   decision)`.

## Secrets

This repository contains **no secrets** and no live credentials. Example configs
(`*.example.toml`) use placeholders. Real `tessera.toml` / `grants.toml` and
`*.log` files are git-ignored. Never commit a real credential or session bundle.

A blocking `scripts/check-pii.sh` gate runs both as a pre-commit hook
(`.pre-commit-config.yaml`) and as the required `hygiene` job in CI. It fails
closed (non-zero exit) if a real identity or secret pattern is ever introduced,
so the "no secrets" property is enforced mechanically, not just by convention.

## Known limitations & residual risks

Tessera is deployable today, but a few invariants above are stronger than the
current rollout. These gaps are tracked here so operators can make an informed
risk decision instead of discovering them by surprise. Each entry names the
residual risk, its blast radius, and the path to close it.

### R1 — Two credential planes during the proxy rollout (interim)

Invariant 3 ("inject, never hand over") is fully enforced for upstreams brokered
*through* Tessera. The target architecture in
[ADR 0015](docs/adr/0015-mcp-egress-through-tessera.md) — where a domain MCP
server holds **no** upstream secret and reaches its API only by egressing
through Tessera — is **Proposed, not yet built**. In the interim, a domain MCP
may still hold its own upstream session/credential (e.g. in a secret store) and
call its API directly, alongside Tessera holding the broker credentials.

- **Residual risk:** compromising such a domain MCP yields *that MCP's* single
  upstream credential. It does **not** expose any other principal's credentials
  and does **not** yield Tessera's broker identity — blast radius is one upstream
  account.
- **Close it by:** completing the ADR 0015 cutover so the only component holding
  upstream secrets is Tessera.

### R2 — End-user assertion audience must be pinned (Flow-B)

When a caller presents an end-user assertion (the *for whom*), Tessera MUST
validate that the assertion's `aud` (audience) names **Tessera's own resource**,
not merely that the token is signed by a trusted IdP. Accepting a correctly
signed token minted for a *different* resource would let that token be replayed
against Tessera — a token-substitution / confused-deputy variant.

- **Residual risk:** the expected audience is a configuration value. An unset or
  wildcard expected-audience downgrades a real audience check into a weaker
  "any token from this issuer is fine" check.
- **Close it by:** setting the expected audience explicitly in the deployment's
  identity config and treating an unset value as **fail-closed**. Verify it
  before exposing Tessera to callers that can obtain tokens for more than one
  resource.

### R3 — Egress containment is a deployment responsibility

Tessera's `SsrfGuard` validates every upstream host **at the application layer**
against the recipe's allow-list before any request leaves the process. That is
the primary egress control. It is **not** a substitute for network-layer
containment: a code-level guard bypass, a dependency RCE, or a misconfigured
recipe could still attempt arbitrary outbound connections if the surrounding
network permits them.

- **Residual risk:** in a deployment whose namespace permits unrestricted egress
  (e.g. a permissive default `NetworkPolicy`), the app-layer guard is the *only*
  thing between a compromised process and the open internet.
- **Close it by:** running Tessera (and any domain MCP it fronts) in a namespace
  with a **default-deny egress** policy plus an explicit allow-list of upstream
  hosts and DNS, so the network enforces what `SsrfGuard` asserts. Treat the two
  as defence-in-depth, not either/or.

