"""The serving plane — an HTTP front door for the broker.

Endpoints:

* ``GET  /healthz`` — liveness (always 200 if the process is up).
* ``GET  /readyz``  — readiness (200 once startup wiring completed).
* ``GET  /status``  — secret-free operational status: store kind, whether the
  network broker endpoint is open or fail-closed, and the startup self-test
  result.
* ``POST /v1/broker`` — the brokering endpoint. **Fails closed**: it returns 503
  unless a caller authenticator is explicitly configured, because exposing an
  unauthenticated path to real credentials is the confused-deputy vulnerability
  this whole project exists to prevent. Even when an authenticator *is* present,
  the Policy Decision Point independently denies any caller it cannot verify.

The request logic lives in the pure :func:`dispatch` function so it can be
unit-tested offline with no sockets; :class:`_Handler` is a thin adapter.
"""

from __future__ import annotations

import json
from dataclasses import dataclass
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Callable

from .model import AccessRequest, CallerIdentity, EndUserAssertion, VerificationMethod
from .broker import Broker

# A caller authenticator turns request headers into a verified CallerIdentity,
# or None if the caller cannot be identified. ``None`` (no authenticator) means
# the broker endpoint is fail-closed.
Authenticator = Callable[[dict[str, str]], CallerIdentity | None]


@dataclass
class ServerState:
    """Mutable, secret-free runtime state shared with the HTTP handler."""

    broker: Broker
    authenticator: Authenticator | None
    store_kind: str
    ready: bool = False
    selftest: dict | None = None


def header_authenticator(headers: dict[str, str]) -> CallerIdentity | None:
    """Dev-only: trust an ``X-Tessera-Caller`` header.

    Returns an **unverified** (DEV) identity, so the PDP will still deny it unless
    explicitly run with ``allow_unverified``. This exists for loopback dev only;
    it is never wired in cluster deployments.
    """
    cid = headers.get("x-tessera-caller")
    if not cid:
        return None
    return CallerIdentity(id=cid, verified_via=VerificationMethod.DEV)


def _json(status: int, body: dict) -> tuple[int, dict]:
    return status, body


def dispatch(
    method: str,
    path: str,
    headers: dict[str, str],
    body: bytes,
    state: ServerState,
) -> tuple[int, dict]:
    """Pure request handler: returns ``(status_code, json_body)``."""
    headers = {k.lower(): v for k, v in headers.items()}
    path = path.split("?", 1)[0].rstrip("/") or "/"

    if method == "GET" and path == "/healthz":
        return _json(200, {"status": "ok"})

    if method == "GET" and path == "/readyz":
        return _json(200 if state.ready else 503, {"ready": state.ready})

    if method == "GET" and path == "/status":
        return _json(
            200,
            {
                "ready": state.ready,
                "store": state.store_kind,
                "broker_endpoint": "enabled"
                if state.authenticator is not None
                else "fail-closed",
                "selftest": state.selftest,
            },
        )

    if method == "POST" and path == "/v1/broker":
        if state.authenticator is None:
            return _json(
                503,
                {
                    "error": "broker endpoint is fail-closed: no caller "
                    "authenticator configured (mTLS/SVID auth plane not enabled)"
                },
            )
        caller = state.authenticator(headers)
        if caller is None:
            return _json(401, {"error": "caller could not be authenticated"})
        try:
            payload = json.loads(body or b"{}")
        except json.JSONDecodeError:
            return _json(400, {"error": "invalid JSON body"})
        target = payload.get("target")
        action = payload.get("action")
        if not target or not action:
            return _json(400, {"error": "body requires 'target' and 'action'"})
        on_behalf_of = None
        if sub := payload.get("on_behalf_of"):
            # In a real auth plane this assertion is itself cryptographically
            # verified; here it rides the (already authenticated) caller channel.
            on_behalf_of = EndUserAssertion(
                subject=sub, issuer=payload.get("issuer", "via-caller")
            )
        request = AccessRequest(
            caller=caller, target=target, action=action, on_behalf_of=on_behalf_of
        )
        result = state.broker.handle(request)
        status = 200 if result.decision.allowed else 403
        return _json(
            status,
            {
                "effect": result.decision.effect.value,
                "reason": result.decision.reason,
                "credential_status": (
                    result.credential.status.value if result.credential else None
                ),
                "ok": result.ok,
            },
        )

    return _json(404, {"error": "not found"})


def run_selftest(
    broker: Broker, target: str, principal: str | None, trust_domain: str
) -> dict:
    """Exercise the authorize+resolve spine against the real store, secret-free.

    Builds an internally-trusted request (the caller is constructed here, not read
    from the network) and reports only the *status* — it makes **no** call to the
    upstream service, so running it against a real account is side-effect-free.
    """
    caller = CallerIdentity(
        id=f"spiffe://{trust_domain}/selftest",
        verified_via=VerificationMethod.SPIFFE_SVID,
        trust_domain=trust_domain,
    )
    on_behalf_of = (
        EndUserAssertion(subject=principal, issuer="tessera-selftest")
        if principal
        else None
    )
    request = AccessRequest(
        caller=caller, target=target, action="read:selftest", on_behalf_of=on_behalf_of
    )
    result = broker.handle(request)
    return {
        "target": target,
        "on_behalf_of": principal,
        "effect": result.decision.effect.value,
        "reason": result.decision.reason,
        "credential_status": (
            result.credential.status.value if result.credential else None
        ),
        "credential_detail": (result.credential.detail if result.credential else None),
        "ok": result.ok,
    }


class _Handler(BaseHTTPRequestHandler):
    server_version = "tessera/0"

    def _state(self) -> ServerState:
        return self.server.state  # type: ignore[attr-defined]

    def _do(self, method: str) -> None:
        length = int(self.headers.get("Content-Length", 0) or 0)
        body = self.rfile.read(length) if length else b""
        headers = {k: v for k, v in self.headers.items()}
        status, payload = dispatch(method, self.path, headers, body, self._state())
        data = json.dumps(payload).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self) -> None:  # noqa: N802 (stdlib naming)
        self._do("GET")

    def do_POST(self) -> None:  # noqa: N802
        self._do("POST")

    def log_message(self, *_args) -> None:  # silence default stderr access log
        pass


def serve_forever(host: str, port: int, state: ServerState) -> None:  # pragma: no cover
    httpd = ThreadingHTTPServer((host, port), _Handler)
    httpd.state = state  # type: ignore[attr-defined]
    httpd.serve_forever()
