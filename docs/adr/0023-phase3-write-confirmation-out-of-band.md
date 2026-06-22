# ADR 0023 — Phase 3 write confirmation: a server-issued, out-of-band approval (resolving HL-18)

> **Status: ACCEPTED — Tessera backend implemented + adversarial-judge PASS (2026-06-21).** The
> server-side out-of-band confirmation below is built and verified; the `apple-mcp` write tool, the
> portal approval UI, and the homelab `manage:dav` grant are the remaining (operator-gated)
> activation steps. This supersedes the placeholder `X-Tessera-Confirm` echo (ADR 0022 §6-F5 /
> **HL-18**), which was **forgeable by the MCP** and has been removed.

## Implementation status (2026-06-21)

- **Tessera backend: DONE + judge PASS.** T-1..T-5 implemented — the held-write store
  (`IWriteChallengeStore` / `InMemoryWriteChallengeStore`), the 409-challenge egress gate, the
  self-scoped portal approve/deny endpoints, the audit, and the removal of the forgeable
  `X-Tessera-Confirm`. 423 tests green, strict-analyzer clean. The adversarial judge found and I
  fixed one **Critical** (the hash must bind the validated **upstream URL**, not the constant
  `/v1/egress/{target}` route — otherwise an empty-body `DELETE` of object A and B hashed alike,
  enabling swap-after-approve) and hardened one **Medium** (bind the WebDAV `Depth` too).
- **Pending activation:** `apple-mcp` `create_event` (flag-gated, challenge-aware); the portal
  approval UI; the homelab `manage:dav` grant + the Tessera restart window (operator-gated).
- **Backlog (judge, non-blocking):** per-principal challenge sub-quota (C2); the 256 KiB body cap
  (C3); authorize on `oid` not `preferred_username` (C4, repo-wide); surface the MOVE/COPY
  destination in the portal DTO (C1-residual); a forward-header allow-list (C5-full).

## 1. Context — why the current confirm is not safe

Tessera Phase 0 maps write methods (`PUT`/`POST`/`DELETE`/`MKCALENDAR`/`MOVE`) to `manage:dav`
with a step-up gate, and the gate today keys on an `X-Tessera-Confirm: true` request header.
That header is **set by `apple-mcp`** — the very component Tessera must not trust (defense in
depth / confused-deputy, HL-2/3). A buggy, compromised, or prompt-injected MCP (or a model that
the MCP faithfully relays) can therefore set `X-Tessera-Confirm: true` and mutate the user's real
calendar **with no genuine human approval**. Echo-back confirmation is forgeable by construction.

**Requirement:** a write must be approved through a channel that `apple-mcp` **cannot forge** —
authenticated **directly by the human**, **out-of-band** from the MCP, and **bound to the exact
write** so the request cannot be swapped after approval.

## 2. Constraints discovered

- **MCP elicitation is not available in this stack.** The 2025-06-18 MCP spec (SDK `^1.17.1`,
  which LibreChat ships) defines server→user *elicitation*, but the LibreChat fork does **not**
  implement it (no elicitation handlers in `packages/api`). So the human cannot be prompted
  out-of-band *through the chat client*. Even if it could, an elicitation reply relayed by
  `apple-mcp` would itself be forgeable — the approval must reach Tessera directly.
- `apple-mcp` is an untrusted relay: anything it forwards (a header, a confirm param, a relayed
  user click) can be fabricated. The approval must originate from the human against Tessera.

## 3. Options

1. **Tessera portal challenge-approval (RECOMMENDED).** On a `manage:` write with no approved
   challenge, Tessera returns **409 + a single-use challenge id** and **stores the pending write
   server-side** — keyed by `{verified onBehalfOf, target, exact method+URL+body, human summary,
   token — out-of-band from `apple-mcp`), sees the human-readable diff, and Approves/Denies. On
   approval Tessera executes the **exact stored request** and marks the challenge consumed. The MCP
   re-requests with the challenge id to retrieve the result. Reuses the existing portal (ADR 0016 /
   `PortalService.cs`); the confirmation never transits the MCP.
2. **Push-to-approve (layer on #1).** Tessera sends a push (ntfy / Apple push) with the summary and
   a deep link to the portal approval. UX nicety; identical security core to #1.
3. **OAuth step-up with Rich Authorization Requests (RAR, RFC 9396 / FAPI transaction
   authorization).** Bind the exact write into a fresh Authentik authorization the user approves.
   Strongest + standards-based, but needs Authentik RAR support and chat-triggered step-up —
   over-engineered for a family homelab. **Defer** (revisit if Tessera serves third parties).
4. **(Rejected) MCP-relayed confirm** — the `X-Tessera-Confirm` header, a `confirm` tool param, or
   a relayed elicitation reply. All forgeable by the MCP → fails the HL-18 requirement. This is the
   status quo to remove.

## 4. Decision

Adopt **Option 1** (Tessera portal challenge-approval), implemented as a **hash-bound re-request** —
a refinement of "execute-on-approve" that fits the egress streaming path with no forward-in-portal
and no result store, and is equally non-forgeable:

1. A manage-plane write with no approved challenge is **held**: Tessera stores it keyed by
   `(verified onBehalfOf, target, content-hash)` and returns **409 + a single-use challenge id + a
   human summary**. Nothing is forwarded.
2. The person it is for **approves it in the portal** — their own forwarded token; the calling agent
   is app-only, has no portal identity, and cannot approve — scoped so only the bound principal may
   decide (an operator cannot approve another's write).
3. The caller **re-issues the identical write**; Tessera matches the approved challenge by content
   hash, **consumes it exactly once**, and forwards. A different request hashes differently → a
   fresh approval is required (no swap); a second attempt finds nothing approved (no replay); a
   restart drops the held state → re-request + re-approve (fail-safe, never a silent forward).

The **content hash binds method + the validated upstream absolute URL + the WebDAV control headers
(Destination / Overwrite / Depth) + body**, so an approval cannot be re-pointed at a different
object, host, or move-target, nor widened (deep vs shallow). This closes HL-18: a compromised or
prompt-injected MCP/model cannot mutate a calendar without a real, identity-bound, single-use,
content-bound human approval that Tessera owns end-to-end.

## 5. MCP-side flow (`apple-mcp` `create_event` / `create_reminder`)

1. `create_event(summary, start, end, location?, calendar?)` builds the VEVENT and issues the
   brokered `PUT` (`manage:dav`). Tessera replies **409 + challenge + summary** (no execution).
2. The tool returns a least-disclosure prompt: *"Pending your approval — open the Tessera portal to
   confirm creating ‘<summary>’ on <date> in <calendar>."* It **does not** claim success.
3. After the human approves out-of-band, Tessera executes the stored request; a follow-up tool call
   (or status check with the challenge id) reports the real result.

## 6. Build checklist (Tessera — all before any `manage:dav` grant)

- **T-1** Pending-write store: `challenge → {verified onBehalfOf, target, exact method+URL+body,
  human summary, expiry, single-use}`. Short TTL (≈2–5 min).
- **T-2** On a `manage:` write without an approved challenge → **409 + challenge + summary**; do
  **not** execute.
- **T-3** Portal approval page (operator-authenticated): list the signed-in identity's pending
  writes, show the human-readable diff, Approve/Deny; on approve, execute the **exact** stored
  request; consume the challenge.
- **T-4** Authorize approval strictly to the **verified onBehalfOf** (a user approves only their own
  pending writes). Audit issue / approve / deny / execute.
- **T-5** Remove the forgeable `X-Tessera-Confirm` echo path (ADR 0022 §6-F5) once the challenge
  lands.
- **T-6** *(optional)* Push notification with a deep link (Option 2).

**Then (`apple-mcp`):** add `create_event` / `create_reminder` behind `APPLE_MCP_ENABLE_WRITES`
(default off) and the `manage:dav` grant; `PUT → manage:dav` already exists (Phase 0); unit-test the
preview/challenge/approved paths; keep **inert** until T-1..T-5 + grant + flag are all in place.

## 7. Consequences

- Each write needs one explicit human portal approval (slightly slower; mitigated by push). Reads
  are unchanged.
- A compromised, buggy, or prompt-injected MCP/model **cannot** mutate calendars without a real,
  identity-bound, single-use approval that Tessera enforces — defense in depth, confused-deputy safe.
- Cross-refs: ADR 0022 (§3/§6-F5), ADR 0016 (portal), ADR 0021 (caller plane), HL-18.
