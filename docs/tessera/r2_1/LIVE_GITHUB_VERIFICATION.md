# Live GitHub Verification

## Status

Identity/read: `BLOCKED_EXTERNAL`
Write: `NOT_RUN_SAFE_MODE`

GitHub CLI authentication alone is not a Tessera Account and was not converted into product success. No Tessera GitHub credential or designated mutation repository was configured.

## Implemented Contract

Validation calls fixed-origin `GET https://api.github.com/user`, persists stable provider account ID and login, stores provider scopes separately, and atomically projects canonical permissions/capability bindings.

- classic `repo`/`public_repo`: issue read/write permitted;
- no scope header (fine-grained): each allow-listed repository is safely read-probed; read may be enabled; write is not inferred;
- failed auth: `AUTH_REQUIRED`;
- malformed/unavailable: `DEGRADED`;
- Account lifecycle immediately recomputes dependent Job health.

Repository calls remain fixed-origin, allow-listed, bounded to 32 KiB, and pass through Tessera grants, capability dispatch, Evidence, and exact Actions.

## Exact Live Read

```bash
TESSERA_LIVE_GITHUB_REPOSITORY=owner/repository ./gates/live-alpha-checks.sh
```

Expected: GitHub identity PASS and GitHub read capability PASS.

## Explicit Write Guard

Write requires all three values:

```bash
TESSERA_LIVE_GITHUB_REPOSITORY=owner/sandbox \
TESSERA_LIVE_WRITE_CONFIRM_TARGET=owner/sandbox \
TESSERA_ENABLE_LIVE_WRITE_TESTS=true \
./gates/live-alpha-checks.sh
```

The harness prints the exact target/title, creates an Action proposal, approves it through Tessera, and requires provider verification. Never target an arbitrary repository.