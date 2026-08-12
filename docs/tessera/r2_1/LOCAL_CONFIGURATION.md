# Local Configuration

## Files

First startup copies:

- `.dev/tessera.json.example` to `.dev/tessera.json`;
- `.dev/grants.json.example` to `.dev/grants.json`.

The materialized files are local runtime configuration. Product Accounts, model profiles, settings, conversations, Memory, Actions, and Jobs are stored in SQLite. Credentials are stored in Lowkey through opaque owner/account-bound references.

## Model Setup

Use Settings in the product:

1. Enter an HTTPS OpenAI-compatible base endpoint, or loopback HTTP for a local adapter.
2. Enter model ID and credential.
3. Choose Save and validate model.
4. Set the default model if multiple profiles exist.

Remote model DNS is connect-time guarded: public addresses only. Explicit loopback is allowed; RFC1918, ULA, link-local, metadata, multicast, and unspecified addresses are blocked.

## GitHub Setup

Use Accounts:

1. Select GitHub.
2. Name the Account.
3. Enter explicit `owner/repository` allow-list values.
4. Enter a token and validate.

Classic `repo`/`public_repo` scopes map to canonical read/write permissions. Fine-grained tokens do not report OAuth scopes, so Tessera probes allow-listed repositories and enables read only; write remains unavailable without provider evidence.

## Environment Overrides

Important runtime variables:

- `TESSERA_PRODUCT_DB_PATH`
- `TESSERA_CONTINUITY_DB_PATH`
- `TESSERA_PLUGIN_ROOT`
- `TESSERA_PLUGIN_CATALOG`
- `TESSERA_VAULT_URL`
- `TESSERA_KEYVAULT_EMULATOR`
- `TESSERA_WEB_ROOT`

Live-check variables are documented in the operator guide. Never put provider secrets in environment variables for the live harness; configure them through Tessera.