"""Tests for credential stores (Azure KV client via an injected fake opener)."""

from __future__ import annotations

import io
import json

import pytest

from tessera.store import (
    AzureKeyVaultStore,
    InMemoryStore,
    StoreError,
    _SpToken,
    azure_store_from_env,
)


def test_in_memory_store_roundtrip() -> None:
    s = InMemoryStore({"a": {"access_token": "x"}})
    assert s.get_bundle("a") == {"access_token": "x"}
    assert s.get_bundle("missing") == {}
    s.put("b", {"refresh_token": "y"})
    assert s.get_bundle("b")["refresh_token"] == "y"


class _FakeResp(io.BytesIO):
    def __enter__(self):
        return self

    def __exit__(self, *a):
        self.close()
        return False


def _opener_for(mapping: dict[str, dict]):
    """Return a fake urlopen that serves AAD tokens and KV secret GETs."""

    def _open(req, timeout=0):
        url = req.full_url
        if "login.microsoftonline.com" in url:
            return _FakeResp(json.dumps({"access_token": "aad-tok", "expires_in": 3600}).encode())
        # KV secret GET: last path segment before '?' is the secret name.
        name = url.split("/secrets/")[1].split("?")[0]
        if name not in mapping:
            import urllib.error

            raise urllib.error.HTTPError(url, 404, "Not Found", {}, None)
        return _FakeResp(json.dumps({"value": json.dumps(mapping[name])}).encode())

    return _open


def test_azure_kv_get_bundle_happy_path() -> None:
    opener = _opener_for({"sess": {"access_token": "AT", "refresh_token": "RT"}})
    token = _SpToken("tenant", "cid", "secret", opener=opener)
    store = AzureKeyVaultStore("https://kv.vault.azure.net", token, opener=opener)
    bundle = store.get_bundle("sess")
    assert bundle == {"access_token": "AT", "refresh_token": "RT"}


def test_azure_kv_missing_secret_raises_store_error() -> None:
    opener = _opener_for({})
    token = _SpToken("tenant", "cid", "secret", opener=opener)
    store = AzureKeyVaultStore("https://kv.vault.azure.net", token, opener=opener)
    with pytest.raises(StoreError):
        store.get_bundle("nope")


def test_sp_token_is_cached() -> None:
    calls = {"n": 0}

    def _open(req, timeout=0):
        if "login.microsoftonline.com" in req.full_url:
            calls["n"] += 1
            return _FakeResp(json.dumps({"access_token": "t", "expires_in": 3600}).encode())
        raise AssertionError("unexpected URL")

    tok = _SpToken("t", "c", "s", opener=_open)
    assert tok.get() == "t"
    assert tok.get() == "t"
    assert calls["n"] == 1  # second call served from cache


def test_azure_store_from_env_requires_all_keys() -> None:
    assert azure_store_from_env({}) is None
    assert (
        azure_store_from_env(
            {
                "AZURE_TENANT_ID": "t",
                "AZURE_CLIENT_ID": "c",
                "AZURE_CLIENT_SECRET": "s",
                # missing TESSERA_VAULT_URL
            }
        )
        is None
    )
    store = azure_store_from_env(
        {
            "AZURE_TENANT_ID": "t",
            "AZURE_CLIENT_ID": "c",
            "AZURE_CLIENT_SECRET": "s",
            "TESSERA_VAULT_URL": "https://kv.vault.azure.net",
        }
    )
    assert isinstance(store, AzureKeyVaultStore)
