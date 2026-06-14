# Live-view worker contract (Job A — captcha hand-off)

> The exact `POST /live-view/arm` request/response the broker's
> [`HttpLiveViewWorker`](../../src/Tessera.Broker/HttpLiveViewWorker.cs) speaks, so a
> browser worker (e.g. the homelab noVNC/sessionkeeper pool) can implement the other
> half. Pairs with [ADR 0016 §3](../adr/0016-admin-portal.md) (Live hand-off) and
> [ADR 0002](../adr/0002-broker-worker-topology.md) (broker/worker trust zones).
>
> **Status:** the broker side is built, wired, and fail-closed (commit `164309f`).
> This document is the spec the worker must satisfy. Standing up the worker service
> and exposing its noVNC surface is a **🛑 operator step** (plan §1.3) — it activates
> a medical seeding surface and the staged playwright pool.

---

## 1. The shape of the exchange

The broker never touches the browser, the CDP channel, or the harvested cookie. It
holds a single seam — `ILiveViewWorker.ArmAsync` — and asks the worker to *arm* a
live session. The worker navigates to the provider, mints a short-TTL embeddable
remote-browser URL, and (after the human logs in / solves the captcha) harvests the
resulting session bundle **worker→vault itself**. The cookie never crosses the
broker. This is the cardinal worker-trust-zone invariant.

```
Portal ──(POST /portal/connections/{id}/live-view)──▶ Broker
                                                        │
                                       ArmAsync(LiveViewWorkerRequest)
                                                        │
                                                        ▼
                                              POST {workerArmUrl}
                                              { connectionId, principal, provider }
                                                        │
                                                        ▼
                                                   Browser worker
                                              (mints noVNC URL, maps slot)
                                                        │
                                          { liveViewUrl, targetHostname, … }
                                                        ▼
Portal ◀──(LiveViewHandle: url + countdown + target strip)── Broker
   │
   └─ human logs in / solves captcha in the embedded stage
                                                        │
                                   worker harvests cookie ──▶ vault (NEVER the broker)
```

---

## 2. Request — broker → worker

`POST {workerArmUrl}` (the absolute URL set in `liveView.workerArmUrl`), body
`application/json`:

```json
{
  "connectionId": "health-portal:alice@example.com",
  "principal": "alice@example.com",
  "provider": "health-portal"
}
```

| Field | Type | Meaning |
| --- | --- | --- |
| `connectionId` | string | The `{provider}:{principal}` connection to seed. |
| `principal` | string | The verified person the seeded session must belong to. **The worker MUST bind the session to this exact principal** — never reuse a slot armed for someone else (cross-person isolation). |
| `provider` | string | The provider/target the worker should arm a login for (the recipe target; the part of `connectionId` before the first `:`). |

**No secret is ever in this request.** The worker resolves any credentials it needs
itself, inside its own trust zone. If the broker is configured with a
broker→worker bearer token (`TESSERA_LIVEVIEW_WORKER_TOKEN`), it arrives as
`Authorization: Bearer <token>` — the worker SHOULD require it and MUST never log it.

---

## 3. Response — worker → broker

On success: `200 OK`, body `application/json`:

```json
{
  "liveViewUrl": "https://worker.internal/novnc/session/abc123?token=…",
  "targetHostname": "www.health-portal.example",
  "ttlSeconds": 300,
  "readWrite": true,
  "faviconUrl": "https://worker.internal/favicons/health-portal.png"
}
```

| Field | Type | Required | Meaning |
| --- | --- | --- | --- |
| `liveViewUrl` | string | **yes** | The embeddable, **short-TTL, single-use** remote-browser URL the portal iframes. Treat it like a capability: single-use, expiring, never logged. |
| `targetHostname` | string | **yes** | The hostname the worker actually navigated to — the portal's anti-phishing target strip. Server-verified by the worker, never client-supplied. |
| `ttlSeconds` | int | no | The session lifetime the worker grants. Omit/null → the broker applies `liveView.defaultTtlSeconds` (default **300**). |
| `readWrite` | bool | no | `true` (default) when the human may drive the session (login/captcha); `false` for a view-only stage. |
| `faviconUrl` | string | no | An optional worker-vouched favicon for the target strip. (The portal does **not** render a remote favicon today — anti-spoofing — but the field is contract-stable.) |

The broker stamps the absolute `expiresAt` itself (`now + ttl`) and surfaces it to
the portal for the visible countdown — so a clock skew on the worker cannot extend
the handle.

---

## 4. Fail-closed contract (the security spine)

`HttpLiveViewWorker` is hardened like the egress transport — **no proxy, no
redirects, no ambient cookies**, 10 s connect / 20 s overall timeout — and is
fail-closed by construction. The worker MUST rely on this: anything other than a
clean `2xx` with a parseable body carrying a non-empty `liveViewUrl` **and**
`targetHostname` becomes a secret-free `Unavailable` result in the portal (HTTP
`503`), never a faked or half-armed session. Specifically the broker treats each of
these as fail-closed:

- any non-2xx status (`4xx` slot-busy / unmapped / unauthorized, `5xx` worker error);
- a transport error (worker unreachable) or a timeout;
- an unparseable / non-JSON body;
- a body missing `liveViewUrl` or `targetHostname`.

So the worker should return a plain `409`/`503` when a slot is busy or the
person→slot mapping is absent, and a `401` when the bearer token is missing — the
portal will show a calm "not available right now", not an error.

Worker obligations that the broker cannot enforce (it never sees the browser):

1. **Single-use + short-TTL** `liveViewUrl` — revoke it on first attach or at `ttl`.
2. **Per-principal slot binding** — never hand a session armed for `principal` A to a
   request for principal B (the broker passes the principal precisely so the worker
   can enforce this).
3. **Cookie stays in the worker zone** — harvest worker→vault; never return any
   credential, cookie, or token in the arm response.
4. **`targetHostname` is server-verified** — it is the only anti-phishing anchor the
   human sees; it must reflect the actual navigated origin.

---

## 5. Broker configuration (already built)

`tessera.json` (`LiveViewOptions`):

```json
{
  "liveView": {
    "enabled": true,
    "workerArmUrl": "http://live-view-worker.internal:8080/live-view/arm",
    "defaultTtlSeconds": 300
  }
}
```

- `enabled: false` (default) ⇒ `DisabledLiveViewProvider` — the portal's Live stage
  is fail-closed (503) regardless of any worker. Flipping it on with a real
  `workerArmUrl` is the operator wire-up step (plan §1.4).
- The optional broker→worker token is supplied out-of-band via the
  `TESSERA_LIVEVIEW_WORKER_TOKEN` environment variable (never in the config file,
  never logged).

---

## 6. Reference worker sketch (for the homelab noVNC pool)

A thin service in front of the existing browser pool, satisfying §2–§4:

1. Authenticate the broker (require the bearer token).
2. Look up / claim a free playwright slot and **bind it to `principal`** (reject if
   none free → `409`).
3. Navigate the slot's browser to the `provider`'s login URL; record the resulting
   origin as `targetHostname`.
4. Mint a single-use, short-TTL noVNC URL scoped to that slot (port 6080 today);
   return it as `liveViewUrl`.
5. On the human completing login/captcha, let the existing `sessionkeeper` harvester
   write the bundle worker→vault (unchanged); expire the noVNC URL.

This adds **no** new path for the cookie to leave the pod — it only fronts the
already-internal noVNC with a per-session, identity-bound, short-TTL URL and the arm
contract above. Exposing it (off-pod reachability, activating the staged pool) is the
operator's security call (plan §1.3).
