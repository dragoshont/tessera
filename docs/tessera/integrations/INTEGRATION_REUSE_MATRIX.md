# Integration Reuse Matrix

**Reviewed:** 2026-08-11

| Integration | Candidate | Source / license | Auth and transport | Decision |
|---|---|---|---|---|
| Regina Maria | Local `reginamaria-mcp` 0.5.36 | Local source, Apache-2.0; committed base `697a2a052a6b3e5fd2fb92680cd87b624ab07373`, currently dirty | One rotating RM session per process; Streamable HTTP `/mcp`; optional Key Vault | `REUSE_LOCAL_MCP`, after pending hardening is validated and pinned |
| Regina Maria | Public/provider MCP | No credible provider-maintained or public alternative found | Unknown | Rejected: no verifiable candidate |
| Gmail | Google Gmail remote MCP Developer Preview | Google-hosted; service implementation not portable; [configuration](https://developers.google.com/workspace/gmail/api/guides/configure-mcp-server) | OAuth 2.0; `gmail.readonly` + `gmail.compose`; Streamable HTTP | Defer: no send/history cursor, preview status, multi-account behavior undocumented |
| Gmail | `taylorwilsdon/google_workspace_mcp` v1.23.1 | [MIT source](https://github.com/taylorwilsdon/google_workspace_mcp), active | OAuth 2.1, stdio/Streamable HTTP, self-hosted, multi-user, broad tool surface | Rejected for current custody boundary; retain as reference candidate |
| Gmail | `googleworkspace/cli` v0.22.5 | [Apache-2.0 source](https://github.com/googleworkspace/cli), Google Workspace org, explicitly unsupported product | Local OAuth/keyring; CLI structured JSON, not MCP | Reference/substrate only |
| Gmail | `GongRzhe/Gmail-MCP-Server` | [Archived MIT repository](https://github.com/GongRzhe/Gmail-MCP-Server), no releases | Global local credential file, stdio, broad modify/delete/filesystem tools | Rejected: archived, unsafe scope/filesystem and immediate mutations |
| GitHub | `github/github-mcp-server` v1.9.0 | [Official MIT source](https://github.com/github/github-mcp-server), active release | Remote OAuth/PAT or local OAuth/PAT/App; HTTP or stdio; tool allow-lists/read-only mode | `REUSE_MCP` target, wrapped by Tessera risk/account/Action policy |
| GitHub | Archived reference server | Unmaintained archive | PAT/stdio | Rejected in favor of official server |

No floating `latest`, `uvx`, or `npx` execution is accepted for deployed sensitive integrations. Selected artifacts require an immutable commit or image digest, source/license review, and a recorded trust decision.
