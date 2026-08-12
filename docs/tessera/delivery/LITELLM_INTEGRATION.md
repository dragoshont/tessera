# LiteLLM Integration

Tessera reuses the existing homelab LiteLLM ClusterIP. `modelGateways` is an operator-owned allow-list; the Settings UI selects the named gateway and accepts a write-only dedicated key plus a model ID. The backend probes `/models` and refuses a model not returned by the gateway before creating the Account/Profile.

The fixed internal prefix uses a private-network-capable, DNS-pinned transport. Arbitrary custom model URLs remain HTTPS or loopback-only. The adapter has passing tests for real-shaped model listing, buffered tool calls, SSE streaming, auth mapping, and route isolation.

The gateway can route to cloud services. Tessera does not call it local merely because it runs in the homelab. The model ID is persisted in the profile and shown in Settings.

Live status: existing LiteLLM is deployed and ready; Tessera has not yet been cut over or given a dedicated key.