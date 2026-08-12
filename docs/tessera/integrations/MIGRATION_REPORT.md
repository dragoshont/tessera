# Integration Migration Report

**Status:** Provider extraction complete in the repository. Deployment and real-account validation remain separate delivery work.

| Provider | Old owner | New owner/runtime | Behavior preserved | Current state |
|---|---|---|---|---|
| Regina Maria | Broker capabilities/endpoints/health plus generic Providers adapter | `Tessera.Plugins.ReginaMaria` mapping over local MCP and `Tessera.Mcp.Client` | Yes: identity, list/get, availability, canonical proposal, Action-gated write, read-back verification | Extracted; 10 external-plugin tests pass |
| Gmail | Broker OAuth/workers/capabilities plus generic Providers REST/OAuth | `Tessera.Plugins.Gmail` direct official API fallback | Yes: OAuth/refresh/revoke, bounded search/read/thread/labels, history sync, draft/send Action and verification | Extracted; 9 plugin tests pass |
| GitHub | Broker inline tools/capabilities plus generic Providers REST | `Tessera.Plugins.GitHub` mapping to the official GitHub MCP server | Yes: identity, repository allow-list, list/create, Action and provider verification | Extracted; 8 plugin tests pass |

Generic execution, policy, credential references, Accounts, Actions, Jobs, Evidence and Memory remain Tessera-owned. Schema v14 replaced Gmail-specific sync storage with `plugin_cursor_states`; schema v15 added MCP server ID/name/version and external tool identity to capability history. Six architecture tests enforce project/assembly direction, provider-identifier absence, zero-provider boot, local capability continuity, historical Evidence survival and dependent-Job isolation.
