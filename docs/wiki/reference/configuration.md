# Configuration reference (`tessera.json`)

The broker reads its settings from `tessera.json` (path via `--config`, or defaults
plus environment). This page lists **every** field: its type, default, and meaning.

> Source of truth: `src/Tessera.Core/Configuration/TesseraConfig.cs`. The broker
> **refuses to start** if validation fails (see [Validation rules](#validation-rules)).

A minimal, safe starting file:

```json
{
  "server":   { "host": "127.0.0.1", "port": 8080 },
  "identity": { "mode": "oidc", "oidc": { "issuer": "https://login.example/v2.0", "audience": "<app-id>" } },
  "policy":   { "default": "deny", "document": "grants.json" },
  "audit":    { "enabled": true, "path": "-" },
  "egress":   { "enabled": false, "allowedHosts": [] }
}
```

---

## `server`

The HTTP listener.

| Field | Type | Default | Meaning |
|---|---|---|---|
| `host` | string | `"127.0.0.1"` | Bind address. Loopback by default. |
| `port` | int | `8080` | Bind port. Must be 1–65535. |

`host` of `127.0.0.1`, `::1`, or `localhost` counts as **loopback**, which the
validator uses to permit `dev` identity mode.

---

## `identity`

Who may call, and how they prove it.

| Field | Type | Default | Meaning |
|---|---|---|---|
| `mode` | string | `"mtls"` | One of `mtls`, `oidc`, `dev`. `dev` disables caller verification and is allowed **only on loopback**. |
| `trustDomain` | string | `"tessera.local"` | The workload trust domain (used for the self-test caller id). |
| `oidc` | object | — | OIDC validation settings (below). |

### `identity.oidc`

Validation of forwarded end-user / app-only tokens (Microsoft Entra).

| Field | Type | Default | Meaning |
|---|---|---|---|
| `issuer` | string | `""` | The expected token issuer. **Required** when `mode = oidc`. |
| `audience` | string | `""` | The expected `aud`. **Empty means delegation fails closed** — every token is denied until an audience is set. |
| `tenantId` | string | `""` | The expected tenant (`tid`). |
| `allowedTenants` | string[] | `[]` | For a multi-tenant authority, the tenant IDs allowed to sign in. Empty = any tenant matching the issuer template. |
| `spaScope` | string | `""` | The OAuth scope the admin-portal SPA requests at sign-in. Empty derives `openid profile email <audience>/.default`. Consumed only by the portal sign-in, never the broker. |

---

## `policy`

The authorisation rules and their default.

| Field | Type | Default | Meaning |
|---|---|---|---|
| `default` | string | `"deny"` | The default effect. **`allow` is rejected as fail-open** — keep it `deny`. |
| `document` | string | `"grants.json"` | Path to the [policy document](policy-document.md) (grants + bindings + recipes). |
| `manageRequiresStepUp` | bool | `true` | When true, an authorised `manage:` action always requires a human step-up, even if the grant did not list it. Set false to loosen the whole control plane (rarely wanted). |

---

## `audit`

The secret-free decision log.

| Field | Type | Default | Meaning |
|---|---|---|---|
| `enabled` | bool | `true` | Whether audit is written. |
| `path` | string | `"-"` | Audit destination. `-` means stdout (right for containers). |
| `tailCapacity` | int | `1000` | How many recent entries the in-memory activity feed keeps for the portal. `0` disables the in-memory tail (the durable log is unaffected). |

The audit never contains a secret value — only identifiers, the decision, and the
resolved credential *status*.

---

## `egress`

The outbound-call settings. **Off by default** — deploying the broker opens no path to
any upstream.

| Field | Type | Default | Meaning |
|---|---|---|---|
| `enabled` | bool | `false` | Whether the broker may make injected upstream calls. |
| `allowedHosts` | string[] | `[]` | The SSRF allow-list: the upstream hosts the broker may reach. **Required (non-empty) when `enabled` is true.** |
| `allowPlainHttp` | bool | `false` | When true, plain `http://` is permitted **to allow-listed hosts only** — the opt-in for internal services that do not speak TLS (for example a cluster-internal address). The host allow-list still applies; this only relaxes the scheme. |

See [Enable egress safely](../how-to/enable-egress-safely.md) for the recommended way
to turn this on.

---

## `portal`

The admin portal (a thin convenience layer; ADR 0016).

| Field | Type | Default | Meaning |
|---|---|---|---|
| `admins` | string[] | `[]` | The operator allow-list: verified principals (`oid` / `preferred_username`) who may enter the operator surface. Everyone else is a Member who sees only their own connections. Empty = self-service only. |
| `webRoot` | string | `null` | Path to the built SPA (`web/dist`). When set and present, the broker serves the portal at `/`. Unset = API only. |

---

## `liveView`

The captcha live hand-off to a browser worker (ADR 0016 §3). **Off by default.**

| Field | Type | Default | Meaning |
|---|---|---|---|
| `enabled` | bool | `false` | Whether a browser worker is wired. Off = the hand-off is unavailable (fail-closed; the endpoint returns 503). |
| `workerArmUrl` | string | `""` | The absolute URL the broker posts an arm request to. **Required and must be absolute** when `enabled`. |
| `defaultTtlSeconds` | int | `300` | The handle lifetime when the worker does not pin its own. Short by design. |

The worker caller token (if any) comes from the environment
(`TESSERA_LIVEVIEW_WORKER_TOKEN`), never the config file — it is a secret.

---

## `refresh`

The background session-refresh (Mode U rotation owner; ADR 0015). **Off by default**
and inert unless `egress.enabled` too.

| Field | Type | Default | Meaning |
|---|---|---|---|
| `enabled` | bool | `false` | Whether the background rotation owner runs. |
| `intervalSeconds` | int | `1800` | How often a rotation pass runs. |
| `acknowledgeSingleWriter` | bool | `false` | The operator's explicit assertion that the broker runs as **exactly one replica**. The refresher is the sole session owner and there is no leader election, so refresh stays inert until this is true. |

Only recipes that declare `rotation.owner = tessera` **and** a `refreshSpec` are ever
touched.

---

## Validation rules

`tessera validate` (and startup) checks these. Any failure prevents the broker from
serving:

- `server.port` must be 1–65535.
- `identity.mode` must be `mtls`, `oidc`, or `dev`.
- `identity.mode = dev` is allowed **only on loopback** (it disables caller
  verification).
- `identity.mode = oidc` requires `identity.oidc.issuer`.
- `policy.default` must be `deny` or `allow`; **`allow` is rejected** (fail-open).
- `egress.enabled = true` requires a non-empty `egress.allowedHosts`.
- `liveView.enabled = true` requires a valid absolute `liveView.workerArmUrl` and a
  positive `defaultTtlSeconds`.
- `refresh.enabled = true` requires `egress.enabled = true`, a positive
  `intervalSeconds`, and `acknowledgeSingleWriter = true`.

---

## Where to go next

- Validate your file: [CLI reference](cli.md) (`tessera validate`).
- Write the rules it points at: [Policy document reference](policy-document.md).
- Turn egress on the safe way: [Enable egress safely](../how-to/enable-egress-safely.md).
