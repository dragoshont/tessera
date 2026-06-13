"""Configuration loading & validation.

Tessera reads a single TOML file (``tessera.toml`` by default) using only the
standard library (``tomllib``). Every setting has a safe default, so a minimal
config is valid; the goal is that a new user can be running in a couple of
minutes. A few environment variables can override the most common values for
container deployments.

The :func:`Config.validate` method enforces the *fail-closed* invariants — it
returns human-readable problems rather than raising, so ``tessera validate`` can
print them all at once.
"""

from __future__ import annotations

import os
import tomllib
from dataclasses import dataclass, field, fields, replace
from pathlib import Path

# Env overrides: only the handful that matter for containerized deploys.
_ENV_PREFIX = "TESSERA_"


@dataclass(frozen=True, slots=True)
class ServerConfig:
    host: str = "127.0.0.1"
    port: int = 8080

    @property
    def is_loopback(self) -> bool:
        return self.host in {"127.0.0.1", "::1", "localhost"}


@dataclass(frozen=True, slots=True)
class IdentityConfig:
    mode: str = "mtls"  # "mtls" | "oidc" | "dev"
    trust_domain: str = "tessera.local"
    oidc_issuers: tuple[str, ...] = ()
    oidc_audience: str = "tessera"


@dataclass(frozen=True, slots=True)
class PolicyConfig:
    default: str = "deny"  # "deny" | "allow"
    grants: str = "grants.toml"


@dataclass(frozen=True, slots=True)
class AuditConfig:
    enabled: bool = True
    path: str = "audit.log"


@dataclass(frozen=True, slots=True)
class Config:
    server: ServerConfig = field(default_factory=ServerConfig)
    identity: IdentityConfig = field(default_factory=IdentityConfig)
    policy: PolicyConfig = field(default_factory=PolicyConfig)
    audit: AuditConfig = field(default_factory=AuditConfig)

    # ── validation ────────────────────────────────────────────────────────────
    def validate(self) -> list[str]:
        """Return a list of problems. Empty list == valid.

        These checks encode the security invariants. The most important one:
        ``identity.mode = "dev"`` (no caller verification) is only tolerated when
        the server is bound to loopback, so an unverified broker can never be
        exposed on the network.
        """
        problems: list[str] = []

        if not (0 < self.server.port < 65536):
            problems.append(f"server.port {self.server.port} is out of range (1-65535)")

        if self.identity.mode not in {"mtls", "oidc", "dev"}:
            problems.append(
                f'identity.mode "{self.identity.mode}" is invalid '
                '(expected "mtls", "oidc", or "dev")'
            )
        if self.identity.mode == "dev" and not self.server.is_loopback:
            problems.append(
                'identity.mode "dev" disables caller verification and is only '
                f'allowed on loopback, but server.host is "{self.server.host}". '
                "Use mtls/oidc, or bind to 127.0.0.1."
            )
        if self.identity.mode == "oidc" and not self.identity.oidc_issuers:
            problems.append(
                'identity.mode "oidc" requires at least one identity.oidc_issuers entry'
            )

        if self.policy.default not in {"deny", "allow"}:
            problems.append(
                f'policy.default "{self.policy.default}" is invalid (expected "deny" or "allow")'
            )
        if self.policy.default == "allow":
            problems.append(
                'policy.default "allow" is unsafe (fail-open). Set it to "deny" '
                "and grant explicitly."
            )

        return problems


def _coerce_identity(raw: dict) -> IdentityConfig:
    base = IdentityConfig()
    issuers = raw.get("oidc_issuers", list(base.oidc_issuers))
    return IdentityConfig(
        mode=raw.get("mode", base.mode),
        trust_domain=raw.get("trust_domain", base.trust_domain),
        oidc_issuers=tuple(issuers),
        oidc_audience=raw.get("oidc_audience", base.oidc_audience),
    )


def _build(cls, raw: dict):
    """Construct a (possibly slotted) dataclass from ``raw``, ignoring unknown keys.

    Unknown TOML keys are dropped rather than raising, so a stray/typo'd key
    degrades to the default instead of crashing the loader.
    """
    names = {f.name for f in fields(cls)}
    return cls(**{k: v for k, v in raw.items() if k in names})


def from_dict(data: dict) -> Config:
    """Build a :class:`Config` from a parsed-TOML dict, filling defaults."""
    return Config(
        server=_build(ServerConfig, data.get("server", {})),
        identity=_coerce_identity(data.get("identity", {})),
        policy=_build(PolicyConfig, data.get("policy", {})),
        audit=_build(AuditConfig, data.get("audit", {})),
    )


def _apply_env_overrides(cfg: Config, environ: dict[str, str] | None = None) -> Config:
    """Override a few common values from ``TESSERA_*`` env vars (for containers)."""
    env = environ if environ is not None else os.environ
    server = cfg.server
    if (host := env.get(_ENV_PREFIX + "SERVER_HOST")) is not None:
        server = replace(server, host=host)
    if (port := env.get(_ENV_PREFIX + "SERVER_PORT")) is not None:
        try:
            server = replace(server, port=int(port))
        except ValueError:
            pass  # surfaced by validate() as an out-of-range/invalid port stays default
    policy = cfg.policy
    if (default := env.get(_ENV_PREFIX + "POLICY_DEFAULT")) is not None:
        policy = replace(policy, default=default)
    return replace(cfg, server=server, policy=policy)


def load_config(
    path: str | os.PathLike[str] | None = None,
    *,
    environ: dict[str, str] | None = None,
) -> Config:
    """Load config from ``path`` (default ``tessera.toml``), applying env overrides.

    A missing file is not an error: you get the all-defaults config (which is
    valid for loopback/dev). This keeps first-run friction near zero.
    """
    cfg_path = Path(path) if path is not None else Path("tessera.toml")
    data: dict = {}
    if cfg_path.exists():
        with cfg_path.open("rb") as fh:
            data = tomllib.load(fh)
    return _apply_env_overrides(from_dict(data), environ)
