# Tournament of Options

## Option A — Minimal Safe Fix

Project FollowUp-shaped fields onto generic R0 assertions. Small diff and low initial
cost, but aggregate invariants would be spread across callers and candidate/conflict
transitions would be easy to bypass. Medium blast radius, low durability, deceptively
high verification burden. Loses.

## Option B — Proper Architectural Fix

Add one workflow-specific FollowUp aggregate/application service while reusing R0
Evidence/Event/Assertion/Context primitives and the existing SQLite adapter. Moderate
local code and test cost, low-to-medium blast radius, explicit ownership, durable
correction/conflict/replay semantics. Wins.

## Option C — Defer / Ask More

Ship only specs and fixtures. Lowest implementation risk but does not satisfy the
authorized end-to-end request or prove restart-safe compounding continuity. Loses.

## Option D — General Ontology

Introduce Commitment/Situation/Entity/Claim or graph/vector infrastructure. Broad
reuse is speculative; blast radius, migration risk, and test burden are high, and it
directly violates scope. Loses.

## Decision Matrix

| Option | Product truth | Blast radius | Durability | Verification | Result |
|---|---|---|---|---|---|
| A | Partial | Medium | Low | Medium | Lose |
| B | Full | Low-medium | High | Focused but substantial | Win |
| C | None | Low | None | Low | Lose |
| D | Speculative | High | Unknown | High | Reject |

## Winner

Option B. It is the first YAGNI rung that meets every criterion: reuse existing
Kernel, SQLite, ASP.NET, React, Storybook, and component primitives, adding only the
workflow state and boundary contracts demanded by the selected vertical.
