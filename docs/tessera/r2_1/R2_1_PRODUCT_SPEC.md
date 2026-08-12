# Tessera R2.1 Product Specification

## Objective

R2.1 turns the internally complete R2 product into an operable Alpha: one-command local startup, truthful setup states, real provider configuration through Tessera custody, streaming Chat, durable Memory and Jobs, exact Actions, restart recovery, and supported backup/restore.

## User Contract

- Chat is primary. Without a model it says configuration is required and links to Settings.
- Model and GitHub credentials are write-only and remain in credential custody.
- Provider identity, provider-reported scopes, Tessera permissions, Account health, and capability availability are distinct.
- Chat text streams transiently; only the final validated message is durable.
- Memory promotion is explicit. Correction preserves history and Why links to Evidence.
- Jobs persist schedules, UTC instants, timezone, context policy, Accounts, capabilities, Actions, outputs, and run history.
- Consequential work always crosses the exact, expiring, one-use Action boundary.
- Backend readiness means the process, SQLite, and scheduler can operate. Missing model/plugins/Accounts remain visible configuration states, not fake outages.

## Runtime Topology

The Alpha is a single .NET modular monolith hosting the API, built React SPA, Chat worker, scheduler, provider adapters, plugin catalog, and SQLite product store. Lowkey provides development credential custody. Ordinary dogfood does not require Kubernetes.

## Scope

Implemented: OpenAI-compatible model Accounts/profiles; SSE Chat and tools; GitHub identity/read and approval-gated issue creation when permission is provider-evidenced; local Memory/Why/time capabilities; durable Jobs; plugin/account lifecycle; backup/restore; live verification harness.

Not added: new providers, ontology, vector/graph storage, distributed scheduler, autonomous side-effect policy, deployment mutation.

## Release Rule

`ALPHA_DOGFOOD_READY` requires a real model PASS. With all internal gates green but no external credentials, status is `INTERNALLY_READY_EXTERNAL_VERIFICATION_BLOCKED`.