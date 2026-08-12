# R2 Decision Log

| Date | Decision | Evidence |
|---|---|---|
| 2026-08-10 | One Broker host, one SQLite product store, one coordinator | R2 architecture; ADR 0028/0031 |
| 2026-08-10 | In-product Chat supersedes external LibreChat for R2 | ADR 0028 |
| 2026-08-10 | Accounts store metadata and opaque custody refs only | ADR 0029 |
| 2026-08-10 | Trusted-local declarative packages; no downloaded code | ADR 0030 |
| 2026-08-10 | GitHub REST is first external plugin | ADR 0030 |
| 2026-08-10 | Additive SQLite scheduler, one active Alpha replica | ADR 0031 |
| 2026-08-10 | Explicit Remember/Correct only; conversation is not truth | `MEMORY_IN_CHAT.md` |
| 2026-08-10 | Physical memory erase is deferred until retention, backup, lineage, and recovery semantics are settled; Alpha exposes truthful stop-using only | `MEMORY_IN_CHAT.md`; `R2_API_CONTRACT.md` |
| 2026-08-10 | Live external checks remain blocked without owner configuration | `R2_TEST_MATRIX.md` |
