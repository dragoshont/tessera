# Web Setup Simplification

## Before

The user had to understand model profiles, LiteLLM endpoints, internal URLs and account/plugin differences before Chat became useful. Installed runtime configuration was not distinguished from account authorization.

## After

1. Open Tessera.
2. Web reads one server-owned setup status.
3. If the configured LiteLLM gateway validates, Web requests the idempotent bootstrap automatically.
4. Server creates non-secret ConnectedAccount metadata, stores the credential only in credential custody, creates the default model profile and sets Chat/lightweight defaults.
5. Chat opens. Manual model configuration is placed under Advanced after setup.

Accounts now separately show integration runtime readiness and user authorization. OAuth application readiness is `READY_TO_CONNECT`; only a verified persisted account is `CONNECTED`. Regina Maria runtime health never implies that either user account is authorized.

## Removed Steps

- No server URL entry in Web.
- No client-side LiteLLM URL or key.
- No manual model-profile/default setup when homelab configuration validates.
- No navigation through an empty `All connections` page.

## Unavoidable Auth

User OAuth/provider consent remains explicit. Gmail and each Regina Maria identity retain separate Connect/Reauthenticate flows. A secondary account is never bootstrapped or marked connected from another account's state.