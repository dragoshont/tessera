# Standards alignment

Tessera's design is **not a preference**. It is what the governing specifications
prescribe for this exact problem: a non-human caller acting with a credential it must
not hold.

> **Read the mechanism-by-mechanism mapping into the code:**
> [docs/architecture.md §9](../../architecture.md#9-standards-alignment-the-de-facto-pattern)

---

## The mapping, in short

| Tessera mechanism | Standard | What it requires |
|---|---|---|
| Validate the caller's token for *Tessera's own audience*; inject a *separate* upstream credential; never forward the caller's token onward | **MCP Authorization spec** §2.6.2 / §3.7 | Token-audience validation; **token passthrough is explicitly forbidden**. |
| Caller (WHO) + end-user (FOR WHOM) kept separate; the broker acts *for* the subject while keeping its own identity | **RFC 8693** (OAuth Token Exchange) | *Delegation* semantics; the `act` / `may_act` claims. |
| Authorise the action at the broker — default-deny, planes, step-up on writes, run in the user's context | **OWASP LLM06 (Excessive Agency)**; Saltzer–Schroeder | *Complete mediation*; least privilege; human-in-the-loop for high-impact actions. |
| The caller proves its own identity and holds no long-lived secret | **OWASP Non-Human-Identity Top 10** | No static secrets in the workload; least privilege; attributable. |
| Egress is allow-listed, HTTPS-pinned, no-redirect, with private/metadata IP ranges blocked | **OWASP SSRF Prevention**; **MCP Security Best Practices** | Allow-list destinations; block metadata/link-local; pin DNS; do not follow redirects. |
| The decision point (PDP) is separate from enforcement (egress), and every request is authorised | **NIST SP 800-207 (Zero Trust)** | PDP / PEP separation; per-request authorisation; full auditability. |

---

## Two load-bearing principles

Two named principles carry the whole design:

1. **No token passthrough** — the MCP-spec invariant. Tessera validates the caller's
   token for itself and uses a *separate* upstream credential. It never relays the
   caller's token to the upstream.
2. **Complete mediation** — every action is re-checked at the broker, every time,
   never trusted from the agent.

If you remember only two things about *why* Tessera is shaped the way it is, remember
these two.

---

## Why it matters (the spec's own reasoning)

The MCP specification gives the reason directly: passthrough breaks audience binding,
defeats rate-limit and validation controls, and destroys the audit trail (the
upstream would see a token "from someone else"). It also notes a *future-compatibility*
benefit: starting with audience separation is what lets the security model evolve.
Tessera removes the anti-pattern by construction.

---

## Where to go next

- The full mapping into the code: [architecture §9](../../architecture.md#9-standards-alignment-the-de-facto-pattern).
- The threat model these standards address: [Security model](security-model.md).
- The decisions that encode them: [Decision records](decisions.md).
