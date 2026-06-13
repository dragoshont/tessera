# ADR 0007 — Worker transport: gRPC + mTLS

- **Status:** Accepted (2026-06-13) · **deferred past iteration 1** (review H2)
- **Deciders:** maintainer (Dragoș)
- **Relates to:** [ADR 0002](0002-broker-worker-topology.md) (broker + worker
  topology), [ADR 0005](0005-identity-first-fail-closed.md) (verified identity)

> **Iteration-1 status.** No live harvest worker ships in iteration 1. The
> HTTP-injectable path (cookies / OAuth tokens) is served by the broker reading a
> warm bundle from the store and injecting it directly — no broker⇄worker RPC is
> needed. gRPC + mTLS (this ADR) is the transport for the *drive-only* `act()`
> egress (cert-pinned apps) and the separate-deployment worker topology, both of
> which are built when a target requires them. The decision below stands; only its
> scheduling moved.

## Context

[ADR 0002](0002-broker-worker-topology.md) established the control-plane / data-plane
split: the broker routes harvest/act jobs to **harvest workers** that register
their **capabilities**, and a worker may be co-located or a separate deployment
(`tessera-browser`, `tessera-android`). That ADR deliberately left the **wire
protocol** between broker and worker open. Two precedents were on the table:

- **Selenium-Grid style** — workers register and receive work over HTTP/1.1/HTTP/2
  with JSON; the Distributor tracks node capabilities.
- **HashiCorp go-plugin style** — out-of-process drivers communicate over
  **gRPC** (HTTP/2), with strongly-typed service contracts and mTLS, used by
  Vault/Boundary/Terraform.

The broker is a security boundary that dispatches privileged, scoped instructions
(`login`, `act`) and receives status — never the secret. The transport must be
typed, mutually authenticated, support **bidirectional streaming** (long-lived
worker registration + push of jobs + progress/heartbeats), and keep the two
trust zones cleanly separated.

## Decision

Use **gRPC over HTTP/2, secured with mTLS**, for all broker ⇄ worker
communication.

- **Service contracts are `.proto`-defined** — typed `Register`, `Dispatch`,
  `Heartbeat`, and result messages. Strong typing matches the auditability goal
  ([ADR 0001](0001-language-and-runtime.md)); .NET has first-class gRPC support.
- **mTLS on every connection.** The worker presents a workload certificate
  (SPIFFE X.509-SVID / client cert); the broker validates it against the trusted
  root and treats the worker as a first-class verified identity
  ([ADR 0005](0005-identity-first-fail-closed.md)). The worker likewise validates
  the broker. No unauthenticated worker is ever routed work.
- **Bidirectional streaming** for registration: a worker opens a stream,
  advertises capabilities, and stays attached to receive dispatched jobs and emit
  heartbeats/progress — the broker never has to reach back out to an unknown
  address.
- **Co-located workers** use the same gRPC contract over a **local channel**
  (Unix domain socket / in-memory), so the in-process and out-of-process paths
  are code-identical from the broker's view — preserving the "seamless either
  way" requirement.
- **Scoped, expiring instructions.** A dispatched `act`/`login` carries only the
  minimum scope and a short TTL; the worker, not the broker, holds the warm
  session, so the secret never crosses into the broker
  ([ADR 0002](0002-broker-worker-topology.md)).

## Consequences

- **Positive:** typed contracts (easy to evolve with protocol versioning),
  built-in bidi streaming for registration/heartbeats, mTLS-native, first-class in
  .NET, and the same contract serves co-located and separate-deployment workers.
- **Positive:** matches the go-plugin isolation model the topology ADR is built on,
  so a worker remains a separate trust zone reachable only over an authenticated
  channel.
- **Negative:** gRPC/HTTP/2 is heavier to debug than plain JSON-over-HTTP, and
  `.proto` tooling is an added build dependency.
- **Mitigation:** keep the surface small (a handful of RPCs); expose a thin
  human-debuggable status endpoint on the broker (not the worker channel); version
  the protocol from day one.

## Rejected alternatives

- **Selenium-style JSON over HTTP** — rejected: untyped, weaker streaming story for
  long-lived registration + heartbeats, and no native mTLS-identity ergonomics. We
  keep Selenium Grid's *capability-routing model* (from ADR 0002) but not its
  transport.
- **A message broker (NATS/Redis) between broker and workers** — rejected for now:
  adds an external dependency and an extra trust hop for a point-to-point,
  security-sensitive channel. Revisit only if worker fan-out demands it.
