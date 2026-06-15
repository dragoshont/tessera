# Add a provider recipe

This task writes a **recipe** for a new provider, so Tessera can broker calls to it. A
recipe names the provider's base URL, how to inject its credential, and the operations
(tools) it offers.

**Before you start**, decide one thing: *how does this provider authenticate?* That
decides the `injection` value.

| The provider uses… | Set `injection` to | The credential bundle holds |
|---|---|---|
| An API-key header (`X-Api-Key`, etc.) | `apikey` | `{ "access_token": "<the key>" }` |
| An OAuth bearer token | `bearer` | `{ "access_token": "<the token>" }` |
| A session cookie | `cookies` | cookies (or `access_token` via `cookieMap`) |

> Full field list: [Policy document reference](../reference/policy-document.md). The
> injection options: [Vocabulary](../reference/vocabulary.md#injection-kinds).

---

## Step 1 — Find the operations you need

List the exact HTTP calls the caller makes: the method, the path (after the base URL),
and any query parameters. For example, for an API-key media service:

| Operation | Method | Path | Query |
|---|---|---|---|
| List series | `GET` | `series` | — |
| List missing | `GET` | `wanted/missing` | `pageSize`, `sortKey` |
| Trigger a search | `POST` | `command` | — |

## Step 2 — Choose an action verb per operation

Give each operation an action verb, written `plane:detail`:

- `read:` for observe (list, status, search).
- `use:` for operate (trigger a search, pause, resume).
- `manage:` for reshape (change settings) — these default to step-up.

A write operation should also set `"stepUp": true`.

## Step 3 — Write the recipe

```json
{
  "target": "media-service",
  "egress": "http",
  "injection": "apikey",
  "upstreamBaseUrl": "http://media-service.internal:8989/api/v3",
  "tools": [
    { "name": "ms_series",  "method": "GET",  "path": "series",         "action": "read:series" },
    { "name": "ms_missing", "method": "GET",  "path": "wanted/missing", "action": "read:missing", "query": ["pageSize", "sortKey"] },
    { "name": "ms_search",  "method": "POST", "path": "command",        "action": "use:search", "stepUp": true }
  ]
}
```

Notes:

- The `path` is appended to `upstreamBaseUrl`. Keep the base URL's API prefix
  (`/api/v3`) in the base URL, and the operation path (`series`) in the tool.
- Only the query names you list in `query` are forwarded — an agent cannot add others.
- For a non-default API-key header, set `injectionHeader` (for example `X-Plex-Token`).

## Step 4 — Add the binding and the grant

The recipe says *how* to call. You still need:

- a **binding** — which stored secret backs the target:
  ```json
  { "target": "media-service", "credential": "tessera-media-service", "owner": "service" }
  ```
- a **grant** — who may call it:
  ```json
  { "caller": "<callerAppId>", "target": "media-service", "actions": ["read:*", "use:search"], "stepUpActions": ["use:search"] }
  ```

## Step 5 — Store the credential in the right shape

The store secret named in the binding must hold a **bundle**, not a raw string. For an
API key:

```json
{ "access_token": "the-real-api-key" }
```

## Step 6 — Validate

```bash
tessera validate --config tessera.json --grants grants.json
```

Fix any problem it reports. The recipe does nothing until [egress is
enabled](enable-egress-safely.md).

---

## Path placeholders and handles

- A `{name}` segment in `path` is filled from a same-named argument, URL-encoded.
- A `{handle}` segment is filled from a result handle returned by a prior search — this
  is how a `fullBody` tool reads one item without a bulk path.

```json
{ "name": "ms_read", "method": "GET", "path": "item/{handle}", "action": "read:item", "resultClass": "fullBody" }
```

---

## Where to go next

- Turn on the upstream path: [Enable egress safely](enable-egress-safely.md).
- The complete field reference: [Policy document reference](../reference/policy-document.md).
- The vocabulary used here: [Vocabulary](../reference/vocabulary.md).
