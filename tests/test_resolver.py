"""Tests for the resolver: bindings + status assessment (never exposes secrets)."""

from __future__ import annotations

from tessera.model import AccessRequest, CallerIdentity, EndUserAssertion, VerificationMethod
from tessera.resolver import (
    CredentialResolver,
    CredentialStatus,
    TargetBinding,
    bindings_from_dict,
    load_bindings,
)
from tessera.store import InMemoryStore


def _req(target="health-portal", principal="bob@example.com") -> AccessRequest:
    caller = CallerIdentity(id="spiffe://t/app", verified_via=VerificationMethod.SPIFFE_SVID)
    oba = EndUserAssertion(subject=principal, issuer="iss") if principal else None
    return AccessRequest(caller=caller, target=target, action="read:x", on_behalf_of=oba)


def test_binding_matches_target_and_principal() -> None:
    b = TargetBinding(target="health-portal", credential="sess", principal="bob@example.com")
    assert b.matches(_req())
    assert not b.matches(_req(principal="alice@example.com"))
    assert not b.matches(_req(target="other"))


def test_resolve_present_when_bundle_has_tokens() -> None:
    store = InMemoryStore({"sess": {"access_token": "AT", "refresh_token": "RT"}})
    resolver = CredentialResolver(
        [TargetBinding("health-portal", "sess", "bob@example.com")], store
    )
    res = resolver.resolve(_req())
    assert res.status is CredentialStatus.PRESENT
    assert res.usable
    # The detail names *which kinds* are present but never the values.
    assert "access_token" in res.detail
    assert "AT" not in res.detail


def test_resolve_incomplete_when_bundle_empty_of_tokens() -> None:
    store = InMemoryStore({"sess": {"note": "placeholder"}})
    resolver = CredentialResolver([TargetBinding("health-portal", "sess", "bob@example.com")], store)
    res = resolver.resolve(_req())
    assert res.status is CredentialStatus.INCOMPLETE


def test_resolve_absent_without_binding() -> None:
    resolver = CredentialResolver([], InMemoryStore())
    res = resolver.resolve(_req())
    assert res.status is CredentialStatus.ABSENT
    assert not res.usable


def test_resolve_absent_when_binding_but_no_bundle() -> None:
    resolver = CredentialResolver(
        [TargetBinding("health-portal", "sess", "bob@example.com")], InMemoryStore()
    )
    res = resolver.resolve(_req())
    assert res.status is CredentialStatus.ABSENT


def test_resolve_error_surfaces_store_failure() -> None:
    class _Boom(InMemoryStore):
        def get_bundle(self, name):
            from tessera.store import StoreError

            raise StoreError("kv down")

    resolver = CredentialResolver(
        [TargetBinding("health-portal", "sess", "bob@example.com")], _Boom()
    )
    res = resolver.resolve(_req())
    assert res.status is CredentialStatus.ERROR
    assert "kv down" in res.detail


def test_bindings_from_dict_and_load(tmp_path) -> None:
    parsed = bindings_from_dict(
        {"target": [{"name": "health-portal", "credential": "sess", "on_behalf_of": "m@x"}]}
    )
    assert parsed[0].principal == "m@x"

    p = tmp_path / "grants.toml"
    p.write_text(
        '[[target]]\nname = "marketplace"\ncredential = "marketplace-session"\n', encoding="utf-8"
    )
    loaded = load_bindings(p)
    assert loaded[0].target == "marketplace"
    assert loaded[0].principal is None
    assert load_bindings(tmp_path / "absent.toml") == []
