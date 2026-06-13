"""Tests for the serving plane: dispatch routing, fail-closed broker, self-test."""

from __future__ import annotations

import json

from tessera.broker import Broker
from tessera.config import Config, IdentityConfig
from tessera.model import CallerIdentity, VerificationMethod
from tessera.policy import Grant, PolicyDecisionPoint
from tessera.resolver import CredentialResolver, TargetBinding
from tessera.runtime import build_state
from tessera.serve import ServerState, dispatch, header_authenticator, run_selftest
from tessera.store import InMemoryStore


def _broker(bundle=None):
    grants = [
        Grant(
            caller="spiffe://tessera.local/selftest",
            target="health-portal",
            actions=("read:*",),
            on_behalf_of="bob@example.com",
        ),
        Grant(caller="caller-x", target="marketplace", actions=("read:*",)),
    ]
    store = InMemoryStore({"sess": bundle} if bundle is not None else {})
    bindings = [
        TargetBinding("health-portal", "sess", "bob@example.com"),
        TargetBinding("marketplace", "marketplace-sess"),
    ]
    return Broker(PolicyDecisionPoint(grants), CredentialResolver(bindings, store))


def _state(authenticator=None, bundle=None):
    return ServerState(broker=_broker(bundle), authenticator=authenticator, store_kind="in-memory")


def test_healthz_always_ok() -> None:
    status, body = dispatch("GET", "/healthz", {}, b"", _state())
    assert status == 200 and body["status"] == "ok"


def test_readyz_reflects_state() -> None:
    st = _state()
    assert dispatch("GET", "/readyz", {}, b"", st)[0] == 503
    st.ready = True
    assert dispatch("GET", "/readyz", {}, b"", st)[0] == 200


def test_status_reports_fail_closed_endpoint() -> None:
    status, body = dispatch("GET", "/status", {}, b"", _state())
    assert status == 200
    assert body["broker_endpoint"] == "fail-closed"


def test_broker_endpoint_fail_closed_without_authenticator() -> None:
    status, body = dispatch(
        "POST", "/v1/broker", {}, json.dumps({"target": "marketplace", "action": "read:x"}).encode(), _state()
    )
    assert status == 503
    assert "fail-closed" in body["error"]


def test_broker_endpoint_401_when_caller_unidentified() -> None:
    st = _state(authenticator=header_authenticator)
    status, _ = dispatch("POST", "/v1/broker", {}, b"{}", st)
    assert status == 401


def test_broker_endpoint_denies_unverified_header_caller() -> None:
    # Header auth yields a DEV (unverified) identity; the PDP must still deny it.
    st = _state(authenticator=header_authenticator, bundle={"access_token": "AT"})
    body = json.dumps({"target": "marketplace", "action": "read:x"}).encode()
    status, payload = dispatch(
        "POST", "/v1/broker", {"X-Tessera-Caller": "caller-x"}, body, st
    )
    assert status == 403
    assert payload["effect"] == "deny"


def test_dispatch_404_and_400() -> None:
    assert dispatch("GET", "/nope", {}, b"", _state())[0] == 404
    st = _state(authenticator=header_authenticator)
    bad = dispatch("POST", "/v1/broker", {"X-Tessera-Caller": "caller-x"}, b"not json", st)
    assert bad[0] == 400


def test_run_selftest_present_for_principal() -> None:
    broker = _broker(bundle={"access_token": "AT", "refresh_token": "RT"})
    result = run_selftest(broker, "health-portal", "bob@example.com", "tessera.local")
    assert result["effect"] == "allow"
    assert result["credential_status"] == "present"
    assert result["ok"] is True


def test_run_selftest_absent_when_no_bundle() -> None:
    broker = _broker(bundle=None)
    result = run_selftest(broker, "health-portal", "bob@example.com", "tessera.local")
    assert result["effect"] == "allow"
    assert result["credential_status"] == "absent"
    assert result["ok"] is False


def test_build_state_wires_selftest_and_fail_closed(tmp_path) -> None:
    grants = tmp_path / "grants.toml"
    grants.write_text(
        '[[grant]]\ncaller = "spiffe://tessera.local/selftest"\n'
        'on_behalf_of = "bob@example.com"\ntarget = "health-portal"\n'
        'actions = ["read:*"]\n'
        '[[target]]\nname = "health-portal"\n'
        'on_behalf_of = "bob@example.com"\ncredential = "sess"\n',
        encoding="utf-8",
    )
    cfg = Config(identity=IdentityConfig(mode="mtls", trust_domain="tessera.local"))
    store = InMemoryStore({"sess": {"access_token": "AT"}})
    env = {"TESSERA_SELFTEST_TARGET": "health-portal", "TESSERA_SELFTEST_PRINCIPAL": "bob@example.com"}
    state = build_state(cfg, str(grants), environ=env, store=store)
    assert state.ready
    assert state.authenticator is None  # fail-closed by default
    assert state.selftest["credential_status"] == "present"
