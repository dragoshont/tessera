"""Audit — an append-only, secret-free record of every brokering decision.

Each line is one JSON object: who asked (caller), for whom, what they wanted
(target + action), what was decided, and the *status* of any credential resolved.
By construction the record can contain **no secret material** — the broker only
ever hands the audit sink identifiers, an :class:`~tessera.model.Effect`, and a
:class:`~tessera.resolver.CredentialStatus` enum, never a bundle.

Output goes to a file path, or to stdout when the path is ``"-"`` (the right
choice in a container, where logs are collected centrally).
"""

from __future__ import annotations

import json
import sys
import time
from typing import TextIO

from .model import AccessRequest, Decision
from .resolver import ResolvedCredential


class AuditSink:
    """Writes one JSON line per decision. Thread-safe enough for the broker's use."""

    def __init__(self, stream: TextIO) -> None:
        self._stream = stream

    @classmethod
    def open(cls, path: str) -> "AuditSink":
        if path == "-" or path == "":
            return cls(sys.stdout)
        return cls(open(path, "a", encoding="utf-8"))  # noqa: SIM115 (long-lived)

    def record(
        self,
        request: AccessRequest,
        decision: Decision,
        credential: ResolvedCredential | None = None,
    ) -> dict:
        entry = {
            "ts": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
            "caller": request.caller.id,
            "caller_verified": request.caller.is_verified,
            "on_behalf_of": (
                request.on_behalf_of.subject if request.on_behalf_of else None
            ),
            "target": request.target,
            "action": request.action,
            "effect": decision.effect.value,
            "reason": decision.reason,
            "credential_status": credential.status.value if credential else None,
        }
        self._stream.write(json.dumps(entry, ensure_ascii=False) + "\n")
        self._stream.flush()
        return entry
