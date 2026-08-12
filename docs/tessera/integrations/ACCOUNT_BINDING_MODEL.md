# Account Binding Model

Every external identity is a separate Tessera `ConnectedAccount` owned by one principal. It binds an integration/plugin version, provider account identity, non-secret configuration, credential/session reference, permissions and stable capability grants.

Invocation selects only an account already granted to the Conversation or Job. More than one eligible account is an ambiguity error unless the user explicitly selects one. Plugins cannot substitute provider identities or endpoints.

Regina Maria uses one MCP process/container and one rotating session chain per owner. The user's and wife's accounts have separate credentials, endpoints, logs, identities and grants. Disconnecting one does not affect the other. Fresh login/MFA is performed independently by the account holder.

Disconnection prevents future capability execution. Plugin disable blocks all bound capabilities and dependent Jobs but preserves account metadata and historical records until explicit safe removal.
