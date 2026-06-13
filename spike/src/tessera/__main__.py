"""Command-line entry point.

Today this is a small, honest CLI: it can report the version and **validate** a
configuration (config + grants) against the security invariants. The serving and
credential-injection planes are on the roadmap (see ``docs/roadmap.md``); this
command is what makes "easy to configure" true from day one — you can author a
config and get immediate, specific feedback.

    tessera version
    tessera validate [--config tessera.toml] [--grants grants.toml]
"""

from __future__ import annotations

import argparse
import json
import sys

from . import __version__
from .config import load_config
from .policy import load_grants
from .resolver import load_bindings


def _cmd_version(_args: argparse.Namespace) -> int:
    print(f"tessera {__version__}")
    return 0


def _cmd_validate(args: argparse.Namespace) -> int:
    cfg = load_config(args.config)
    problems = cfg.validate()

    grants_path = args.grants or cfg.policy.grants
    grants = load_grants(grants_path)
    bindings = load_bindings(grants_path)

    print(f"config:  {args.config or 'tessera.toml'}")
    print(f"  identity mode : {cfg.identity.mode}")
    print(f"  listen        : {cfg.server.host}:{cfg.server.port}")
    print(f"  policy default: {cfg.policy.default}")
    print(f"grants:  {grants_path}  ({len(grants)} grant(s), {len(bindings)} binding(s))")

    if problems:
        print("\nNOT OK — fix these:")
        for p in problems:
            print(f"  ✗ {p}")
        return 1

    print("\nOK — configuration is valid and fail-closed.")
    if not grants:
        print("note: no grants loaded yet, so every request will be denied.")
    return 0


def _cmd_serve(args: argparse.Namespace) -> int:
    # Imported lazily so `version`/`validate` don't pull in the serving stack.
    from .runtime import build_state
    from .serve import serve_forever

    cfg = load_config(args.config)
    problems = cfg.validate()
    if problems:
        print("refusing to serve — invalid configuration:", file=sys.stderr)
        for p in problems:
            print(f"  ✗ {p}", file=sys.stderr)
        return 1

    grants_path = args.grants or cfg.policy.grants
    state = build_state(cfg, grants_path)

    # Startup banner (secret-free) so the pod logs show exactly what is wired.
    print(
        json.dumps(
            {
                "event": "tessera.start",
                "version": __version__,
                "listen": f"{cfg.server.host}:{cfg.server.port}",
                "store": state.store_kind,
                "broker_endpoint": "enabled"
                if state.authenticator is not None
                else "fail-closed",
                "selftest": state.selftest,
            }
        ),
        flush=True,
    )
    serve_forever(cfg.server.host, cfg.server.port, state)
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="tessera",
        description="Secretless, identity-aware credential broker.",
    )
    sub = parser.add_subparsers(dest="command", required=True)

    p_version = sub.add_parser("version", help="print the version and exit")
    p_version.set_defaults(func=_cmd_version)

    p_validate = sub.add_parser("validate", help="validate config + grants")
    p_validate.add_argument("--config", default=None, help="path to tessera.toml")
    p_validate.add_argument("--grants", default=None, help="path to grants.toml")
    p_validate.set_defaults(func=_cmd_validate)

    p_serve = sub.add_parser("serve", help="run the broker HTTP server")
    p_serve.add_argument("--config", default=None, help="path to tessera.toml")
    p_serve.add_argument("--grants", default=None, help="path to grants.toml")
    p_serve.set_defaults(func=_cmd_serve)

    return parser


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
