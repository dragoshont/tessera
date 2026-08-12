# Tournament of Options

## Option A — Minimal Complete Cross-Referenced Set

Create every required file with complete acceptance coverage, but centralize detailed contracts in `ARCHITECTURE.md` and use concise cross-references elsewhere.

- Pros: satisfies the artifact and content contract with low duplication and a small diff.
- Cons: readers must jump between files; security, execution, and test ownership are harder to review independently.
- Risk/blast radius: low; ambiguity is concentrated in the canonical document.
- Durability: medium; easy to maintain, but weak as an engineering contract.
- Verification burden: required-file, link, status, and markdown checks.
- Reversibility: high because all files are new.
- Contract fit: complete but less reviewable.
- Result: viable, but loses because named artifacts are intended as independent handoff surfaces.

## Option B — Contract-Focused Documentation Set

Create a concise linked set in which each file owns one concern, grounded in observed seams and explicit `CURRENT`, `PROPOSED R0`, `PRODUCT-GATED`, and `OPEN` labels.

- Pros: complete, capability-honest, testable, and consistent with the product reduction.
- Cons: requires cross-document terminology and status discipline.
- Risk/blast radius: medium-low and confined to new documentation.
- Durability: high because ownership, boundaries, and unresolved choices are explicit.
- Verification burden: source checks, required concepts, link/status consistency, semantic review, run validation, and markdown checks.
- Reversibility: high because all product artifacts are new.
- Contract fit: high; follows Core/adapter/broker seams already recorded in the repository map.
- Result: wins.

## Option C — Full Future-Architecture Documents

Create every required file as a comprehensive destination specification including graph, semantic retrieval, model routing, providers, and trusted edge.

- Pros: records the broad vision and minimizes future documentation gaps.
- Cons: turns provisional choices into apparent commitments, conflicts with earned structure, and obscures R0 versus MVP.
- Risk/blast radius: high architecture and product drift despite documentation-only changes.
- Durability: low because unearned decisions will require correction.
- Verification burden: high and mostly impossible without implementation/product evidence.
- Reversibility: medium; prose can be changed, but downstream design may already rely on it.
- Contract fit: low.
- Result: loses on YAGNI and capability honesty.

## Option D — Defer Until Integration

Record only the run decision and wait for Kernel code before writing the requested set.

- Pros: all implementation claims could be source-observed.
- Cons: no architecture contract would guide integration, and the explicit documentation request would remain unmet.
- Risk/blast radius: low immediate change risk; high coordination risk.
- Durability: deferred.
- Verification burden: none now, larger later.
- Reversibility: high.
- Contract fit: does not satisfy the task.
- Result: loses; uncertainty is manageable with explicit provisional status.

## Decision Matrix

| Option | Meets artifacts | Capability honesty | Product-scope fit | Durability | Test burden | Result |
|---|---:|---:|---:|---:|---:|---|
| A | Yes | High | High | Medium | Low | Lose |
| B | Yes | High | High | High | Medium | Win |
| C | Yes | Low | Low | Low | High | Lose |
| D | No | High | Neutral | Deferred | None now | Lose |

## Winner

Option B. It is the first YAGNI rung that satisfies the request: reuse the existing repository map, audit, architecture, ADRs, and test-project seams, then add only the required documentation contracts.
