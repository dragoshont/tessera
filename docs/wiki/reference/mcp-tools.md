# MCP tool surface reference (`tessera_*`)

The MCP surface at `/mcp` is the door for a **chat consumer** (an assistant that
forwards a person's login token). It offers five tools. They are read-mostly: the only
one that can perform a write is `tessera_call`, and only after an explicit human
confirmation.

> Source of truth: `src/Tessera.Mcp/TesseraMcpTools.cs`. This door validates a
> *forwarded end-user* token; non-human callers use the [Broker API](broker-api.md) at
> `/v1/broker` instead.

---

## How the token is read

Each tool reads the forwarded end-user token from the request:

- from the `Authorization: Bearer <token>` header, or
- from a configured alternate header (`TesseraMcpOptions.AlternateTokenHeader`), when set.

A `Bearer ` prefix is stripped if present. The token is validated; an invalid or
missing token resolves to *no identity*, and the tools report that they are not
authenticated.

---

## The tools

### `tessera_whoami`

Reports the verified identity Tessera resolved for this call — the calling workload
and, if delegated, the signed-in person. Use it to confirm per-user delegation is
working.

Returns: `{ authenticated, caller, user, isAutomation, detail }`.

### `tessera_list_targets`

Lists the configured providers/targets and whether the signed-in person is granted
access to each. Read-only; makes no upstream call.

Returns: `{ authenticated, targets[], detail }` where each target carries its name, its
exposed actions, whether it is granted, and its egress mode.

### `tessera_check_access`

Authorises a `(target, action)` for the signed-in person and reports the policy decision
plus whether a usable credential is present. Read-only — it does **not** call the
upstream.

| Argument | Meaning |
|---|---|
| `target` | The provider/target, e.g. `health-portal`. |
| `action` | The action verb, e.g. `read:results`. |

Returns: `{ effect, reason, credentialStatus, ok }`.

### `tessera_list_provider_tools`

Lists the provider operations (per target) the signed-in person may call — each with
its method, action plane (`read` = observe, `use` = operate, `manage` = reshape), and
whether it is a write that needs confirmation. Read-only.

Returns: `{ authenticated, tools[], detail }`.

### `tessera_call`

Calls a provider operation as the signed-in person — Tessera injects that person's
credential and returns only the result. The caller never sees the secret.

| Argument | Meaning |
|---|---|
| `target` | The provider/target. |
| `tool` | The operation name (from `tessera_list_provider_tools`). |
| `args` | Optional JSON arguments/body. |
| `confirm` | Set `true` **only** for a write/booking, after the person has confirmed the exact details. |

For a **write**, the safe pattern is enforced by design: read the exact details back to
the person, get an explicit yes, then call again with `confirm: true`. A write never
runs with `confirm: false`.

Returns: `{ status, httpStatus, body, detail, outputClass }` — the same shape as the
[Broker API](broker-api.md#response).

---

## Read-only by design (iteration 1)

The MCP tools prove per-user delegation and report credential status. The actual
injection egress is **gated in the broker host**, so deploying the MCP surface never
opens an upstream path on its own. Egress is enabled deliberately by an operator — see
[Enable egress safely](../how-to/enable-egress-safely.md).

---

## Where to go next

- The non-human caller's door instead: [Broker API reference](broker-api.md).
- The action planes these tools surface: [Vocabulary](vocabulary.md#action-planes).
- Set up a chat consumer: [Connect a domain MCP](../how-to/connect-a-domain-mcp.md).
