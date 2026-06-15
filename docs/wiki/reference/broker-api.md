# Broker API reference (`/v1/broker`)

The caller plane is the HTTP door for **non-human** callers (a domain MCP, a CLI, a
workflow). A caller posts a request here with its own app-only token.

> Source of truth: `src/Tessera.Broker/CallerBrokerEndpoint.cs` and
> `CallerBrokerService.cs`. For the design, see
> [ADR 0021](../../adr/0021-caller-authentication-plane.md). The chat consumer uses a
> different door — the [MCP surface](mcp-tools.md) at `/mcp`.

---

## Two fail-closed gates

`/v1/broker` opens **nothing** until **both** are true:

1. **A caller authenticator is configured** — `identity.mode = oidc` with an audience.
   Otherwise the endpoint answers `503`.
2. **`egress.enabled`** — otherwise a call reaches no upstream and returns `notallowed`.

Authenticating a caller and opening egress are two separate, deliberate switches.

---

## Authentication

| Header | Required | Meaning |
|---|---|---|
| `Authorization: Bearer <token>` | yes | The caller's **app-only** token (Entra client-credentials), with `aud` = Tessera's audience. A *user* token here is rejected. |
| `X-Tessera-On-Behalf-Of: <token>` | no | A forwarded **end-user** token (Mode U). It must be a user token; the policy independently requires it to be verified. |

With no on-behalf-of header, the caller acts as itself (Mode P).

---

## Request body

`POST /v1/broker` with `Content-Type: application/json`.

| Field | Type | Used by | Meaning |
|---|---|---|---|
| `op` | string | all | The operation: `call` (default), `invoke`, `list-tools`, `check`. |
| `target` | string | all | The provider/target. **Required.** |
| `tool` | string | `call` | The tool name (from `list-tools`). |
| `method` + `path` | string | `invoke` | The tool's HTTP shape — addresses a tool by `(method, path)`. |
| `action` | string | `check` | The action verb to authorise. |
| `args` | object | `call`, `invoke` | JSON arguments filled into the tool's path / allow-listed query / body. |
| `confirm` | bool | `call`, `invoke` | `true` to run a write/booking (step-up) tool. |

---

## Operations

### `list-tools` — what may I call? (dry, no upstream call)

```bash
curl -sS https://tessera.internal/v1/broker \
  -H "Authorization: Bearer $CALLER_TOKEN" -H 'Content-Type: application/json' \
  -d '{ "op": "list-tools", "target": "sonarr" }'
```

Returns the provider tools the resolved caller may call, each with its method, plane,
output class, and whether it is a write.

### `check` — would this be allowed? (dry, no upstream call)

```bash
-d '{ "op": "check", "target": "sonarr", "action": "read:series" }'
```

Returns the policy effect (`allow` / `deny` / `stepup`), the reason, and the resolved
credential status.

### `call` — run a tool by name

```bash
-d '{ "target": "sonarr", "tool": "sonarr_series" }'
```

### `invoke` — run a tool by its HTTP shape

A domain MCP usually already knows the URL it would call. `invoke` maps `(method, path)`
to the declared recipe tool, so the MCP needs no second name map:

```bash
-d '{ "op": "invoke", "target": "sonarr", "method": "GET", "path": "/series" }'
```

`invoke` matches **exact-path** tools only (no `{placeholder}` in the recipe path); a
parameterised tool must use `call`. A `(method, path)` matching no declared tool is
refused — the recipe is the allow-list.

### A write needs `confirm: true`

A first call to a step-up tool without `confirm` returns `stepup`. Re-issue with
`confirm: true` after the human has approved:

```bash
-d '{ "target": "sonarr", "tool": "sonarr_search", "args": {}, "confirm": true }'
```

---

## Response

A successful call returns the upstream result, never the credential:

```json
{ "status": "completed", "httpStatus": 200, "body": "…", "outputClass": null }
```

| Field | Meaning |
|---|---|
| `status` | The outcome (see the table below). |
| `httpStatus` | The upstream HTTP status, when a call was made. |
| `body` | The upstream response body, when a call was made. |
| `detail` | A secret-free explanation (for example the confirmation prompt for a write). |
| `outputClass` | The output class of the body (`metadata` / `fullBody` / …), or null. |

### Status → HTTP code

| `status` | HTTP | Meaning |
|---|---|---|
| `completed` | 200 | The upstream call ran. |
| `stepup` | 409 | A write/booking tool — re-issue with `confirm: true`. |
| `denied` | 403 | The policy denied (no grant, or end-user unverified). |
| `notallowed` | 403 | Egress disabled, not an HTTP recipe, host off the allow-list, or no tool matched. |
| `nocredential` | 424 | No usable credential resolved for this identity. |
| `badrequest` | 400 | Bad arguments (for example a full-body tool called without a handle). |
| `unauthenticated` | 401 | The caller token is missing, invalid, or a user token. |
| (no authenticator) | 503 | `identity.mode = oidc` + audience not configured. |

Every `call` and `invoke` is **authorisation-audited** (the decision is recorded,
secret-free, like a `check`). The credential is never returned, logged, or audited.

---

## Health and status endpoints

| Endpoint | Returns |
|---|---|
| `GET /healthz` | `{ "status": "ok" }` — liveness. |
| `GET /readyz` | `{ "ready": true }` (200) or `{ "ready": false }` (503) — readiness. |
| `GET /status` | A secret-free status: store kind, broker-endpoint state (`enabled` / `fail-closed`), delegation state, and the startup self-test result. |

---

## Where to go next

- Connect a real caller end-to-end: [Connect a domain MCP](../how-to/connect-a-domain-mcp.md).
- The tools you address here come from a recipe: [Policy document reference](policy-document.md).
- The chat consumer's door instead: [MCP tool surface](mcp-tools.md).
