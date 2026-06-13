"""Core identity & decision vocabulary.

These small, immutable dataclasses are the nouns the rest of Tessera speaks in.
Two identities are always distinguished, because conflating them is the root of
the *confused-deputy* class of bugs:

* **CallerIdentity** — *who* is calling: a workload / non-human identity (an
  agent process, an n8n worker, a crawler, a CI job). Proven by mTLS / SPIFFE
  SVID or a signed workload JWT.
* **EndUserAssertion** — *for whom* the call is made: an optional human on whose
  behalf the caller acts. Proven by a signed OIDC/JWT assertion. Absent for pure
  automation, which acts only as itself.

An :class:`AccessRequest` binds them to a target + action; the policy engine
returns a :class:`Decision`.
"""

from __future__ import annotations

import enum
from dataclasses import dataclass, field


class VerificationMethod(enum.Enum):
    """How an identity was established. ``DEV`` is unverified (local dev only)."""

    MTLS = "mtls"
    SPIFFE_SVID = "spiffe-svid"
    OIDC_JWT = "oidc-jwt"
    DEV = "dev"

    @property
    def is_verified(self) -> bool:
        """True if this method cryptographically proves the identity."""
        return self is not VerificationMethod.DEV


@dataclass(frozen=True, slots=True)
class CallerIdentity:
    """A workload (non-human identity) that is making a request.

    ``id`` is a stable identifier — a SPIFFE ID (``spiffe://domain/workload``),
    a certificate subject, or a JWT subject. ``verified_via`` records *how* we
    know it; the policy layer refuses unverified callers outside dev mode.
    """

    id: str
    verified_via: VerificationMethod
    trust_domain: str | None = None

    @property
    def is_verified(self) -> bool:
        return self.verified_via.is_verified


@dataclass(frozen=True, slots=True)
class EndUserAssertion:
    """A human on whose behalf a caller acts (the delegated 'for whom')."""

    subject: str
    issuer: str
    verified_via: VerificationMethod = VerificationMethod.OIDC_JWT

    @property
    def is_verified(self) -> bool:
        return self.verified_via.is_verified


@dataclass(frozen=True, slots=True)
class AccessRequest:
    """A request to perform ``action`` on ``target`` as ``caller``.

    ``on_behalf_of`` is set when a human is delegating; ``None`` means the caller
    acts purely as itself (automation). ``action`` is a provider-defined verb
    such as ``"read:listings"`` or ``"write:events.create"``.
    """

    caller: CallerIdentity
    target: str
    action: str
    on_behalf_of: EndUserAssertion | None = None


class Effect(enum.Enum):
    """The outcome of a policy evaluation."""

    ALLOW = "allow"
    DENY = "deny"
    STEP_UP = "step_up"  # allowed in principle, but needs human confirmation


@dataclass(frozen=True, slots=True)
class Decision:
    """The policy engine's verdict for an :class:`AccessRequest`.

    ``reason`` is always populated (including for allows) so every decision is
    auditable. ``obligations`` carries any conditions the broker must honour,
    e.g. ``{"step_up": "approve-payment"}``.
    """

    effect: Effect
    reason: str
    obligations: dict[str, str] = field(default_factory=dict)

    @property
    def allowed(self) -> bool:
        return self.effect is Effect.ALLOW

    @staticmethod
    def deny(reason: str) -> "Decision":
        return Decision(Effect.DENY, reason)

    @staticmethod
    def allow(reason: str) -> "Decision":
        return Decision(Effect.ALLOW, reason)
