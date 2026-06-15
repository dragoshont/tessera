# Explanation

These pages help you **understand** Tessera: how it works, and why it is built this
way. Read them to build a mental model. They are for thinking, not for doing — when
you want to perform a task, use the [how-to guides](../how-to/README.md) instead.

## Pages

| Page | What it explains |
|---|---|
| [What Tessera is and why](what-is-tessera.md) | The problem, the idea, and what Tessera is *not*. Start here. |
| [How a call works](how-a-call-works.md) | The six steps of a single brokered call, in plain language. |
| [Identity model: who and for whom](identity-model.md) | The two identities (caller and end-user) and why they are separate. |
| [Architecture](architecture.md) | The full system: components, request lifecycle, deployment shapes. |
| [Positioning: where Tessera fits](positioning.md) | The two-plane stack (access gateway vs action broker) and scenarios. |
| [Security model and threats](security-model.md) | The invariants, the threat model, and the trust boundaries. |
| [Standards alignment](standards-alignment.md) | Why this design is the *de-facto* shape, mapped to the specifications. |
| [Credential ownership](credential-ownership.md) | The three owners (service / user / dependent) and what changes. |
| [Architecture decision records](decisions.md) | The load-bearing decisions and why they were made. |

## The shape of the idea

Tessera answers one question well:

> *May this **caller** perform **this action**, with **this hidden credential**,
> right now?*

Everything in these pages comes back to that question. The
[identity model](identity-model.md) explains *who* asks. The
[security model](security-model.md) explains *how the answer is trusted*. The
[standards alignment](standards-alignment.md) explains *why the shape is correct*.
