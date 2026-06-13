"""Tests for the core identity & decision dataclasses."""

from __future__ import annotations

from tessera.model import (
    CallerIdentity,
    Decision,
    Effect,
    EndUserAssertion,
    VerificationMethod,
)


def test_verification_method_is_verified() -> None:
    assert VerificationMethod.MTLS.is_verified
    assert VerificationMethod.SPIFFE_SVID.is_verified
    assert VerificationMethod.OIDC_JWT.is_verified
    assert not VerificationMethod.DEV.is_verified


def test_caller_identity_verified_flag() -> None:
    verified = CallerIdentity(id="a", verified_via=VerificationMethod.MTLS)
    dev = CallerIdentity(id="b", verified_via=VerificationMethod.DEV)
    assert verified.is_verified
    assert not dev.is_verified


def test_end_user_defaults_to_oidc_verified() -> None:
    u = EndUserAssertion(subject="x@y.z", issuer="https://issuer")
    assert u.is_verified


def test_decision_helpers() -> None:
    allow = Decision.allow("ok")
    deny = Decision.deny("nope")
    assert allow.allowed and allow.effect is Effect.ALLOW
    assert not deny.allowed and deny.effect is Effect.DENY
    assert allow.obligations == {}


def test_dataclasses_are_frozen() -> None:
    c = CallerIdentity(id="a", verified_via=VerificationMethod.MTLS)
    try:
        c.id = "b"  # type: ignore[misc]
    except AttributeError:
        pass
    else:  # pragma: no cover
        raise AssertionError("CallerIdentity should be immutable")
