"""Credential stores — where Tessera reads the secret it will inject.

A store maps a *name* to a JSON **bundle** (the shape the harvester writes and
the per-provider MCPs read):

    {"access_token": ..., "refresh_token": ..., "cookies": {...}, ...}

Tessera only ever *reads* from the store; it never logs or returns the bundle to
a caller. Two implementations:

* :class:`AzureKeyVaultStore` — the production store, a dependency-free Azure Key
  Vault REST client (Service-Principal client-credentials → token → GET secret).
  It is byte-for-byte the same contract the harvester's KV client uses, so
  Tessera reads exactly the bundles the harvester already maintains.
* :class:`InMemoryStore` — for tests and local dev; no network.

The HTTP transport is injectable (``opener``) so the whole surface is unit-tested
offline with no Azure and no network.
"""

from __future__ import annotations

import abc
import json
import time
import urllib.error
import urllib.parse
import urllib.request

_KV_API = "7.4"
_KV_SCOPE = "https://vault.azure.net/.default"


class StoreError(Exception):
    """Raised when a credential store cannot be reached or read."""


class CredentialStore(abc.ABC):
    """Read-only access to credential bundles by name."""

    @abc.abstractmethod
    def get_bundle(self, name: str) -> dict:
        """Return the JSON bundle stored under ``name`` (``{}`` if missing/empty)."""


class InMemoryStore(CredentialStore):
    """A dict-backed store for tests and offline dev."""

    def __init__(self, bundles: dict[str, dict] | None = None) -> None:
        self._bundles = dict(bundles or {})

    def put(self, name: str, bundle: dict) -> None:
        self._bundles[name] = bundle

    def get_bundle(self, name: str) -> dict:
        return dict(self._bundles.get(name, {}))


class _SpToken:
    """Azure SP client-credentials token, cached until ~60s before expiry."""

    def __init__(
        self,
        tenant: str,
        client_id: str,
        client_secret: str,
        *,
        opener=urllib.request.urlopen,
    ) -> None:
        self._tenant = tenant
        self._client_id = client_id
        self._client_secret = client_secret
        self._opener = opener
        self._tok = ""
        self._exp = 0.0

    def get(self) -> str:
        if self._tok and time.time() < self._exp - 60:
            return self._tok
        url = f"https://login.microsoftonline.com/{self._tenant}/oauth2/v2.0/token"
        body = urllib.parse.urlencode(
            {
                "grant_type": "client_credentials",
                "client_id": self._client_id,
                "client_secret": self._client_secret,
                "scope": _KV_SCOPE,
            }
        ).encode()
        req = urllib.request.Request(
            url,
            data=body,
            method="POST",
            headers={"Content-Type": "application/x-www-form-urlencoded"},
        )
        try:
            with self._opener(req, timeout=30) as resp:
                data = json.loads(resp.read().decode("utf-8", "replace"))
        except urllib.error.HTTPError as e:  # pragma: no cover - network
            raise StoreError(f"AAD token HTTP {e.code}") from e
        except urllib.error.URLError as e:  # pragma: no cover - network
            raise StoreError(f"AAD token unreachable: {e.reason}") from e
        self._tok = data["access_token"]
        self._exp = time.time() + float(data.get("expires_in", 3600))
        return self._tok


class AzureKeyVaultStore(CredentialStore):
    """Read credential bundles from Azure Key Vault (read-only)."""

    def __init__(
        self,
        vault_url: str,
        token: _SpToken,
        *,
        opener=urllib.request.urlopen,
    ) -> None:
        self._base = vault_url.rstrip("/")
        self._token = token
        self._opener = opener

    def get_bundle(self, name: str) -> dict:
        url = f"{self._base}/secrets/{urllib.parse.quote(name)}?api-version={_KV_API}"
        req = urllib.request.Request(
            url,
            headers={"Authorization": f"Bearer {self._token.get()}"},
            method="GET",
        )
        try:
            with self._opener(req, timeout=30) as resp:
                data = json.loads(resp.read().decode("utf-8", "replace"))
        except urllib.error.HTTPError as e:  # pragma: no cover - network
            raise StoreError(f"KV GET {name} HTTP {e.code}") from e
        except urllib.error.URLError as e:  # pragma: no cover - network
            raise StoreError(f"KV unreachable: {e.reason}") from e
        raw = data.get("value", "") if isinstance(data, dict) else ""
        if not raw:
            return {}
        try:
            return json.loads(raw)
        except json.JSONDecodeError:
            return {}


def azure_store_from_env(environ: dict[str, str]) -> AzureKeyVaultStore | None:
    """Build an :class:`AzureKeyVaultStore` from env, or ``None`` if unconfigured.

    Required keys (the same the harvester + MCPs use):
    ``AZURE_TENANT_ID``, ``AZURE_CLIENT_ID``, ``AZURE_CLIENT_SECRET``,
    ``TESSERA_VAULT_URL``.
    """
    tenant = environ.get("AZURE_TENANT_ID")
    client_id = environ.get("AZURE_CLIENT_ID")
    client_secret = environ.get("AZURE_CLIENT_SECRET")
    vault_url = environ.get("TESSERA_VAULT_URL")
    if not all((tenant, client_id, client_secret, vault_url)):
        return None
    return AzureKeyVaultStore(vault_url, _SpToken(tenant, client_id, client_secret))
