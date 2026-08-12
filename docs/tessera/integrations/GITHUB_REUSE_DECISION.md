# GitHub Reuse Decision

## Decision

`REUSE_MCP`

Target GitHub's official `github/github-mcp-server`, pinned to a reviewed release/image digest. It is MIT licensed, actively maintained, supports remote HTTP and local stdio, and offers exact tool allow-lists plus read-only and lockdown modes.

Tessera will expose only mapped stable capabilities. Provider descriptions and annotations cannot lower Tessera risk. Account credentials remain Tessera references; issue creation remains an exact Tessera Action and is verified by a provider read-back. Until MCP parity and account custody are proven, the current bounded REST code may remain only as an extracted `Tessera.Plugins.GitHub` fallback.

The archived Model Context Protocol GitHub reference server is rejected because maintenance moved to GitHub's official server.
