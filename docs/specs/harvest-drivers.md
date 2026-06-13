# Spec — Harvest drivers & workers

> Status: **draft** (design phase). See [ADR 0002](../adr/0002-broker-worker-topology.md)
> (broker + worker topology) and [ADR 0006](../adr/0006-harvest-drivers.md)
> (pluggable drivers).

A **harvest worker** is a data-plane process that obtains and keeps warm the
sessions for providers with no machine credential. A **harvest driver** is the
pluggable unit *inside* a worker that knows how to drive one kind of software
(browser, Android emulator, desktop app). Workers and the broker meet only at the
**credential store**.

## Driver contract

Every driver implements the same contract, regardless of what it drives:

| Method | Purpose | Notes |
|---|---|---|
| `probe(session)` | cheap health check → `healthy` / `stale` / `dead` | read-only; no login |
| `refresh(session)` | silent refresh of an existing session | provider-specific; may be a no-op |
| `login(recipe)` | drive the real software to (re)authenticate, harvest a fresh bundle | expensive; rate-limited by a circuit breaker |
| `act(scoped)` *(optional)* | for `driven` providers, perform the upstream action using the warm session | the session never leaves the worker |

A driver writes the resulting **session bundle** to the store
(`{access_token?, refresh_token?, cookies?, …}`). The broker reads it later and
**never knows which driver produced it**.

## Drivers

| Driver | Status | Drives | For |
|---|---|---|---|
| `browser` | now | headful Chromium via Playwright / CDP | un-API'd web portals |
| `android` | future | Android emulator via ADB + UI automation | app-only / cert-pinned providers |
| `desktop` | future | a desktop application | same, where only a desktop app exists |

The first `browser` driver is the existing Python
[`sessionkeeper`](https://github.com/dragoshont/sessionkeeper) engine (scheduler +
circuit breaker + CDP harvest). A worker wraps it; the broker stays browserless.

## Worker registration & routing

A worker advertises **capabilities** and registers with the broker over **mTLS**.
The broker routes a harvest/act job to a worker that has the needed capability —
the Selenium-Grid model, with go-plugin-style out-of-process isolation
([ADR 0002](../adr/0002-broker-worker-topology.md)).

```mermaid
sequenceDiagram
    participant W as Harvest Worker
    participant B as Broker (router)
    participant S as Store
    W->>B: register {capabilities: [browser:chromium, android:emulator?]} (mTLS)
    B-->>W: ack (worker tracked)
    Note over B: later — a recipe needs driver=browser
    B->>W: dispatch login(recipe) / act(scoped)  (by capability)
    W->>S: write warm session bundle
    W-->>B: done (status only; no secret to the broker)
```

### Topology (same contract, three shapes)

- **Co-located** — an in-process driver inside the broker (batteries-included; one
  container). The "registration" is local.
- **Separate deployment** — `tessera-browser` / `tessera-android` as their own pods
  that register over mTLS. Scale or isolate a driver without touching the broker.
- **Mixed** — co-locate the browser driver, run Android as a separate farm. The
  client never sees the difference (single broker endpoint).

## Isolation requirements (non-negotiable)

- A worker runs in its **own process / trust zone**, never sharing the broker's
  memory. A worker crash or compromise must not reach the security boundary
  (go-plugin's core guarantee).
- Worker ⇄ broker is **mTLS**; workers are sandboxed (seccomp/gVisor/Firecracker as
  appropriate) and pinned by image digest.
- Seed credentials a driver needs to log in are read **just-in-time** from the
  store and never persisted in the worker.
- A `login()` storm is bounded by a **circuit breaker** (`min_seconds_between_logins`,
  `max_logins_per_day`) so re-login can't trip bot-detection / lock the account.

## Open questions

- Registration/transport: gRPC bidi streaming (typed, go-plugin-like) vs HTTP/2
  capability registration (Selenium-like). Leaning gRPC.
- `act()` channel for `driven` providers: how to give the worker a scoped, expiring
  instruction without the broker ever touching the cookie.
- Android driver: emulator vs device farm; attestation/anti-bot arms race; whether
  to support per-tenant device profiles.
