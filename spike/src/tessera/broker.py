"""The broker core — authorize, then (secretlessly) resolve the credential.

:class:`Broker.handle` is the whole pipeline minus the network: given an
:class:`~tessera.model.AccessRequest`, it asks the Policy Decision Point, and on
``allow`` it resolves the backing credential's *status* (never its bytes), audits
the decision, and returns a :class:`BrokerResult`.

What it deliberately does **not** do (yet): make the outbound call to the upstream
service. That "injection egress" is the next slice and is intentionally gated, so
that deploying the broker never opens an unauthenticated path to a real account.
``handle`` proves the authorize-and-resolve spine end-to-end and is the function
the startup self-test exercises against a real credential store.
"""

from __future__ import annotations

from dataclasses import dataclass

from .audit import AuditSink
from .model import AccessRequest, Decision
from .policy import PolicyDecisionPoint
from .resolver import CredentialResolver, CredentialStatus, ResolvedCredential


@dataclass(frozen=True, slots=True)
class BrokerResult:
    """The broker's verdict for a request. Carries status, never secrets."""

    decision: Decision
    credential: ResolvedCredential | None

    @property
    def ok(self) -> bool:
        """True when the request was allowed *and* a usable credential resolved."""
        return self.decision.allowed and bool(
            self.credential and self.credential.usable
        )


class Broker:
    """Wires the PDP, the resolver, and the audit sink into one decision pipeline."""

    def __init__(
        self,
        pdp: PolicyDecisionPoint,
        resolver: CredentialResolver,
        audit: AuditSink | None = None,
    ) -> None:
        self._pdp = pdp
        self._resolver = resolver
        self._audit = audit

    def handle(self, request: AccessRequest) -> BrokerResult:
        decision = self._pdp.evaluate(request)
        credential: ResolvedCredential | None = None
        if decision.allowed:
            # Only resolve a credential once the request is authorized — never
            # touch the store for a denied request.
            credential = self._resolver.resolve(request)
        if self._audit is not None:
            self._audit.record(request, decision, credential)
        return BrokerResult(decision=decision, credential=credential)
