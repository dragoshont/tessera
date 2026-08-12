# MCP Installation

**Current compatibility:** operator-configured Streamable HTTP only. Stdio is not yet a supported Tessera integration mode.

An installation must record server ID, exact endpoint, source, publisher, immutable version/commit or image digest, license, trust state, expected server identity/version, required tools and schemas, network destinations, account type, credentials requested, telemetry/logging, and Tessera risk overlays.

Remote endpoints require TLS except explicitly approved private connectors, a fixed host/path, SSRF and redirect protection, response/time bounds, and no model-controlled URL. Public endpoints use public-only address policy. Private-network access is carried as explicit endpoint policy and does not permit metadata/link-local destinations. Credentials remain references resolved only for an authorized account invocation.

An installation is not executable while `UNTRUSTED` or `DISABLED`. New or changed write tools remain denied until the overlay is reviewed. Secrets never belong in manifests or command arguments.
