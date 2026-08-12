# LiteLLM E2E

Existing homelab LiteLLM v1.94.0 is healthy and reachable from the Tessera network boundary. A real credential-safe in-pod completion on `claude-haiku-4.5` returned HTTP 200 with `LITELLM LIVE`. Owner-scoped connection through Tessera Settings, streaming and tool execution remain pending human OIDC sign-in. Clients never receive the LiteLLM credential.