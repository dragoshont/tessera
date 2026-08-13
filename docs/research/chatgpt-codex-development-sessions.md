# ChatGPT/Codex development execution and session continuity

**Research date:** 2026-08-12

## Findings from official sources

OpenAI documents two different execution models. Codex local and worktree chats
run on the user's computer under OS-native sandboxing: Seatbelt on macOS,
`bubblewrap` plus seccomp on Linux, native Windows sandboxing, or Linux sandboxing
inside WSL2. A Dev Container may provide an outer boundary, but a Linux VM is not
the default desktop architecture.

Codex cloud tasks do run in OpenAI-managed containers. A task starts from a
selected repository branch or commit, creates an isolated container, runs a setup
phase, applies network policy, performs the agent phase, and returns a summary and
diff. The agent phase is offline by default but may receive explicit domain and
HTTP-method allowlists. OpenAI does not publicly document whether the container
host uses shared kernels, VMs, microVMs, or single-tenant placement. Tessera must
not claim VM-grade isolation unless its deployment actually supplies it.

Session continuation is also several mechanisms rather than live container
migration:

- cloud tasks are account-visible durable tasks;
- desktop reads shared Codex history/configuration and can move a chat plus Git
  state between Local and Worktree modes;
- managed worktrees associate one checkout with one chat and retain recent
  worktrees/snapshots;
- Mobile Remote controls a paired desktop host. Files, credentials, MCP servers,
  plugins, policy, and execution remain on that host, which must stay awake;
- OpenAI explicitly does not describe seamless handoff of a running local
  container to cloud or mobile.

The relevant extension concepts are separate:

- `AGENTS.md` gives repository guidance;
- skills package instructions, resources, and scripts;
- agents/subagents are delegated model threads constrained by tools and sandbox;
- MCP connects external tools and context, with per-server/tool enablement,
  authentication, timeouts, and approvals;
- plugins bundle reviewed skills and MCP-backed connectors and may include UI.

## Sources

- [OpenAI: Sandbox](https://learn.chatgpt.com/docs/sandboxing)
- [OpenAI: Agent approvals and security](https://learn.chatgpt.com/docs/agent-approvals-security)
- [OpenAI: Environment modes](https://learn.chatgpt.com/docs/environments/modes)
- [OpenAI: Cloud environments](https://learn.chatgpt.com/docs/environments/cloud-environment)
- [OpenAI: Cloud internet access](https://learn.chatgpt.com/docs/cloud/internet-access)
- [OpenAI: Worktrees](https://learn.chatgpt.com/docs/environments/git-worktrees)
- [OpenAI: Remote](https://learn.chatgpt.com/docs/remote)
- [OpenAI: Remote connections](https://learn.chatgpt.com/docs/remote-connections)
- [OpenAI: MCP](https://learn.chatgpt.com/docs/extend/mcp)
- [OpenAI: Subagents](https://learn.chatgpt.com/docs/agent-configuration/subagents)
- [OpenAI: Plugins](https://learn.chatgpt.com/docs/plugins)
- [OpenAI: Build an MCP server](https://developers.openai.com/apps-sdk/build/mcp-server)
- [Introducing Codex](https://openai.com/index/introducing-codex/), 2025-05-16,
  updated 2025-06-03
- [Introducing upgrades to Codex](https://openai.com/index/introducing-upgrades-to-codex/),
  2025-09-15, updated 2025-09-23
- [Introducing the Codex app](https://openai.com/index/introducing-the-codex-app/),
  2026-02-02, Windows update 2026-03-04

Pages without visible publication dates were accessed on the research date.

## Tessera implications

Tessera should persist Conversation, Job, run, checkpoint, base revision, patch,
approval, and bounded output independently from disposable execution state. A
client reconnects to that canonical server state; Tessera must not claim that a
shell or container moved between devices.

Homelab execution belongs in a separate trust zone: short-lived rootless
containers, no runtime socket, no host path, no service-account token in task
pods, bounded resources and output, default-deny egress, server-owned repository
snapshots, and exact approvals for writes. Setup and agent execution need separate
secret and network policy. Container caches must never be described as private or
secret-free without evidence.

The implemented proof follows this model: one owner-scoped Conversation creates a
typed development Job; an ephemeral hardened Kubernetes Job reads an immutable
server snapshot; bounded output returns to the same durable run on every client.
It does not yet provide repository acquisition, edits, build/test profiles,
interactive terminals, or autonomous agents.