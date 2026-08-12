# R2 Security Model

## Trust Boundaries

Browser, model output, plugin manifests, capability results, provider HTTP, and scheduler wakeups are untrusted. The Broker derives canonical owner identity and authorizes every object. Credential values remain in custody. Model and plugin output can propose structured data only; neither authorizes, chooses arbitrary egress, or mutates canonical memory/Chat directly.

## Required Controls

- owner-scoped keys and indistinguishable cross-owner `404`;
- strict manifest path/hash/size/version/schema/executor validation;
- fixed HTTPS host/routes, DNS/IP SSRF guards, timeouts, result limits, and secret redaction;
- exact one-use Action approvals and dispatch-time availability recheck;
- explicit Job account/capability/context/side-effect grants and fenced leases;
- prompt-injection-resistant handling of capability results as quoted data;
- no secret columns, logs, events, results, context, examples, or run artifacts;
- stable Problem Details without upstream bodies or credential material.

## Adversarial Gates

Tests cover prompt injection, model authorization claims, malicious manifest/path/hash/schema, arbitrary URL and private-address egress, cross-user/account access, consequential ambiguity, approval replay/payload/account/target substitution, job grant escalation/mismatch, revoke/disable race, lease fence race, malicious result size/content, credential leakage scans, timeout, malformed provider output, and unknown external outcomes. No Critical/High finding may remain.
