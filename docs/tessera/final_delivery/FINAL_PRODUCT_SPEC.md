# Final Product Specification

Tessera is one deployed canonical backend with two clients: the Web app and a packaged macOS Electron app built from the same React product routes. Conversations, Memory, Jobs, Accounts, Plugins, Actions, Evidence and Activity are server-owned. Desktop contains no scheduler, provider client, SQLite database or canonical product store.

Required shared routes: Chat, Jobs, Accounts, Plugins, Memory, Activity and Settings. Consequential provider calls remain exact Tessera Actions. Jobs continue when Desktop is closed.