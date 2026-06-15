# Reference

Exact, complete, and dry facts. Use these pages to **look up** a detail — a config
field, an API shape, an allowed value. They describe *what is*, not how to use it;
for tasks, see the [how-to guides](../how-to/README.md).

> Reference describes the machinery. It is accurate and complete, and it stays out of
> your way. If you want to *understand* a thing rather than look it up, see
> [Explanation](../explanation/README.md).

## Pages

| Page | Look up… |
|---|---|
| [Configuration (`tessera.json`)](configuration.md) | Every broker config field: type, default, meaning, and the validation rules. |
| [Policy document](policy-document.md) | The grants / bindings / recipes schema — every field. |
| [Broker API (`/v1/broker`)](broker-api.md) | The caller-plane HTTP API: operations, body, status codes; plus health endpoints. |
| [MCP tool surface (`tessera_*`)](mcp-tools.md) | The Model Context Protocol tools a chat consumer calls. |
| [Vocabulary](vocabulary.md) | The enums in detail: planes, injection kinds, result classes, ownership, verification. |
| [Command-line interface (`tessera`)](cli.md) | The `tessera` commands and flags. |
| [Glossary](glossary.md) | Plain-language definitions of every term. |

## A note on accuracy

The source of truth for behaviour is the **code**. These reference pages are kept in
step with it, and each page names the source file it describes. Where a page and the
code disagree, the code is correct — please report the gap.
