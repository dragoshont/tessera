# Gmail Reuse Decision

## Decision

`DIRECT_GOOGLE_API_PLUGIN_FALLBACK` for delivery now, isolated in `Tessera.Plugins.Gmail`.

Google's Gmail remote MCP is the preferred future provider-maintained path but is currently Developer Preview. It supports search/read/thread/labels/drafts but not approved send or history-cursor ingestion. Its multi-account selection and immutable runtime pin are not documented. It therefore cannot replace the current delivery contract yet.

The active `taylorwilsdon/google_workspace_mcp` is self-hosted, MIT, Streamable HTTP, and multi-user, but it owns an independent credential/session subsystem and a much broader mutation surface. Adopting it now would duplicate Tessera custody and expand the sensitive trust boundary. The archived GongRzhe server is rejected.

The existing official Gmail REST/OAuth implementation is retained without expansion, moved out of Broker/generic Providers, and used only behind Tessera Account, Policy, Action, Evidence and Job contracts. Incremental history ingestion remains plugin-owned because available MCP candidates do not satisfy restart-safe cursor requirements.

Re-evaluate Google Gmail MCP after GA, send support, stable OAuth-MCP acquisition, documented multi-account behavior, and history ingestion or a measured hybrid design.
