# Adversarial Integration Security Review

**Status:** Repository security controls pass; deployed and external-account attacks remain pending.

Required attacks include malicious tool descriptions/results, unknown write discovery, schema drift, hash mismatch, downgrade, untrusted source, account substitution, approval replay/payload mutation, disabled-during-execution, remote MCP SSRF/redirects, local process environment leakage, Gmail token leakage, RM cross-account session use, timeout/disconnect unknown outcomes and MCP restart.

The runtime denies arbitrary endpoint shapes, requires HTTPS/public-only addresses for public MCPs, gives private-network reachability only to explicit connector endpoints, disables redirects/proxy/cookies, applies connect-time SSRF and DNS-rebind defense, bounds schemas/results, supports cancellation/timeouts, treats MCP output as data and marks mutation transport failure unknown only after dispatch. Hash mismatch, malformed/traversal/symlink modules, unknown trust, disabled/untrusted modules, duplicate identity/tools, exact server-version drift, typed input/output drift, extra unclassified tools, account substitution, Action replay/payload binding, deferred write credential resolution and plugin disable have executable coverage.

Hostile MCP descriptions are discarded from the neutral contract; hostile result instructions remain inert structured provider data. Stdio is not implemented, so inherited-environment/shell-argument attacks are not applicable yet. Deployed cross-account RM, Gmail token, MCP restart and disable-during-real-provider-call dogfood still require the approved runtime and separately authorized accounts.
