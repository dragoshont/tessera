# How-to guides

Short, practical recipes. Each guide helps you **do one task** that you already
understand. A guide assumes you know the basics; if you are new, start with a
[tutorial](../tutorials/README.md) instead.

> A how-to guide is goal-oriented. It is like a recipe: a series of steps that get
> you to a result. It does **not** stop to explain every idea — for that, see
> [Explanation](../explanation/README.md).

## Guides

| Guide | The task |
|---|---|
| [Connect a domain MCP](connect-a-domain-mcp.md) | Wire a non-human caller (an MCP, CLI, or job) to `/v1/broker`. |
| [Register a non-human caller](register-a-non-human-caller.md) | Give a workload its own identity (an app-only token, no stored secret). |
| [Add a provider recipe](add-a-provider-recipe.md) | Write a recipe for a new provider (API key, bearer, or cookie). |
| [Enable egress safely](enable-egress-safely.md) | Turn on outbound calls the safe way (the two gates + the SSRF allow-list). |
| [Migrate a credential-holding MCP](migrate-a-credential-holding-mcp.md) | Move an MCP that holds keys onto Tessera, one service at a time. |
| [Run the admin portal](run-the-admin-portal.md) | Turn on the read-mostly web portal and set operators. |

## The order that usually works

For a first real integration, this order avoids surprises:

1. [Register a non-human caller](register-a-non-human-caller.md) — give it an identity.
2. [Add a provider recipe](add-a-provider-recipe.md) — describe the provider.
3. Write the grant + binding ([policy reference](../reference/policy-document.md)).
4. [Enable egress safely](enable-egress-safely.md) — turn on the upstream path.
5. [Connect the caller](connect-a-domain-mcp.md) — make the call.
