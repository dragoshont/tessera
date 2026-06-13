"""The Policy Decision Point (PDP): fail-closed authorization.

This is the heart of P1. Given an :class:`~tessera.model.AccessRequest`, the PDP
returns a :class:`~tessera.model.Decision`. The rules are deliberately boring and
auditable — this is the security boundary, so it favours clarity over cleverness.

Invariants:

* **Verified callers only.** An unverified caller (dev mode) is denied unless the
  PDP is explicitly constructed with ``allow_unverified=True`` (local dev).
* **Default deny.** If no grant matches, deny.
* **Explicit delegation.** A grant that names ``on_behalf_of`` matches only when
  the request carries that exact end-user; a grant without it matches only pure
  automation (no end-user). A human identity is never silently dropped or added.
* **Least-privilege actions.** Actions match by shell-glob, so ``read:*`` grants
  every read verb but no writes.

The grant model is intentionally the same shape as the per-user MCP gate that
already runs in production — generalized from "which server" to
"(caller, end-user, target, action)".
"""

from __future__ import annotations

import tomllib
from dataclasses import dataclass
from fnmatch import fnmatchcase
from pathlib import Path

from .model import AccessRequest, Decision


@dataclass(frozen=True, slots=True)
class Grant:
    """One authorization rule. See ``grants.example.toml`` for the file form."""

    caller: str
    target: str
    actions: tuple[str, ...]
    on_behalf_of: str | None = None

    def matches(self, request: AccessRequest) -> bool:
        if self.caller != request.caller.id:
            return False
        if self.target != request.target:
            return False
        # Delegation must line up exactly: a grant for a human only applies to a
        # request carrying that human; an automation grant only to no-human calls.
        request_user = request.on_behalf_of.subject if request.on_behalf_of else None
        if self.on_behalf_of != request_user:
            return False
        return any(fnmatchcase(request.action, pat) for pat in self.actions)


class PolicyDecisionPoint:
    """Evaluates requests against a list of :class:`Grant` rules, fail-closed."""

    def __init__(
        self,
        grants: list[Grant] | None = None,
        *,
        allow_unverified: bool = False,
    ) -> None:
        self._grants = list(grants or [])
        self._allow_unverified = allow_unverified

    def evaluate(self, request: AccessRequest) -> Decision:
        # 1) Identity gate — never authorize an unproven caller on the network.
        if not request.caller.is_verified and not self._allow_unverified:
            return Decision.deny(
                f"caller {request.caller.id!r} is not verified "
                f"(via {request.caller.verified_via.value})"
            )
        # An end-user, if present, must also be verified.
        if request.on_behalf_of and not request.on_behalf_of.is_verified:
            return Decision.deny(
                f"end-user {request.on_behalf_of.subject!r} assertion is not verified"
            )

        # 2) Authorization — explicit grant required (default deny).
        for grant in self._grants:
            if grant.matches(request):
                who = (
                    f"{request.caller.id} on behalf of {request.on_behalf_of.subject}"
                    if request.on_behalf_of
                    else request.caller.id
                )
                return Decision.allow(
                    f"granted: {who} may {request.action} on {request.target}"
                )

        return Decision.deny(
            f"no grant allows {request.caller.id} to {request.action} on {request.target}"
        )


def grants_from_dict(data: dict) -> list[Grant]:
    """Build grants from a parsed-TOML dict with a top-level ``grant`` array."""
    out: list[Grant] = []
    for entry in data.get("grant", []):
        out.append(
            Grant(
                caller=entry["caller"],
                target=entry["target"],
                actions=tuple(entry.get("actions", ())),
                on_behalf_of=entry.get("on_behalf_of"),
            )
        )
    return out


def load_grants(path: str | Path) -> list[Grant]:
    """Load grants from a TOML file. A missing file yields no grants (deny-all)."""
    p = Path(path)
    if not p.exists():
        return []
    with p.open("rb") as fh:
        return grants_from_dict(tomllib.load(fh))
