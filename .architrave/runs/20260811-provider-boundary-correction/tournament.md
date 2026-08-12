# Tournament of Options

## Option A — Explicit local assembly scan

Scan one canonical operator-owned directory, instantiate parameterless modules in deterministic order, validate them against the hash-pinned package catalog, then atomically publish a registry snapshot. Pros: no dependency, works when assemblies are physically absent, smallest change, easy temporary-directory tests. Cons: reflection/load-context care and explicit atomic validation are required. Risk/blast radius: composition root and packaging. Durability: high. Verification: traversal/symlink/bounds/malformed/duplicate/mismatch/undeclared-capability/partial-publication tests plus startup absence tests. Wins on YAGNI and testability.

## Option B — CLI-generated signed module index

Have CLI/publish generate an index of assembly paths and hashes consumed by Broker. Pros: no directory enumeration and stronger artifact inventory. Cons: adds a second catalog and synchronization/failure modes beside the existing package catalog; local test/run and dirty-tree packaging become more complex. Risk/blast radius: CLI, publish, Broker, container, and release process. Durability: high if maintained. Verification: index generation/tamper/staleness tests plus all Option A identity tests. Loses because current evidence does not justify a parallel artifact authority.

## Option C — Separate plugin host process

Load providers out of process behind RPC. Pros: strongest implementation isolation and independent lifecycle. Cons: new protocol, deployment unit, health model, credential boundary, latency, and failure modes; rewrites far beyond a boundary-preserving extraction. Risk/blast radius: very high across runtime, deploy, security, and operations. Durability: high but disproportionate. Verification: contract, process crash, transport, custody, deployment, and end-to-end suites. Loses on YAGNI and behavior-preservation risk.

## Option D — Defer / document only

Pros: no immediate code regression or test burden. Cons: leaves systemic drift and blocks the requested work. Risk: coupling continues. Durability: none. Loses because the scope and desired boundary are explicit.

## Decision Matrix

| Option | Contract honesty | Absence behavior | Regression risk | Verification | Durability |
|---|---|---|---|---|---|
| Explicit scan | High | Passes | Medium | High | High |
| Generated index | High | Passes | Medium-high | High | High |
| Separate process | High | Passes | Very high | Very high | High |
| Defer | Low | Fails | Low now | None | None |

## Winner

Option A. It stops at the existing-contract/tiny-local-discovery YAGNI rung, reuses the package catalog as the sole artifact authority, and has the narrowest rollback and dirty-tree blast radius.
