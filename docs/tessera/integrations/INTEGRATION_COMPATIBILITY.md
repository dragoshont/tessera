# Integration Compatibility

| Runtime | Current status | Notes |
|---|---|---|
| Streamable HTTP MCP client | Implemented; real protocol and adversarial tests green | Typed schema/server pinning, public/private egress policy and historical runtime identity wired |
| Remote OAuth MCP | Generic acquisition exists | No selected provider delivery claim until real E2E |
| Stdio MCP | Not implemented | Requires a selected integration and process isolation design |
| Generic HTTP plugin | Existing recipe/transport infrastructure | Provider semantics must live in plugin |
| First-party executable plugin | Implemented; hash-pinned discovery, host contributions and disable behavior tested | Docker and local dev packaging include Gmail, GitHub and RM modules |

Current provider decisions: local Streamable HTTP MCP for Regina Maria; direct official Google API fallback in an isolated Gmail plugin; official GitHub MCP through an isolated mapping plugin.
