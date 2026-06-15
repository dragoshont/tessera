# Connect a domain MCP (or any non-human caller) to the broker

> The runbook for the **caller authentication plane** ([ADR 0021](adr/0021-caller-authentication-plane.md)):
> how a non-human caller — a domain MCP that egresses through Tessera
> ([ADR 0015](adr/0015-mcp-egress-through-tessera.md)), a CLI, an n8n flow, a CI job —
> authenticates to `POST /v1/broker` and performs a brokered provider call **without
> ever holding the upstream credential**.
>
> The chat consumer uses the MCP surface at `/mcp` (it forwards a *person's* token).
> This door is for a **workload** that proves *its own* identity. The full
> design + use-case scoping is in
> [specs/caller-plane-and-mcp-cutover.md](specs/caller-plane-and-mcp-cutover.md).

---

## The shape

```
   domain MCP / CLI                          Tessera
   (holds NO secret)   ── POST /v1/broker ──▶ authenticate caller (app-only token)
                          Authorization:        + optional end-user (X-Tessera-On-Behalf-Of)
                            Bearer <token>     → PDP (grant) → resolve credential
                          X-Tessera-            → inject → call the allow-listed upstream
                            On-Behalf-Of:      ◀── result (body + outputClass), never the key
                            <user token?>
```

Two **independent** fail-closed gates — the endpoint opens nothing until **both** are set:

1. **A caller authenticator** — `identity.mode=oidc` + an audience (else every caller
   token is rejected and `/v1/broker` answers `503`).
2. **`egress.enabled`** — until then the provider gateway is disabled and every call
   returns `notallowed`, so authenticating a caller still reaches **no** upstream.

---

## 1. Register the caller identity

The caller presents **its own app-only token** (Entra client-credentials /
workload-identity federation) whose `aud` is the value Tessera validates. Follow
[Registering a non-human caller](../README.md#registering-a-non-human-caller-cli--automation--job)
— prefer **workload-identity federation** (no stored secret). The token Tessera sees
is app-only (no user), carrying the caller's `appid` — that `appid` is the caller's
**WHO**.

> Phase 2 (ADR 0021) replaces the bearer token with an **mTLS client certificate**
> (`VerificationMethod.Mtls`) terminated at the ingress; the policy below is
> unchanged — only how the caller identity is established changes.

## 2. Author the policy (recipe + binding + grant)

Three pieces in your policy document (files stay the source of truth —
[ADR 0008](adr/0008-policy-and-identity-administration.md)). The real hosts + keys
live in your **private operator overlay**, never in this repo.

A **recipe** — the target, its base URL, and its tools as `(name, method, path,
action)` with a plane and (optionally) an output class:

```json
{
  "target": "sonarr",
  "egress": "http",
  "upstreamBaseUrl": "https://sonarr.internal/api/v3",
  "injection": "bearer",
  "tools": [
    { "name": "sonarr_series", "method": "GET",  "path": "series",  "action": "read:series" },
    { "name": "sonarr_search", "method": "POST", "path": "command", "action": "use:search", "stepUp": true }
  ]
}
```

A **binding** — the credential that backs the target, `owner: service` (the brokered
household key nobody personally holds — [ADR 0020](adr/0020-credential-ownership.md)):

```json
{ "target": "sonarr", "credential": "sonarr-api-key", "owner": "service" }
```

A **grant** — scoped to the **caller** (`appid`). Pick the delegation mode:

- **Mode P (per-account, no human):** the caller acts under its own service grant —
  omit `onBehalfOf`.
  ```json
  { "caller": "<callerAppId>", "target": "sonarr", "actions": ["read:*", "use:search"] }
  ```
- **Mode U (multi-user):** the caller forwards a person's token; the grant names that
  exact human, and the PDP independently requires the end-user to be verified.
  ```json
  { "caller": "<callerAppId>", "onBehalfOf": "alice@example.com",
    "target": "sonarr", "actions": ["read:*", "use:search"] }
  ```

> Default deny: an `appid` with **no** grant reaches nothing. A `manage:` action is
> default step-up even when `use` is granted ([ADR 0019](adr/0019-app-integrations-and-user-delegated-actions.md)).

## 3. Enable egress *(operator gate — live upstream)*

Set `egress.enabled = true` and add the recipe's host to the SSRF allow-list
(`egress.allowedHosts`). Restrict who can reach `/v1/broker` at the network
(NetworkPolicy) to just your callers. This is the security-sensitive cutover —
real keys + a live upstream path.

## 4. Call `/v1/broker`

`POST /v1/broker` with the caller token in `Authorization`, an optional forwarded
end-user token in `X-Tessera-On-Behalf-Of`, and a JSON body:

| Field | Meaning |
|---|---|
| `op` | `call` (default) · `invoke` · `list-tools` · `check` |
| `target` | the provider/target (required) |
| `tool` | the tool name (required for `call`) |
| `method` + `path` | the tool's HTTP shape (required for `invoke` — see below) |
| `action` | the action verb (required for `check`) |
| `args` | a JSON object filled into the tool's path/query/body |
| `confirm` | `true` to run a write/booking (step-up) tool |

### Two ways to address a tool: `call` (by name) vs `invoke` (by HTTP shape)

A domain MCP usually already calls an upstream by **method + path** (e.g.
`GET /series`). Rather than maintain a second name map, it can address the tool by
that same shape with **`op: "invoke"`** — Tessera resolves it to the declared recipe
tool, so the recipe stays the single source of truth:

```bash
# invoke — the MCP forwards the URL shape it already knows
curl -sS https://tessera.internal/v1/broker \
  -H "Authorization: Bearer $CALLER_TOKEN" -H 'Content-Type: application/json' \
  -d '{ "op": "invoke", "target": "sonarr", "method": "GET", "path": "/series" }'
```

`invoke` matches **exact-path** tools only (no `{placeholder}` in the recipe path); a
parameterized tool must be addressed by name with `op: "call"`. A `(method, path)`
that matches no declared tool is refused — the recipe is the allow-list either way.

**Discover what you may call** (dry — no upstream call):

```bash
curl -sS https://tessera.internal/v1/broker \
  -H "Authorization: Bearer $CALLER_TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{ "op": "list-tools", "target": "sonarr" }'
```

**Make a read call:**

```bash
curl -sS https://tessera.internal/v1/broker \
  -H "Authorization: Bearer $CALLER_TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{ "target": "sonarr", "tool": "sonarr_series" }'
# → { "status": "completed", "httpStatus": 200, "body": "…", "outputClass": null }
```

**A write needs `confirm: true`** (a first call without it returns `stepup`):

```bash
curl -sS https://tessera.internal/v1/broker \
  -H "Authorization: Bearer $CALLER_TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{ "target": "sonarr", "tool": "sonarr_search", "args": {}, "confirm": true }'
```

### Response status → HTTP code

| `status` | HTTP | Meaning |
|---|---|---|
| `completed` | 200 | the upstream call ran; `body` + `outputClass` returned |
| `stepup` | 409 | a write/booking tool — re-issue with `confirm: true` |
| `denied` | 403 | the PDP denied (no grant, or end-user unverified) |
| `notallowed` | 403 | egress disabled, not an HTTP recipe, or host off the allow-list |
| `nocredential` | 424 | no usable credential resolved for this identity |
| `badrequest` | 400 | bad args (e.g. a full-body tool called without a handle) |
| (auth failure) | 401 | the caller token is missing / invalid / a user token |
| (no authenticator) | 503 | `identity.mode=oidc` + audience not configured |

Every `call` is **authorization-audited** (the decision is recorded secret-free,
just like a `check`); the credential is **never** returned, logged, or audited.

---

## What does *not* go through this plane

- **SSH-backed / shell tools** (a private key for arbitrary `kubectl`/host commands)
  are a different credential class and an explicit non-goal — they keep their own
  credential. See [the cutover spec §3](specs/caller-plane-and-mcp-cutover.md#3-use-case-review-which-tools-actually-fit).
- **A static, credential-free MCP** (a baked-in read-only corpus, no upstream call)
  has nothing to broker — don't integrate it.

## See also

- [ADR 0021 — caller authentication plane](adr/0021-caller-authentication-plane.md)
- [ADR 0015 — domain MCPs egress through Tessera](adr/0015-mcp-egress-through-tessera.md)
- [Caller plane & MCP cutover spec](specs/caller-plane-and-mcp-cutover.md)
- [Registering a non-human caller (identity side)](../README.md#registering-a-non-human-caller-cli--automation--job)
