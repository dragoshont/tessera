"""Tests for the fail-closed Policy Decision Point."""

from __future__ import annotations

from tessera.model import (
    AccessRequest,
    CallerIdentity,
    EndUserAssertion,
    Effect,
    VerificationMethod,
)
from tessera.policy import Grant, PolicyDecisionPoint, grants_from_dict, load_grants


def _caller(cid: str = "spiffe://tessera.local/app") -> CallerIdentity:
    return CallerIdentity(id=cid, verified_via=VerificationMethod.SPIFFE_SVID)


def _user(sub: str = "alice@example.com") -> EndUserAssertion:
    return EndUserAssertion(subject=sub, issuer="https://accounts.google.com")


def test_default_deny_when_no_grants() -> None:
    pdp = PolicyDecisionPoint([])
    req = AccessRequest(caller=_caller(), target="health-portal", action="read:record")
    decision = pdp.evaluate(req)
    assert decision.effect is Effect.DENY
    assert "no grant" in decision.reason


def test_unverified_caller_is_denied() -> None:
    pdp = PolicyDecisionPoint(
        [Grant(caller="x", target="t", actions=("read:*",))]
    )
    caller = CallerIdentity(id="x", verified_via=VerificationMethod.DEV)
    req = AccessRequest(caller=caller, target="t", action="read:a")
    assert pdp.evaluate(req).effect is Effect.DENY


def test_unverified_caller_allowed_in_dev_pdp() -> None:
    pdp = PolicyDecisionPoint(
        [Grant(caller="x", target="t", actions=("read:*",))],
        allow_unverified=True,
    )
    caller = CallerIdentity(id="x", verified_via=VerificationMethod.DEV)
    req = AccessRequest(caller=caller, target="t", action="read:a")
    assert pdp.evaluate(req).allowed


def test_action_glob_allows_reads_but_not_writes() -> None:
    pdp = PolicyDecisionPoint(
        [Grant(caller="spiffe://tessera.local/app", target="marketplace", actions=("read:*",))]
    )
    ok = AccessRequest(caller=_caller(), target="marketplace", action="read:listings")
    no = AccessRequest(caller=_caller(), target="marketplace", action="write:order")
    assert pdp.evaluate(ok).allowed
    assert not pdp.evaluate(no).allowed


def test_delegation_must_match_exactly() -> None:
    grant = Grant(
        caller="spiffe://tessera.local/app",
        target="health-portal",
        actions=("read:*",),
        on_behalf_of="alice@example.com",
    )
    pdp = PolicyDecisionPoint([grant])

    # Right human -> allow.
    allow = AccessRequest(
        caller=_caller(), target="health-portal", action="read:x", on_behalf_of=_user()
    )
    assert pdp.evaluate(allow).allowed

    # Wrong human -> deny.
    wrong = AccessRequest(
        caller=_caller(),
        target="health-portal",
        action="read:x",
        on_behalf_of=_user("bob@example.com"),
    )
    assert not pdp.evaluate(wrong).allowed

    # No human at all -> deny (a delegated grant never applies to automation).
    none = AccessRequest(caller=_caller(), target="health-portal", action="read:x")
    assert not pdp.evaluate(none).allowed


def test_automation_grant_does_not_match_delegated_request() -> None:
    grant = Grant(caller="spiffe://tessera.local/cron", target="marketplace", actions=("read:*",))
    pdp = PolicyDecisionPoint([grant])
    cron = CallerIdentity(
        id="spiffe://tessera.local/cron", verified_via=VerificationMethod.SPIFFE_SVID
    )
    delegated = AccessRequest(
        caller=cron, target="marketplace", action="read:a", on_behalf_of=_user()
    )
    # Automation grant has on_behalf_of=None, so a delegated call must NOT match.
    assert not pdp.evaluate(delegated).allowed


def test_grants_from_dict_and_load(tmp_path) -> None:
    p = tmp_path / "grants.toml"
    p.write_text(
        '[[grant]]\ncaller = "spiffe://tessera.local/app"\n'
        'target = "marketplace"\nactions = ["read:*"]\n',
        encoding="utf-8",
    )
    grants = load_grants(p)
    assert len(grants) == 1
    assert grants[0].caller == "spiffe://tessera.local/app"

    parsed = grants_from_dict({"grant": [{"caller": "c", "target": "t", "actions": ["read:x"]}]})
    assert parsed[0].on_behalf_of is None


def test_load_grants_missing_file_is_deny_all(tmp_path) -> None:
    assert load_grants(tmp_path / "absent.toml") == []
