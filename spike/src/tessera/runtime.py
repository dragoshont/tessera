"""Assemble a running broker from configuration + environment.

This is the one place that wires the pieces together for the ``serve`` command:
config → grants + bindings → credential store → PDP → resolver → audit → broker,
plus the optional startup self-test. Keeping it here keeps ``__main__`` thin and
makes the assembly itself testable.
"""

from __future__ import annotations

import os

from .audit import AuditSink
from .broker import Broker
from .config import Config
from .policy import PolicyDecisionPoint, load_grants
from .resolver import CredentialResolver, load_bindings
from .serve import ServerState, header_authenticator, run_selftest
from .store import CredentialStore, InMemoryStore, azure_store_from_env


def build_store(environ: dict[str, str] | None = None) -> tuple[CredentialStore, str]:
    """Return ``(store, kind)``. Azure KV if configured, else an empty in-memory store."""
    env = environ if environ is not None else os.environ
    azure = azure_store_from_env(env)
    if azure is not None:
        return azure, "azure-key-vault"
    return InMemoryStore(), "in-memory (no Azure env; resolution will be 'absent')"


def build_state(
    cfg: Config,
    grants_path: str,
    *,
    environ: dict[str, str] | None = None,
    store: CredentialStore | None = None,
) -> ServerState:
    """Build the full :class:`ServerState` for serving."""
    env = environ if environ is not None else os.environ

    grants = load_grants(grants_path)
    bindings = load_bindings(grants_path)

    store_kind = "injected"
    if store is None:
        store, store_kind = build_store(env)

    pdp = PolicyDecisionPoint(grants, allow_unverified=(cfg.identity.mode == "dev"))
    resolver = CredentialResolver(bindings, store)
    audit = AuditSink.open(cfg.audit.path) if cfg.audit.enabled else None
    broker = Broker(pdp, resolver, audit)

    # The network broker endpoint is fail-closed unless dev header auth is opted
    # in *and* the server is on loopback (an unverified header is never trusted
    # on the network).
    authenticator = None
    if env.get("TESSERA_DEV_HEADER_AUTH") == "1" and cfg.server.is_loopback:
        authenticator = header_authenticator

    state = ServerState(
        broker=broker, authenticator=authenticator, store_kind=store_kind
    )

    target = env.get("TESSERA_SELFTEST_TARGET")
    if target:
        principal = env.get("TESSERA_SELFTEST_PRINCIPAL") or None
        state.selftest = run_selftest(
            broker, target, principal, cfg.identity.trust_domain
        )

    state.ready = True
    return state
