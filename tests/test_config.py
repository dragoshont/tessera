"""Tests for config loading & the fail-closed validation invariants."""

from __future__ import annotations

from tessera.config import Config, IdentityConfig, ServerConfig, from_dict, load_config


def test_defaults_are_valid_on_loopback() -> None:
    cfg = Config()
    assert cfg.validate() == []
    assert cfg.server.is_loopback
    assert cfg.policy.default == "deny"


def test_dev_mode_rejected_when_not_loopback() -> None:
    cfg = Config(
        server=ServerConfig(host="0.0.0.0", port=8080),
        identity=IdentityConfig(mode="dev"),
    )
    problems = cfg.validate()
    assert any("dev" in p and "loopback" in p for p in problems)


def test_dev_mode_allowed_on_loopback() -> None:
    cfg = Config(identity=IdentityConfig(mode="dev"))  # host defaults to 127.0.0.1
    assert cfg.validate() == []


def test_fail_open_policy_is_rejected() -> None:
    cfg = from_dict({"policy": {"default": "allow"}})
    problems = cfg.validate()
    assert any("fail-open" in p for p in problems)


def test_oidc_mode_requires_issuers() -> None:
    cfg = from_dict({"identity": {"mode": "oidc"}})
    assert any("oidc_issuers" in p for p in cfg.validate())


def test_invalid_port_flagged() -> None:
    cfg = from_dict({"server": {"port": 70000}})
    assert any("port" in p for p in cfg.validate())


def test_from_dict_fills_defaults_and_reads_values() -> None:
    cfg = from_dict({"server": {"port": 9000}, "identity": {"trust_domain": "acme.test"}})
    assert cfg.server.port == 9000
    assert cfg.server.host == "127.0.0.1"  # default preserved
    assert cfg.identity.trust_domain == "acme.test"


def test_env_overrides(tmp_path) -> None:
    env = {"TESSERA_SERVER_PORT": "9999", "TESSERA_SERVER_HOST": "0.0.0.0"}
    cfg = load_config(tmp_path / "nope.toml", environ=env)  # missing file -> defaults
    assert cfg.server.port == 9999
    assert cfg.server.host == "0.0.0.0"


def test_missing_file_yields_defaults(tmp_path) -> None:
    cfg = load_config(tmp_path / "absent.toml", environ={})
    assert cfg.validate() == []


def test_loads_real_toml(tmp_path) -> None:
    p = tmp_path / "tessera.toml"
    p.write_text(
        '[server]\nhost = "127.0.0.1"\nport = 8081\n'
        '[identity]\nmode = "mtls"\ntrust_domain = "x.local"\n'
        '[policy]\ndefault = "deny"\n',
        encoding="utf-8",
    )
    cfg = load_config(p, environ={})
    assert cfg.server.port == 8081
    assert cfg.identity.trust_domain == "x.local"
    assert cfg.validate() == []
