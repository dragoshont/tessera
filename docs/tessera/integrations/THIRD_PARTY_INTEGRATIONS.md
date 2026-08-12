# Third-Party Integrations

## Regina Maria MCP

- Source: private local `reginamaria-mcp`; Apache-2.0; v0.5.38 tag at clean commit `16037af`.
- Transport: Streamable HTTP; one account/session per deployment.
- Credentials: RM session, APIM app value, optional Key Vault; mutation action token.
- Network: RM API/site plus configured identity/secret endpoints.
- Logging: local redacted audit; no external telemetry found.
- Trust: pending release pin, authenticated ingress and hardening validation.

## Gmail

- Chosen runtime: first-party direct Google API fallback while Google's provider MCP is preview/incomplete.
- Credentials: owner-bound OAuth access/refresh references; least required Gmail scopes.
- Network: Google OAuth and Gmail APIs only.
- Data: highly sensitive mailbox content; no unknown hosted intermediary.
- Trust: built-in plugin after extraction and tests.

## GitHub MCP Server

- Source: official [GitHub MCP Server](https://github.com/github/github-mcp-server), MIT, target reviewed release `v1.9.0`; deployment requires immutable digest.
- Transport: fixed official remote HTTPS endpoint with an exact tool allow-list; stdio is not enabled.
- Credentials: OAuth/PAT/GitHub App reference; least permissions.
- Network: GitHub endpoints only.
- Logging/telemetry: provider audit where available; local telemetry needs verification before deploy.
- Trust: trusted external only after pin, tool allow-list, account binding and Action wrapper tests.
