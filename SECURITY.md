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
