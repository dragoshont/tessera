# Final Product Specification

Tessera is one canonical backend with three clients: the Web app, a packaged macOS Electron app built from the React product routes, and a native React Native iOS app. Conversations, Memory, Jobs, Accounts, Plugins, Actions, Evidence and Activity are server-owned. No client contains a scheduler, provider credential/client, SQLite database or canonical product store.

Required shared surfaces: Chat, Jobs, Accounts, Plugins, Memory, Activity and Settings. Consequential provider calls remain exact Tessera Actions. Jobs continue when every client is closed. Native routes require TLS plus the stable Tessera Home server UUID before authentication.