# Run the admin portal

This task turns on Tessera's **admin portal** — a small, read-mostly web interface
served at `/`. It shows people, connection health, and an activity feed. It **never**
shows a secret value.

> Field reference: [Configuration → `portal`](../reference/configuration.md#portal).
> The design: [ADR 0016](../../adr/0016-admin-portal.md).

---

## What the portal is — and is not

- **It is** a convenience layer: see who can act, whether a connection is healthy, and
  what happened (a secret-free activity feed). An operator can also add a person without
  editing files.
- **It is not** the source of truth. The policy files stay authoritative (GitOps). The
  portal never reveals a secret value, and it is OIDC-gated by the same identity provider
  as the rest of Tessera.

A credential broker is never on the public edge, so keep the portal **cluster-internal**
(no public ingress). Reach it with a port-forward, or through your access gateway.

---

## Step 1 — Point the broker at the built SPA

Set `portal.webRoot` to the built portal output (the `web/dist` directory). When it is
set and the directory exists, the broker serves the portal at `/`:

```json
"portal": {
  "webRoot": "/app/web/dist",
  "admins": ["alice@example.com"]
}
```

If `webRoot` is unset, the broker serves the API only — the portal is simply absent.

## Step 2 — Set the operators

`portal.admins` is the **only** portal authorisation datum. List the verified principals
(their `oid` or `preferred_username`, for example an email) who may enter the operator
surface:

```json
"admins": ["alice@example.com", "bob@example.com"]
```

- Everyone else is a **Member**: they see only their **own** connections.
- An **empty** `admins` list means there is no operator — the portal is self-service
  only.

This list lives in config, so it is a reviewable change like every other rule.

## Step 3 — Reach it

The portal is same-origin with the API, so its requests need no CORS. Because it is
cluster-internal, reach it with a port-forward:

```bash
kubectl -n <namespace> port-forward svc/tessera 8080:8080
# then open http://localhost:8080
```

Sign in with the same identity provider as the rest of Tessera. Operators see the admin
sections; members see their own connections.

---

## What you can do in the portal

- **People** — who appears (derived from bindings; no database), and who is an Admin vs
  a Member.
- **Connections** — each connection's health (present / absent) and its credential
  **owner** (so "who can act as me" is honest about shared service keys).
- **Activity** — a secret-free feed of recent decisions (from the in-memory tail; see
  `audit.tailCapacity`).
- **Add a connection** — for a self-service user, without editing files (on a read-only
  GitOps mount, the add is in-memory only).

---

## Where to go next

- The portal fields: [Configuration → `portal`](../reference/configuration.md#portal).
- The design and its limits: [ADR 0016](../../adr/0016-admin-portal.md).
- The awareness model behind it: [ADR 0017](../../adr/0017-awareness-dashboard.md).
