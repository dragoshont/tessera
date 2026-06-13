"""Credential resolution — *which* stored bundle backs a (target, principal).

A :class:`TargetBinding` says: for this ``target`` (e.g. ``health-portal``) acting
for this ``principal`` (e.g. ``bob@example.com``), the credential lives in
store secret ``credential`` (e.g. ``health-portal-session``). A binding
with ``principal = None`` backs pure-automation access to the target.

The resolver fetches that bundle and returns a :class:`ResolvedCredential` whose
**status is all a caller ever learns** — ``present`` / ``incomplete`` / ``absent``
/ ``error``. The secret bytes stay inside the resolver; they are never returned
across the broker boundary, logged, or audited. This is the secretless contract:
"applications cannot leak what they don't have."
"""

from __future__ import annotations

import enum
from dataclasses import dataclass

from .model import AccessRequest
from .store import CredentialStore, StoreError


@dataclass(frozen=True, slots=True)
class TargetBinding:
    """Maps a (target, principal) to the store secret that holds its bundle."""

    target: str
    credential: str
    principal: str | None = None

    def matches(self, request: AccessRequest) -> bool:
        if self.target != request.target:
            return False
        req_principal = request.on_behalf_of.subject if request.on_behalf_of else None
        return self.principal == req_principal


class CredentialStatus(enum.Enum):
    PRESENT = "present"        # bundle has a usable access/refresh token
    INCOMPLETE = "incomplete"  # bundle exists but is missing tokens
    ABSENT = "absent"          # no bundle / no binding
    ERROR = "error"            # the store could not be read


@dataclass(frozen=True, slots=True)
class ResolvedCredential:
    """The *result* of resolution. Deliberately carries no secret material."""

    target: str
    status: CredentialStatus
    detail: str = ""

    @property
    def usable(self) -> bool:
        return self.status is CredentialStatus.PRESENT


def _assess(bundle: dict) -> tuple[CredentialStatus, str]:
    if not bundle:
        return CredentialStatus.ABSENT, "no bundle in store"
    has_access = bool(bundle.get("access_token"))
    has_refresh = bool(bundle.get("refresh_token"))
    has_cookies = bool(bundle.get("cookies"))
    if has_access or has_refresh or has_cookies:
        # We report *that* material is present, never *what* it is.
        kinds = [
            n
            for n, ok in (
                ("access_token", has_access),
                ("refresh_token", has_refresh),
                ("cookies", has_cookies),
            )
            if ok
        ]
        return CredentialStatus.PRESENT, "has " + ", ".join(kinds)
    return CredentialStatus.INCOMPLETE, "bundle present but no tokens/cookies"


class CredentialResolver:
    """Resolves a request to a :class:`ResolvedCredential` using bindings + a store."""

    def __init__(self, bindings: list[TargetBinding], store: CredentialStore) -> None:
        self._bindings = list(bindings)
        self._store = store

    def binding_for(self, request: AccessRequest) -> TargetBinding | None:
        for b in self._bindings:
            if b.matches(request):
                return b
        return None

    def resolve(self, request: AccessRequest) -> ResolvedCredential:
        binding = self.binding_for(request)
        if binding is None:
            return ResolvedCredential(
                request.target, CredentialStatus.ABSENT, "no target binding"
            )
        try:
            bundle = self._store.get_bundle(binding.credential)
        except StoreError as exc:
            return ResolvedCredential(request.target, CredentialStatus.ERROR, str(exc))
        status, detail = _assess(bundle)
        return ResolvedCredential(request.target, status, detail)


def bindings_from_dict(data: dict) -> list[TargetBinding]:
    """Build bindings from a parsed-TOML dict with a top-level ``target`` array."""
    out: list[TargetBinding] = []
    for entry in data.get("target", []):
        out.append(
            TargetBinding(
                target=entry["name"],
                credential=entry["credential"],
                principal=entry.get("on_behalf_of"),
            )
        )
    return out


def load_bindings(path) -> list[TargetBinding]:
    """Load target bindings from a TOML file (the ``[[target]]`` array).

    Bindings live alongside grants in the same file. A missing file yields no
    bindings, so every resolution returns ``absent`` (fail-closed).
    """
    import tomllib
    from pathlib import Path

    p = Path(path)
    if not p.exists():
        return []
    with p.open("rb") as fh:
        return bindings_from_dict(tomllib.load(fh))
