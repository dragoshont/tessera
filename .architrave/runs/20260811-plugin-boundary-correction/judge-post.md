# Judge Gate 2

## Verdict

Initial independent verdicts: Architecture **REVISE**; Security **REVISE**; Product/Delivery **FAIL**.

Repository blockers were remediated after the first review.

- Architecture: Copilot-family **PASS**; Claude-family **PASS** for `ARCH-001..010` and `MCP-001..011`.
- Security: Copilot-family **PASS**; Claude-family **PASS** for repository MCP/plugin security.
- Product UX: Claude-family **PASS** after generic Chat/Job grants, arbitrary-plugin browser coverage, Account recovery and plugin-removal confirmation.
- Overall delivery: **PARTIAL**. Product delivery remains FAIL until approved deployment and real E2E.

## Findings

First-review findings and disposition:

1. Dead provider-shaped schemas in Broker: removed; architecture source guard expanded to provider-shaped identifiers.
2. Removed plugins remained listable/re-enableable: fixed with `removed=0` list/update policy and successful-removal/restart/history regression.
3. Weak MCP drift and missing server identity: replaced with exact reviewed server name/version and typed required input/output schema validation.
4. MCP runtime identity not durable: schema v15 persists server ID/name/version and external tool; RM Job regression verifies the record.
5. Provider credentials resolved while constructing write capabilities: capability creation/discovery is deferred to invocation after exact Action authorization; timing regression proves no construction-time discovery or credential resolution.
6. Public MCP could reach private networks and GitHub accepted endpoint override: public endpoints now require HTTPS/public-only addresses; private reachability is explicit for RM connectors; GitHub uses the fixed official endpoint.
7. Test-only `ProjectTools` path diverged from production: removed; production-path tests cover extra/duplicate tools.
8. Gate and phase artifacts were empty/stale: deterministic commands, source fingerprints and phase results are recorded in this run.
9. Chat/Jobs UX hard-coded provider selection: replaced by ConnectedAccount `capabilityIds` and currently available read-capability metadata; a fictional `calendar-mcp` integration passes desktop/phone Playwright.
10. Disabled Account and plugin-removal recovery gaps: disabled Accounts expose `Enable and test`; RM `AUTH_REQUIRED` remains account-holder gated; removal now has an explicit irreversible/history/in-use confirmation.

Remaining product/delivery findings are intentional blockers, not repository success claims: no dynamic third-party installation UX, corrected homelab image not deployed, Gmail/RM auth incomplete, and no real side-effect/restart dogfood.
