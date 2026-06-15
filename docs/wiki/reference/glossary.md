# Glossary

Plain-language definitions of every important Tessera term. Terms are grouped by
topic. Within Tessera, each term always means the same thing.

> How to read this page: the **bold word** is the term. The first sentence is the
> short meaning. Any following sentence adds detail or a contrast.

---

## Identities — who is involved

**Caller** (also: the *WHO*)
: The program that asks Tessera to act. It is a *non-human* identity — an AI agent,
an MCP server, a script, a workflow, or a job. The caller proves its identity and
holds **no** secret.

**End-user** (also: the *FOR WHOM*)
: The person a caller acts on behalf of. The caller forwards the person's signed
login token. Not every call has an end-user: a pure automation acts as *itself*,
with no person involved.

**Verified identity**
: An identity that was *proven*, not merely *claimed*. A caller is verified by a
client certificate, a SPIFFE SVID, or a signed app-only token. An end-user is
verified by a signed OIDC token. Tessera never trusts an identity written in a plain
header.

**Non-human identity (NHI)**
: An identity that belongs to software, not a person. A caller is always an NHI.
Managing NHIs well (no shared secrets, least privilege, attributable) is a core
goal — see the [OWASP NHI Top 10](https://owasp.org/www-project-non-human-identities-top-10/).

**Workload identity / SPIFFE SVID / mTLS**
: Ways a *workload* (a running program) proves it is itself. mTLS is a mutual-TLS
client certificate. A SPIFFE SVID is a standard short-lived workload certificate.
Both answer "which program is calling?".

**Delegation**
: A caller acts *for* an end-user while keeping its own identity. The audit shows
"caller on behalf of end-user". This is **safer** than impersonation (where the
caller would become indistinguishable from the person). Tessera uses delegation.

**Act-as / `actAs`**
: A caller asks to act under a *named* principal without forwarding that person's
token. Tessera defers this; it would require policy to authorise the caller to act
for that principal (the standard `may_act` claim). See
[RFC 8693](https://datatracker.ietf.org/doc/html/rfc8693).

---

## Policy — who may do what

**Grant**
: One authorisation rule. It says: *this caller*, optionally *for this end-user*,
may perform *these actions* on *this target*. With no matching grant, the request is
denied (this is **default-deny**).

**Binding**
: A link from a *target* to the *stored credential* that backs it, plus who owns
that credential. The grant decides *if* a call is allowed; the binding supplies the
*key*.

**Policy Decision Point (PDP)**
: The part of Tessera that decides allow / deny / step-up. It is deliberately small
and auditable. It is separate from the part that performs the call (the *enforcement*
point) — a Zero-Trust pattern.

**Default-deny**
: The starting answer is always *no*. Access exists only where a grant explicitly
allows it. A new caller or user reaches nothing until you say so.

**Step-up**
: An extra human confirmation required before a high-impact (write / booking / pay)
call runs. The agent cannot perform a step-up action on its own; it must re-ask with
an explicit confirmation.

**Action plane** (`read` · `use` · `manage`)
: The *kind of authority* a verb exercises. `read` observes, `use` operates (the
data plane), `manage` reshapes the integration (the control plane). The control
plane is default-deny even when `use` is granted, and defaults to step-up.

**Action verb**
: The name of an action, written `plane:detail`, for example `read:series` or
`use:search` or `manage:settings`. The text before the `:` is the plane.

---

## Credentials — the secret material

**Credential**
: The secret that authenticates to an upstream service: an API key, a bearer token,
a session cookie, or a username and password. Tessera stores it, injects it, and
**never** returns it to the caller.

**Credential bundle**
: The shape Tessera stores a credential in: a small JSON object with fields like
`access_token`, `refresh_token`, `cookies`, and `extra`. The store keeps the bundle;
the egress reads it to inject the right header.

**Credential owner** (`service` · `user` · `dependent`)
: Whose secret it is. A `service`-owned key is a shared household/service key nobody
personally holds (the default). A `user`-owned login belongs to one person. A
`dependent`-owned credential is seeded by a guardian for someone in their care.

**Credential store**
: Where secrets rest. Azure Key Vault (default), HashiCorp Vault / OpenBao, or a
file/in-memory store for development. Tessera *uses* a store; it is not itself a
store.

**Injection**
: Tessera adds the stored credential to the upstream request (as a header, a cookie,
or an API-key header) at the moment of the call. The caller never receives the
credential. This is the opposite of *handing over* the secret.

**Secretless transit**
: Tessera reaches its credential store without holding a long-lived store secret. It
uses Managed Identity or Workload Identity Federation. There is no store password to
leak.

---

## The call path — how a request flows

**Recipe**
: The description of one provider: its base URL, how to inject its credential, and
the list of tools (operations) it offers. A recipe is operator configuration; the
broker stays provider-agnostic.

**Recipe tool** (also: *provider tool* / *operation*)
: One callable operation in a recipe: a name, an HTTP method, a path, the action verb
it maps to, and optional query parameters and output class.

**Target**
: The name of a provider in policy. A grant, a binding, and a recipe all refer to the
same target name (for example `sonarr` or `health-portal`).

**Egress**
: The act of making the outbound call to the upstream service, with the credential
injected. Egress is **off by default**; an operator turns it on deliberately.

**Injectable provider / HTTP-injectable**
: A provider whose credential can be added to an HTTP request (an API key, a bearer,
a cookie). These fit Tessera. Providers that need arbitrary shell access or device
pairing do **not** fit and stay outside Tessera.

**SSRF allow-list**
: The list of upstream hosts Tessera is permitted to reach. SSRF means *Server-Side
Request Forgery*, an attack that tricks a server into calling an unintended address.
An empty allow-list permits nothing.

**Result class** (`metadata` · `preview` · `fullBody` · `attachment` · `receipt`)
: How much of a person's content a result may contain. A search returns `metadata`
plus opaque handles; full content comes only from a later call using a handle. This
stops a search from spilling a whole mailbox.

**Handle**
: An opaque reference to one content item, returned by a search. It carries no body.
You read full content by passing the handle back to a read operation. A handle from
one provider cannot be replayed against another.

---

## Surfaces — how callers reach Tessera

**`/v1/broker`** (the caller plane)
: The HTTP door for *non-human* callers. A caller posts a request here with its own
app-only token. See the [Broker API reference](broker-api.md).

**`/mcp`** (the MCP surface)
: The Model Context Protocol door for a *chat consumer*. It validates a forwarded
end-user token and offers the `tessera_*` tools. See the
[MCP tools reference](mcp-tools.md).

**Caller authentication plane**
: The mechanism that turns a caller's proof (an app-only token today, mTLS later)
into a verified caller identity at `/v1/broker`. See
[ADR 0021](https://github.com/dragoshont/tessera/blob/main/docs/adr/0021-caller-authentication-plane.md).

**Admin portal**
: A small, read-mostly web interface served at `/`. It shows people, connection
health, and an activity feed. It never shows a secret value.

---

## Roles and modes

**Mode P (per-account)**
: A caller acts as *itself* with a service grant — no end-user token. Used by pure
automations.

**Mode U (multi-user)**
: A caller forwards a *person's* token and acts for that verified end-user. Used by a
multi-user assistant.

**Rotation owner**
: Who keeps a session credential warm (refreshed). `tessera` means Tessera's
background refresher owns it (Mode U). `external` means another component keeps it
warm. `none` means it is static.

---

## Standards and patterns (the "why it is correct")

**No token passthrough**
: The rule that a broker must **not** forward the caller's token to the upstream. It
must validate the token for *itself* and use a *separate* upstream credential. This
is required by the MCP authorization specification.

**Complete mediation**
: Every action is checked by the policy at the broker, every time — never trusted
from the agent. A classic security principle (Saltzer–Schroeder).

**PDP / PEP**
: Policy Decision Point and Policy Enforcement Point. The decision and the
enforcement are separate parts, and every request is authorised. From
[NIST SP 800-207 Zero Trust](https://csrc.nist.gov/pubs/sp/800/207/final).

---

*Cannot find a term? It may be in the [vocabulary reference](vocabulary.md) (the
enums in detail) or the [explanation pages](../explanation/README.md). If a term is
missing, please report it.*
