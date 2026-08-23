# Trusted State and Context Release Contract

## Scope

This first Tessera 3.0 slice exposes no network endpoint. It defines the Core
contract consumed by later first-party applications and replaceable workers.
Hermes integration is explicitly out of scope.

## Trusted State Query

A query is bound to one owner and one to 100 exact `(subject, predicate)` keys.
It has a maximum of 100 selected Assertions. Empty selectors, excessive key
counts, and limits outside `1..100` are rejected before repository access.

The result contains, per key:

- at most one current Assertion;
- superseded and rejected historical Assertions;
- explicit conflicted Assertions with no insertion-order winner;
- referenced Evidence available to that owner;
- a truncation flag when the item limit prevents a complete projection.

Selection order is deterministic: current, conflict, superseded, rejected;
then subject, predicate, validity time descending, creation time descending,
and Assertion ID ordinal. Candidates and supported-but-unaccepted Assertions
are not Trusted State.

Authority is categorical and does not derive from confidence. Explicit user
Corrections and assertions, unclassified sources, deterministic systems,
extractions, model inferences, and derivations remain distinguishable.

## Correction

A Correction creates a new current explicit-user Assertion and supersedes the
old current Assertion. It appends the predecessor Assertion ID to lineage. The
old Assertion and its Evidence remain queryable; no value is overwritten.

## Context Release

The caller supplies verified workload and delegated-user identities, a Trusted
State query, an existing `ContextBuildRequest`, and a disclosure reason.
Tessera constructs the fixed policy request:

```text
action: read:context
target: context:<owner-principal-id>
```

The delegated canonical principal, query owner, and context owner must match.
Every decision is audited. Deny and step-up return no envelope and perform no
repository read. Allow projects state and maps only current and conflicted
Assertions into the existing deterministic Context Builder. Raw Evidence
content is not automatically disclosed; Evidence IDs and correction lineage
remain provenance references.

Requested capabilities are context constraints, not capability grants.
Sensitivity filtering and UTF-8 byte budgeting produce explicit omissions.

## Errors and Capability Honesty

Invalid identifiers, empty selectors, out-of-range limits, owner mismatch,
foreign repository results, duplicate current Assertions, or missing referenced
Evidence fail closed. This slice does not claim global state browsing,
transactional multi-key snapshots, account references, provider authority,
semantic retrieval, worker invocation, or an API surface.