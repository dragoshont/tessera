# Third-Party Plugin Inventory

## Gmail

No opaque or unlicensed Gmail plugin was imported. Tessera implements the official Google OAuth 2.0 and Gmail REST contracts directly. Network destinations are fixed Google origins. Requested scopes are restricted to `gmail.readonly`, `gmail.compose`, and `gmail.send`; `https://mail.google.com/` is rejected.

## Regina Maria

Tessera reuses the user's existing `reginamaria-mcp` service as a separately deployed domain connector. It is not a public or provider-supported API integration. Each spouse has an isolated service and Key Vault session. Tessera calls only the operator-configured internal `/mcp` endpoints and wraps every mutation in Tessera Actions.

The connector source was extended in this delivery with `rm_account_identity` and non-mutating `rm_prepare_appointment`. Those changes must be versioned, published, and deployed before the Tessera cutover.

## LiteLLM

Existing LiteLLM is used as an OpenAI-compatible model gateway, not a Tessera Plugin. No duplicate gateway is deployed.