# R2 Model Provider Model

A model provider is a `ConnectedAccount` plus one or more `ModelProfile` records: adapter kind, endpoint, model, opaque credential reference, context limit, streaming/tool support, enabled state, and optimistic version. Settings select default chat/lightweight profiles; conversations and Jobs may explicitly override.

Production adapters are `openai-compatible-remote` and explicitly enabled `openai-compatible-local`. Remote endpoints require HTTPS. Local HTTP is allowed only when the account is explicitly local and the host resolves to loopback. Validation calls the configured models/chat endpoint through the constrained transport. Errors normalize to authentication, rate limit, unavailable, timeout, invalid model, and malformed structured output. No hardcoded output or production fake adapter exists.
