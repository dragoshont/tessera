# Tessera extension model

**Status:** Accepted contract; implementation varies by extension type

Tessera keeps four extension concepts distinct. Installation metadata is never
execution authority, and model selection is never authorization.

## Agents

An agent is an owner-scoped, versioned orchestration definition over a model,
instructions, allowed capabilities, MCP tools, development command profiles,
resource budgets, and approval policy. Agents create typed Conversations, Jobs,
Actions, and development runs through existing APIs. They do not own identity,
credentials, canonical memory, filesystem paths, images, network policy, or an
unbounded process loop.

Agent definitions will be reviewed data, disabled by default, and optimistic-
versioned. Subagents inherit or narrow the parent grants and budget; they cannot
widen them. The current proof has no user-installable agent-definition API yet.

## MCP servers

An MCP server contributes external tools and context through the existing
`IMcpClientRuntime`. Remote streamable-HTTP servers require reviewed HTTPS
endpoints and per-owner OAuth or bearer custody. Local stdio-style servers must
run as reviewed sidecars/workloads, never inside Broker and never from arbitrary
`npx` or user-supplied commands. Servers and individual tools can be disabled;
dispatch rechecks grants and availability.

The MCP server/client runtime and public registry inspection exist today. Public
metadata is Inspect-only; installation requires a reviewed local package.

## Apps

An app is a reviewed client surface over canonical Tessera APIs and MCP resources.
It may render typed tool results and initiate typed requests, but it cannot carry
authorization in client-only state or execute remote scripts in a privileged
origin. Web, packaged macOS, and native iOS are built-in apps today. Installable
third-party app resources require a later sandbox/origin/content-security ADR and
are not currently supported.

## Plugins

A plugin is the existing trusted-local, exact-version, hash-pinned package plus an
optional first-party executable assembly. First installation is disabled. The
operator-owned catalog, package manifest, enabled state, owner grants, account
binding, and dispatch-time capability check all remain independent. Plugins may
contribute typed capabilities, model tools, setup descriptors, catalog metadata,
and MCP requirements; they cannot load arbitrary remote code or bypass Actions.

Reviewed local plugin installation and provider-owned catalog adapters exist
today. Public MCP/GitHub metadata cannot execute.

## Development integration

Agents may orchestrate development Jobs. MCP tools may request typed development
tasks. Apps may display and control canonical runs. Plugins may eventually
declare reviewed command-profile metadata. None may provide a client-selected
path, URL, executable, image, environment, mount, shell string, or egress rule.
The server resolves every executable profile and effect class; workspace writes
require an exact Action and durable patch artifact before any write profile is
registered.

## Current capability matrix

| Extension | Discover/review | Install/configure | Execute | Current limit |
|---|---|---|---|---|
| Agent | contract only | not implemented | not implemented | typed agent registry is a later slice |
| MCP server | registry and plugin metadata | reviewed local package / server config | existing MCP runtime | no arbitrary local process install |
| App | built-in app routes | built-in release process | Web/macOS/iOS | no third-party UI sandbox yet |
| Plugin | local, official MCP, provider catalog | exact local hash-pinned package, disabled first | declared enabled capabilities | no downloaded executable code |

This matrix is product truth. UI and API responses must not imply support beyond
the implemented row.