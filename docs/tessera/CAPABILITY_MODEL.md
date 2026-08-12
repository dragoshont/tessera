# Capability Model

## Contract

A capability is a replaceable, versioned function available to Tessera. Its descriptor exposes:

- stable ID and version;
- description and structured input/output schemas;
- side-effect class;
- required permissions and allowed sensitivity classes;
- idempotency support;
- provider/external verification support.

Side-effect classes are `ReadOnly`, `LocalReversible`, `ExternalReversible`, `ExternalCommunication`, and `HighImpact`.

## Invocation

An invocation carries canonical owner, task/workflow ID, capability ID/version, structured input, optional authorization ID, and idempotency key. Exact version resolution prevents silent downgrade or provider guessing.

The registry exposes descriptors for deterministic policy inspection. It does not itself authenticate, authorize, discover remote code, or enforce provider permissions.

## Implementations

- `DeterministicCapability` proves the local interface and idempotency requirement.
- Fake capabilities may represent success, failure, or unknown outcome in tests.
- Provider adapters may later normalize provider payloads and verification receipts behind the same boundary.
- Models and agents can implement worker/capability contracts but remain replaceable computation, never canonical state or authority.

## Policy Boundary

A model, worker, capability output, or evidence item cannot declare its own permission, broaden its descriptor, or satisfy approval. Broker policy must inspect the declared side-effect and permission metadata before invocation.

## Current Non-Scope

There is no chosen provider, cloud model, capability marketplace, dynamic code loading, distributed discovery, or live-write integration in R0. Exact capability-policy enforcement at the broker/Kernel composition boundary remains a future gate before external effects.