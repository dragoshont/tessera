"""Tessera — a secretless, identity-aware credential broker for non-human identities.

Tessera lets an agent, script, or workflow act *as a specific person* against the
services that person uses, without the calling code ever holding the password,
cookie, or token. The secret stays inside Tessera; the caller gets only the result.

This package is the **umbrella broker**. The session-harvesting arm (keeping
custom-login sessions warm) lives in its sibling project, ``sessionkeeper``.
"""

__version__ = "0.0.2"
