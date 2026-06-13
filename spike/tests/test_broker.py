"""Tests for the broker core and the audit sink (secret-free by construction)."""

from __future__ import annotations

import io
import json

from tessera.audit import AuditSink
from tessera.broker import Broker
from tessera.model import AccessRequest, CallerIdentity, EndUserAssertion, VerificationMethod
from tessera.policy import Grant, PolicyDecisionPoint
from tessera.resolver import CredentialResolver, TargetBinding
from tessera.store import InMemoryStore


def _caller(verified=True):
    return CallerIdentity(
        id="spiffe://t/app",
        verified_via=VerificationMethod.SPIFFE_SVID if verified else VerificationMethod.DEV,
    )


def _portal_broker(audit=None, bundle=None):
    grants = [
        Grant(
            caller="spiffe://t/app",
            target="health-portal",
            actions=("read:*",),
            on_behalf_of="bob@example.com",
        )
    ]
    store = InMemoryStore({"sess": bundle} if bundle is not None else {})
    bindings = [TargetBinding("health-portal", "sess", "bob@example.com")]
    return Broker(PolicyDecisionPoint(grants), CredentialResolver(bindings, store), audit)


def _req():
    return AccessRequest(
        caller=_caller(),
        target="health-portal",
        action="read:record",
        on_behalf_of=EndUserAssertion(subject="bob@example.com", issuer="iss"),
    )


def test_broker_allow_and_resolve_present() -> None:
    broker = _portal_broker(bundle={"access_token": "AT", "refresh_token": "RT"})
    result = broker.handle(_req())
    assert result.decision.allowed
    assert result.ok
    assert result.credential.status.value == "present"


def test_broker_denied_does_not_resolve() -> None:
    # No grant for this action target combo -> deny, and credential stays None.
    broker = _portal_broker(bundle={"access_token": "AT"})
    bad = AccessRequest(
        caller=_caller(),
        target="health-portal",
        action="write:delete",  # only read:* is granted
        on_behalf_of=EndUserAssertion(subject="bob@example.com", issuer="iss"),
    )
    result = broker.handle(bad)
    assert not result.decision.allowed
    assert result.credential is None
    assert not result.ok


def test_broker_allow_but_absent_credential_is_not_ok() -> None:
    broker = _portal_broker(bundle=None)  # binding exists, store empty
    result = broker.handle(_req())
    assert result.decision.allowed
    assert result.credential.status.value == "absent"
    assert not result.ok


def test_audit_writes_one_secret_free_line() -> None:
    buf = io.StringIO()
    audit = AuditSink(buf)
    broker = _portal_broker(audit=audit, bundle={"access_token": "SUPERSECRET"})
    broker.handle(_req())
    line = buf.getvalue().strip()
    entry = json.loads(line)
    assert entry["caller"] == "spiffe://t/app"
    assert entry["on_behalf_of"] == "bob@example.com"
    assert entry["effect"] == "allow"
    assert entry["credential_status"] == "present"
    # The secret must never appear anywhere in the audit line.
    assert "SUPERSECRET" not in line


def test_audit_open_stdout_for_dash() -> None:
    sink = AuditSink.open("-")
    assert sink._stream is not None  # stdout, no file created
