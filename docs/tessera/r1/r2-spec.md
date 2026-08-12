# Tessera R2 — Usable Alpha: Chat, Jobs, Accounts, Plugins, Real Execution

You are the engineering manager responsible for turning the completed Tessera R0/R1 substrate into the first **actually usable Tessera product**.

This is no longer a synthetic architecture exercise.

This phase must produce a Tessera instance that I can run, open in a browser, connect accounts/services to, chat with, create persistent jobs in, grant capabilities to, inspect memory/context, and use for real work.

Do not build more architecture than the product requires.

Do not substitute mocks for product functionality.

Do not create placeholder pages pretending to be features.

Do not return to provider-specific product definitions such as “Tessera is an Outlook appointment assistant.”

Tessera is the product.

---

# 1. Starting Point

R0 is complete.

It established:

* canonical principals;
* durable Evidence;
* append-oriented Observation Events;
* constrained Assertions;
* provenance;
* deterministic Context;
* policy;
* durable Actions;
* durable Workflows;
* capability contracts;
* replaceable model adapters;
* SQLite persistence;
* authorization and replay protections;
* trust-boundary hardening.

R1 is complete.

It established:

* provider-neutral continuity;
* workflow-specific FollowUp state;
* candidate vs accepted truth;
* current vs historical state;
* correction as evidence;
* conflict handling;
* exact provenance;
* stale/replay protection;
* restart-safe continuity;
* “Why?”;
* context-dependent interpretation;
* compounding memory mechanics.

R1 demonstrated an important Tessera property:

> Later incomplete information can become useful because Tessera retained accepted and corrected prior state.

Do not rebuild these foundations.

Use them.

---

# 2. R2 Mission

Build the first **Tessera usable alpha**.

At the end of R2, a user must be able to:

1. start Tessera;
2. log in;
3. open a real Chat experience;
4. configure at least one real AI model/provider;
5. connect external accounts/services;
6. see connected accounts and their permissions;
7. install or enable capability plugins;
8. ask Tessera questions in Chat;
9. have Chat retrieve Tessera-owned persistent context;
10. have Chat invoke allowed capabilities;
11. see what capability Tessera intends to use;
12. approve consequential external actions;
13. execute a real capability;
14. receive the result in Chat;
15. create persistent Jobs;
16. schedule Jobs;
17. pause/resume/cancel Jobs;
18. inspect Job execution history;
19. let Jobs use explicitly granted accounts/capabilities;
20. see Evidence/History/Why? behind important state;
21. correct Tessera's remembered state;
22. restart Tessera without losing conversations, jobs, accounts, accepted state, or action history.

The end state should feel like:

> **I have Tessera running. I connect things to it, talk to it, give it jobs, and it remembers enough context to keep working.**

---

# 3. This Phase Is Product Integration

The main loop now becomes real:

```text
USER
  ↓
CHAT
  ↓
CONTEXT BUILDER
  ↓
MODEL / REASONER
  ↓
CAPABILITY SELECTION
  ↓
POLICY
  ↓
APPROVAL WHEN REQUIRED
  ↓
CAPABILITY EXECUTION
  ↓
RESULT / EVIDENCE
  ↓
TESSERA STATE
  ↓
CHAT / JOB CONTINUES
```

Jobs use the same primitives:

```text
JOB
  ↓
SCHEDULE / TRIGGER
  ↓
CONTEXT
  ↓
REASON
  ↓
CAPABILITY
  ↓
POLICY
  ↓
EXECUTE
  ↓
VERIFY
  ↓
RECORD
  ↓
NEXT RUN
```

Do not create a separate “agent system” for Jobs.

Chat and Jobs use the same Tessera capabilities, context, policy, execution, and memory.

---

# 4. Product Surfaces Required

R2 must ship these real product surfaces:

```text
Chat
Jobs
Accounts
Plugins
Memory / Activity
Settings
```

Supporting internal/admin screens may remain.

The default product route should no longer be an operator credential console.

---

# 5. Chat — Mandatory

Build a real persistent conversational experience.

## 5.1 Chat capabilities

The user must be able to:

* create a conversation;
* rename it;
* continue it later;
* delete/archive it;
* send prompts;
* receive streamed responses where supported;
* stop generation;
* retry;
* inspect capability calls;
* approve requested side effects;
* see capability results;
* see citations/provenance when Tessera memory was used;
* reopen the conversation after restart.

## 5.2 Persisted conversation model

At minimum:

```text
Conversation
Message
MessagePart
CapabilityCall
CapabilityResult
ContextSnapshotReference
```

Do not persist hidden chain-of-thought.

Persist only product-useful structured reasoning artifacts such as:

* plan summary;
* capability requested;
* evidence used;
* action proposal;
* result;
* error.

## 5.3 Message roles

Support, conceptually:

```text
USER
ASSISTANT
SYSTEM_EVENT
CAPABILITY
```

Do not expose internal security/system prompts as normal chat messages.

## 5.4 Chat Context

A prompt must not simply resend the entire conversation plus entire user database.

Use the R0/R1 Context Builder.

Context may combine:

* recent conversation;
* accepted current state;
* relevant historical state;
* FollowUps;
* relevant Evidence excerpts;
* Job state;
* capability availability;
* account availability;
* user policies.

## 5.5 Context provenance

Important state surfaced in a response should be capable of linking back to Tessera provenance.

Example:

> “You changed the deliverable to lease renewal checklist.”

The system should be able to show:

```text
Why?
→ user correction
→ timestamp
→ previous value
→ current value
```

---

# 6. Model Provider Layer — Real, Configurable

R2 needs a real model connection.

Do not make one provider the architecture.

Implement a production-grade provider abstraction supporting multiple adapters.

At minimum the product must support:

1. one configurable remote LLM provider;
2. one local/OpenAI-compatible endpoint path if practical.

The exact supported providers should follow currently maintained APIs and repository constraints.

Do not hard-code Tessera storage around one provider's IDs or tool schema.

## 6.1 Model Provider Account

Treat model access as a configurable account/service.

A configured model provider includes:

```text
provider
endpoint where applicable
model
credential reference
capabilities
context limit
tool support
enabled/disabled
```

Credentials belong in the Trust Plane, not product tables.

## 6.2 Model selection

Initially allow:

```text
Default chat model
Default lightweight model
Optional model override per conversation/job
```

Do not build an elaborate automatic model marketplace yet.

## 6.3 Failure

Chat must handle:

* provider unavailable;
* rate limit;
* authentication failure;
* invalid model;
* timeout;
* malformed structured output.

Do not silently lose user prompts.

---

# 7. Accounts — Mandatory

Build a real Accounts system where external services can be connected.

The product concept is:

> **An Account is an authorized external identity/service connection that exposes one or more capabilities to Tessera.**

Examples may eventually include:

* Google;
* Microsoft;
* GitHub;
* Slack;
* Notion;
* Home Assistant;
* arbitrary REST systems;
* model providers;
* user-hosted services.

Do not permanently encode these examples into the core domain.

---

# 8. Account Domain

Introduce or formalize:

```text
ConnectedAccount
AccountProvider
AccountCredentialRef
AccountPermission
AccountHealth
AccountCapabilityBinding
```

Canonical Account state must contain metadata only.

Secret material remains in Tessera's credential custody.

## 8.1 Account lifecycle

Support:

```text
CONNECTING
CONNECTED
DEGRADED
AUTH_REQUIRED
DISABLED
REVOKED
ERROR
```

## 8.2 Accounts UI

For every connected Account show:

* display name;
* provider;
* account identity;
* connection status;
* permissions/scopes;
* capabilities enabled;
* last successful use;
* reauthenticate;
* disable;
* disconnect.

Do not expose raw secrets.

---

# 9. Plugin / Connector Architecture — Mandatory

A user should be able to add capabilities to Tessera without changing Tessera's core.

Build a **Tessera Plugin SDK / manifest system**.

Do not build arbitrary executable-code installation from the internet yet.

Start with trusted/local plugin packages.

---

# 10. Plugin Definition

A plugin should conceptually provide:

```yaml
plugin_id:
name:
version:
description:
publisher:
auth:
resources:
capabilities:
configuration_schema:
minimum_tessera_version:
```

Each capability includes:

```yaml
capability_id:
name:
description:
input_schema:
output_schema:
side_effect_class:
required_permissions:
allowed_data_classes:
supports_idempotency:
supports_verification:
```

Plugins may provide:

* account/auth adapters;
* read capabilities;
* write capabilities;
* source ingestion adapters;
* UI metadata.

Plugins MUST NOT own Tessera's canonical memory.

---

# 11. Plugin Product Experience

Build a real Plugins page.

The user can:

* see installed plugins;
* enable/disable plugin;
* inspect capabilities;
* inspect required account types;
* inspect permissions;
* configure plugin;
* connect required account;
* uninstall/remove plugin configuration where safe.

No fake marketplace is required.

A local plugin catalog is sufficient.

---

# 12. Initial Plugins

Do not make R2 depend on one provider.

However, R2 must contain actual useful plugins, not only an SDK.

Implement a small initial plugin set based on what is feasible in the current repository.

Prioritize:

### A. Local/system-safe capability plugin

Examples:

* current date/time;
* structured note/state operation;
* local deterministic utility.

### B. HTTP/API plugin foundation

Allows a trusted plugin to call declared REST endpoints using Tessera credential custody and egress controls.

### C. At least one real connected external-service plugin

Choose based on repository support and available configuration.

It must:

* authenticate for real;
* expose at least one read capability;
* expose at least one meaningful capability;
* appear in Accounts;
* appear in Plugins;
* be callable from Chat.

If credentials/client configuration are not available in the development environment, implementation must still be complete and configuration-driven; final live verification may be marked `BLOCKED_BY_EXTERNAL_CREDENTIALS`.

Do NOT replace it with a fake provider and claim completion.

Tests may use fakes.

The product path may not.

---

# 13. Generic HTTP Capability Foundation

The existing Tessera broker/egress work is valuable here.

Turn that functionality into a product capability foundation.

A trusted plugin should be able to declare an HTTP capability with:

* allowed host;
* method;
* path template;
* query schema;
* request body schema;
* response normalization;
* credential binding;
* timeout;
* maximum result size;
* side-effect class;
* verification strategy.

Do not permit user/model-controlled arbitrary URL execution.

SSRF protections remain mandatory.

---

# 14. Capability Discovery

Chat and Jobs need to know what Tessera can do.

Build a Capability Registry.

A capability record must include:

* plugin;
* stable ID;
* version;
* account requirement;
* permission requirement;
* side-effect class;
* availability;
* description;
* input/output schema.

The model receives a filtered list based on:

* user;
* enabled plugin;
* connected Account;
* permissions;
* Job/Conversation policy.

Do not expose every capability indiscriminately.

---

# 15. Capability Invocation From Chat

Implement real capability calling.

Flow:

```text
User prompt
   ↓
Model determines capability useful
   ↓
Tessera validates structured capability request
   ↓
Policy evaluates
   ↓
If READ_ONLY:
    execute if policy allows
Else:
    create Action proposal
    request user approval
   ↓
execute
   ↓
verify where supported
   ↓
record result as Evidence/Event
   ↓
return structured result to model
   ↓
assistant response
```

The model cannot invoke the provider directly.

All execution goes through Tessera.

---

# 16. Side-Effect Policy

Use the R0 Action model.

Recommended default:

```text
READ_ONLY
→ allowed after account authorization

LOCAL_REVERSIBLE
→ policy dependent

EXTERNAL_REVERSIBLE
→ approval by default

EXTERNAL_COMMUNICATION
→ explicit approval by default

HIGH_IMPACT
→ denied by default
```

User-configurable automation policy may come later in R2 if safely scoped.

---

# 17. Action Approval UX

When Chat wants to perform an external side effect, show a concrete action card.

Example:

```text
Tessera wants to:

Send email to:
  person@example.com

Subject:
  ...

Body:
  ...

Using:
  Google Account — me@example.com

[Approve] [Edit] [Cancel]
```

Approval MUST bind to:

* exact user;
* exact capability;
* exact target;
* exact payload;
* expiry;
* one use.

If edited, generate a new proposal/authorization.

---

# 18. Jobs — Mandatory

Jobs are a core R2 product capability.

A Job is:

> **A durable instruction that Tessera should execute now, later, or repeatedly using explicitly granted context, accounts, and capabilities.**

Examples:

* “Every morning summarize my new important messages.”
* “Every Friday tell me which FollowUps are still open.”
* “Check this service every hour and alert me if state changes.”
* “At 6 PM prepare tomorrow's agenda.”
* “Once a week review outstanding commitments.”

---

# 19. Job Domain

Implement:

```text
Job
JobSchedule
JobRun
JobStep / WorkflowCheckpoint
JobCapabilityGrant
JobAccountGrant
JobContextPolicy
JobOutput
```

## Job status

```text
DRAFT
ACTIVE
PAUSED
RUNNING
WAITING_FOR_APPROVAL
SUCCEEDED
FAILED
CANCELED
```

A recurring Job remains ACTIVE after successful runs.

---

# 20. Job Creation

Jobs must be creatable from:

## Chat

Example:

> “Every weekday at 8 summarize my open FollowUps.”

Tessera proposes a structured Job.

The user reviews and confirms it.

## Jobs UI

Allow manual creation/editing.

---

# 21. Job Schedule

Support at least:

* run now;
* one-time timestamp;
* recurring simple schedules;
* cron or equivalent internal schedule representation.

Prefer a durable scheduler.

Schedules survive restart.

Do not rely on an in-memory timer.

---

# 22. Job Permissions

Every Job has explicit scope.

At minimum:

```text
allowed accounts
allowed capabilities
allowed side-effect classes
context access
```

A Job must not inherit unlimited authority merely because the user created it.

---

# 23. Job Approval Behavior

Read-only Jobs may run unattended within granted policy.

Jobs requiring external side effects should default to:

```text
WAITING_FOR_APPROVAL
```

The Jobs UI and Chat must surface pending approval.

Later Tessera may support explicit always-allow policies for tightly scoped actions.

Do not silently implement autonomous communication.

---

# 24. Job Run History

Every run must show:

* start/end;
* model;
* context snapshot;
* capabilities used;
* account used;
* actions;
* approval state;
* outputs;
* errors;
* verification;
* relevant Evidence created.

This is product observability, not chain-of-thought.

---

# 25. Jobs UI

Required:

## Jobs list

Show:

* name;
* active/paused;
* next run;
* previous result;
* health.

## Job detail

Show:

* instruction;
* schedule;
* model;
* account grants;
* capability grants;
* context policy;
* run history;
* pending approvals.

## Run detail

Show actual execution trace at the product level.

---

# 26. Memory in Chat

R2 should expose the continuity system meaningfully.

The user should be able to ask:

> “What do you remember about this?”

> “Why do you think that?”

> “What changed?”

> “Forget/correct this.”

Chat should use:

* accepted Assertions;
* FollowUps;
* corrections;
* relevant Evidence;
* conversation history.

Do not expose arbitrary raw database rows.

---

# 27. Conversation Memory vs Personal Memory

Keep these separate.

## Conversation state

Useful to a specific conversation.

## Tessera personal/durable state

Accepted facts, corrections, FollowUps, Evidence, etc.

A chat message does not automatically become durable personal truth.

Potential memory insertion flow:

```text
conversation
  ↓
candidate durable memory
  ↓
policy / confidence / review
  ↓
accepted Tessera state
```

For R2, default to conservative promotion.

---

# 28. “Remember This”

Support an explicit user operation:

> “Remember that I prefer X.”

This should create explicit user-asserted Evidence/Assertion through normal Tessera persistence.

Support:

> “That is no longer true.”

which supersedes the prior state.

This gives users a deterministic memory control path independent of extraction.

---

# 29. Search / Memory Explorer

Add a practical Memory/Activity surface.

Do not build an ontology graph visualization.

Allow the user to inspect:

* remembered current state;
* FollowUps;
* corrections;
* Evidence/provenance;
* recent significant events;
* action history.

Include:

* search/filter;
* Why?;
* Correct;
* Forget/remove where lifecycle semantics allow.

---

# 30. No Fake UI

This is mandatory.

Production UI must not display:

* hardcoded fake accounts;
* fake plugin connections;
* fake conversations;
* fake jobs;
* fake actions;
* fake Evidence;
* fake run results.

Storybook and automated tests may use fixtures.

The actual application must use real APIs and durable state.

If no data exists, show a truthful empty state.

If a capability cannot function because configuration is missing, show:

```text
Configuration required
```

not synthetic success.

---

# 31. No Fake Backend Paths

Likewise:

Do not create production endpoints that return demo results.

Fake providers/capabilities are permitted only in:

* tests;
* Storybook;
* explicitly labeled developer test harnesses.

---

# 32. Real Product Navigation

Recommended primary navigation:

```text
Chat
Jobs
Accounts
Plugins
Memory
Activity
Settings
```

Operations/admin surfaces belong under Settings/Admin, not as the home product experience.

The default route should be Chat or a useful home/attention surface.

---

# 33. Home / Attention

Optional but recommended.

A simple Home may show:

* pending action approvals;
* failed Jobs;
* account connection issues;
* unresolved FollowUp conflicts;
* recent relevant changes.

Do not overbuild dashboards.

---

# 34. API Contracts

Create versioned API contracts for:

```text
/conversations
/messages
/chat
/accounts
/plugins
/capabilities
/jobs
/job-runs
/actions
/approvals
/memory
/evidence
/followups
```

Follow existing API conventions.

Do not expose internal persistence entities directly when a product DTO is more appropriate.

---

# 35. Streaming

Chat SHOULD support streamed assistant output if the configured model supports it.

Capability execution should produce structured Chat events such as:

```text
thinking/status summary
capability requested
approval required
capability running
capability result
final answer
```

Do not stream hidden chain-of-thought.

---

# 36. Long-Running Chat Tasks

If a chat request takes longer than one HTTP request lifecycle:

* convert it into a durable execution/workflow;
* persist progress;
* allow the user to leave;
* show current status;
* continue/recover after restart.

Do not keep long work alive only in an HTTP request or model context.

---

# 37. Product Notifications

R2 may provide in-app notifications for:

* pending action approval;
* Job failure;
* account reauthentication;
* Job completion;
* FollowUp conflict.

External push/email notifications are not mandatory unless a connected plugin provides them.

---

# 38. Plugin Security Boundary

Plugins are not trusted merely because installed.

A plugin may declare capabilities.

Tessera still controls:

* credentials;
* context disclosure;
* network egress;
* policy;
* execution;
* audit.

No plugin gets raw global database access.

---

# 39. Plugin Versioning

Capability executions must record:

```text
plugin_id
plugin_version
capability_id
capability_version
```

A plugin update must not silently reinterpret historical action receipts.

---

# 40. Plugin Configuration

Use typed schemas.

Do not allow arbitrary unvalidated JSON blobs where a clear schema exists.

Secrets must be written to credential custody.

Non-secret configuration may live in product persistence.

---

# 41. Account / Plugin Relationship

A Plugin defines an integration.

An Account represents a user's authorization/configuration for that integration.

Example conceptually:

```text
Google Plugin
   ├── Account A
   └── Account B

GitHub Plugin
   └── Account C
```

A capability call specifies the account binding when required.

---

# 42. Multiple Accounts

Design Accounts for multiple instances of the same provider from day one.

Do not assume:

```text
one provider == one account
```

The user may have:

* personal account;
* work account;
* household/shared account.

Chat and Jobs must be able to select explicitly.

---

# 43. Capability Selection Safety

When multiple accounts can satisfy a capability, Tessera must not guess in a consequential action if account selection is ambiguous.

Ask or use an explicit stored preference/policy.

---

# 44. Account Connectivity UX

Connection flow should be complete.

Expected states:

```text
Choose plugin
→ Connect account
→ Authenticate/configure
→ Review permissions
→ Connection test
→ Connected
→ Capabilities available
```

Also implement:

```text
Reconnect
Disable
Disconnect
```

---

# 45. Product Settings

At minimum:

* default model;
* model accounts;
* execution approval defaults;
* timezone;
* account/plugin management;
* memory controls;
* data retention summary;
* developer/debug mode if appropriate.

---

# 46. Employee / Subagent Team

Use real subagents when available.

The manager owns architecture and integration.

---

## Employee A — R1 Baseline Verifier

Verify R0/R1 remains green.

No product changes until verified.

Deliver:

`docs/tessera/r2/R1_BASELINE.md`

---

## Employee B — Product Architecture Lead

Own the R2 integrated product architecture.

Ensure Chat, Jobs, Accounts, Plugins, Memory, and Execution use one substrate rather than six disconnected systems.

Deliver:

`R2_PRODUCT_ARCHITECTURE.md`

---

## Employee C — Chat Engineer

Own:

* conversation persistence;
* message API;
* streaming;
* context;
* capability calls;
* approvals in Chat;
* Chat UX.

Must not own Account credential code.

---

## Employee D — Model Provider Engineer

Own:

* model adapter production implementation;
* provider settings;
* connection validation;
* streaming;
* structured tool/capability requests;
* model errors.

Must preserve provider-neutral canonical state.

---

## Employee E — Account Connectivity Engineer

Own:

* ConnectedAccount;
* auth/configuration flow;
* account health;
* connection lifecycle;
* credential references;
* Accounts UI.

---

## Employee F — Plugin SDK Engineer

Own:

* plugin manifest;
* loading;
* validation;
* capability registration;
* plugin configuration;
* plugin lifecycle;
* Plugins UI backend.

No arbitrary remote code execution.

---

## Employee G — Initial Integration Engineer

Own at least one real external-service plugin.

Must use real implementation.

Tests may mock upstream.

Product code may not.

---

## Employee H — Job Engine Engineer

Own:

* Job persistence;
* schedules;
* scheduler;
* JobRun;
* restart recovery;
* pause/resume;
* run history.

---

## Employee I — Job Intelligence Engineer

Own:

* Chat → Job proposal;
* Job context;
* model invocation;
* capability selection;
* Job outputs.

Must not bypass policy.

---

## Employee J — Execution & Approval Engineer

Own:

* R0 Action integration;
* policy;
* pending approvals;
* exact payload binding;
* capability dispatch;
* verification.

Converge duplicated authorization mechanisms where the existing architecture requires it.

---

## Employee K — Memory / Context Engineer

Own:

* conversation vs durable memory boundary;
* Remember This;
* correction;
* context retrieval;
* FollowUp integration;
* Memory APIs.

Do not add vector/graph by default.

---

## Employee L — Product UI Lead

Own real product navigation and integrated UX:

```text
Chat
Jobs
Accounts
Plugins
Memory
Activity
Settings
```

No mocked production pages.

---

## Employee M — Reliability Engineer

Build:

* restart tests;
* scheduler tests;
* capability failure tests;
* account expiry tests;
* Chat persistence tests;
* action replay tests;
* plugin-version tests.

---

## Employee N — Security Adversary

Attack the integrated product.

Required attack areas:

* prompt injection;
* capability escalation;
* plugin manifest manipulation;
* cross-account access;
* cross-user access;
* approval replay;
* payload replacement;
* job permission escalation;
* job/account mismatch;
* credential leakage;
* SSRF;
* malicious capability results;
* model claiming authorization.

---

## Employee O — Product Adversary

Try to prove Tessera Alpha is still an engineering console rather than a usable product.

Questions:

* Can a user understand what to do without reading architecture docs?
* Can they connect a service?
* Can they actually chat?
* Can Chat do something useful?
* Can they create a Job naturally?
* Does the Job run later?
* Does memory matter?
* Are approval flows comprehensible?
* Is Plugins a real product mechanism or just manifests?
* Are empty states honest?
* Are there any mock/demo paths pretending to work?

Fix product blockers.

---

## Employee P — Architecture Adversary

Attack:

* duplicated execution engines;
* duplicated persistence;
* plugin-owned state;
* model-owned memory;
* Account/provider coupling;
* Chat bypassing Context;
* Jobs bypassing Actions;
* generic framework overengineering;
* microservice creep.

---

## Employee Q — Documentation / Handoff Engineer

Produce full user/developer docs and final report.

---

# 47. Mandatory End-to-End Alpha Journeys

R2 is not complete until these work.

---

## Journey A — Configure AI and Chat

```text
Start Tessera
→ Login
→ Settings / Model Providers
→ Configure real model provider
→ Validate connection
→ Open Chat
→ Ask a normal question
→ Receive real model response
→ Restart Tessera
→ Conversation still exists
```

---

## Journey B — Remember Something

```text
Chat:
“Remember that I prefer morning appointments.”

→ Tessera proposes/persists user-asserted memory
→ Ask:
“What time of day do I prefer for appointments?”
→ Tessera answers from durable state
→ Why? shows user assertion
→ Restart
→ Ask again
→ same durable answer
```

Then:

```text
“I prefer afternoons now.”

→ old state superseded
→ new state current
→ history remains visible
```

---

## Journey C — Connect an Account

```text
Accounts
→ Add Account
→ Choose installed plugin
→ authenticate/configure
→ test connection
→ account becomes CONNECTED
→ capabilities appear
```

No fake connected state.

---

## Journey D — Read Capability From Chat

Example depends on installed plugin.

```text
User asks Chat to inspect/read something
→ model requests capability
→ Tessera validates
→ capability executes using selected Account
→ result stored/audited
→ model answers
```

---

## Journey E — External Action From Chat

Using a safe test/dogfood capability:

```text
User requests side effect
→ model proposes capability call
→ Tessera creates Action
→ user sees exact proposal
→ user approves
→ capability executes
→ verification occurs where supported
→ Chat receives result
→ Activity shows receipt
```

Do not use production-impacting data for automated verification.

---

## Journey F — Create Job From Chat

```text
User:
“Every weekday at 8, summarize my open FollowUps.”

→ Tessera creates Job proposal
→ user reviews schedule/context/capabilities
→ confirms
→ Job becomes ACTIVE
→ scheduler runs it
→ JobRun is durable
→ result appears in Job history
```

---

## Journey G — Job Survives Restart

```text
Create scheduled Job
→ restart Tessera
→ scheduler resumes
→ Job executes exactly once
→ history records run
```

---

## Journey H — Job Requires Approval

```text
Job produces external side-effect Action
→ Job becomes WAITING_FOR_APPROVAL
→ user approves
→ action executes
→ Job continues/completes
```

No silent external mutation.

---

## Journey I — Plugin Disable

```text
Disable Plugin
→ capabilities disappear from Chat/Jobs
→ existing Jobs using it become BLOCKED/DEGRADED
→ no capability execution occurs
```

---

## Journey J — Account Revocation

```text
Account authentication becomes invalid
→ status changes AUTH_REQUIRED/REVOKED
→ Chat cannot use it
→ Jobs cannot use it
→ user receives visible recovery path
```

---

# 48. R2 Acceptance Criteria

## AC-R2-01

All R0/R1 tests remain green.

## AC-R2-02

Real persistent Chat works end to end.

## AC-R2-03

At least one real model provider can be configured and used.

## AC-R2-04

Conversation history survives restart.

## AC-R2-05

Chat uses Tessera Context rather than sending only raw recent messages.

## AC-R2-06

Explicit “Remember This” creates durable user-asserted state.

## AC-R2-07

Correction supersedes old memory and survives restart.

## AC-R2-08

Why? exposes provenance of durable remembered state.

## AC-R2-09

Accounts page operates on real persisted Accounts.

## AC-R2-10

At least one actual external account integration is implemented.

If live credentials are unavailable during CI, live verification may be externally blocked, but implementation cannot be replaced with a fake.

## AC-R2-11

Credentials never enter product database/logs.

## AC-R2-12

Multiple accounts of the same type are supported architecturally.

## AC-R2-13

Plugins are versioned and validated.

## AC-R2-14

Production plugin list contains real installed plugins, not fake fixtures.

## AC-R2-15

Capabilities become available only when plugin/account/policy requirements are satisfied.

## AC-R2-16

Chat can execute a real READ_ONLY capability.

## AC-R2-17

Chat side effects create durable Action proposals.

## AC-R2-18

External side effects require exact approval by default.

## AC-R2-19

Approval payload/account/target substitution fails.

## AC-R2-20

Action replay fails.

## AC-R2-21

Capability result becomes available to Chat without allowing the capability to directly mutate Chat state.

## AC-R2-22

Jobs can be created from UI.

## AC-R2-23

Jobs can be created from Chat through a reviewed structured proposal.

## AC-R2-24

One-time Jobs work.

## AC-R2-25

Recurring Jobs work.

## AC-R2-26

Jobs survive restart.

## AC-R2-27

Scheduler does not double-run a Job after restart.

## AC-R2-28

Job run history is durable.

## AC-R2-29

A Job's Account grants are enforced.

## AC-R2-30

A Job's capability grants are enforced.

## AC-R2-31

A Job cannot escalate permissions using model output.

## AC-R2-32

Side-effecting Job transitions to WAITING_FOR_APPROVAL when policy requires.

## AC-R2-33

Disabling a Plugin prevents future capability execution.

## AC-R2-34

Revoking an Account prevents future capability execution.

## AC-R2-35

Chat gracefully handles model outage.

## AC-R2-36

Jobs gracefully handle model outage.

## AC-R2-37

Capability timeouts produce durable recoverable state.

## AC-R2-38

UI has no production mock data.

## AC-R2-39

Production backend has no fake provider success path.

## AC-R2-40

Empty states are truthful.

## AC-R2-41

Product can be used entirely from main navigation without an operator console.

## AC-R2-42

Security adversary finds no unresolved Critical/High issue.

## AC-R2-43

Architecture adversary finds no unresolved Critical/High issue.

## AC-R2-44

Product adversary finds no unresolved Critical/High usability blocker.

## AC-R2-45

Backend, frontend, Playwright, migration, security, and repository gates all pass.

## AC-R2-46

End-to-end Alpha Journeys A–J are documented with pass/block status.

---

# 49. No Corner-Cutting Rules

Do not call R2 complete with:

* a Chat screen backed by fixtures;
* a Jobs screen that doesn't schedule;
* an Accounts page that only saves metadata;
* a Plugins screen that only reads manifests;
* a capability button that bypasses policy;
* a fake connected account;
* hard-coded model output;
* in-memory scheduler;
* fake successful capability responses in production;
* mock Evidence displayed as real data;
* Action approval that only sets `confirm=true`;
* background jobs that disappear after restart.

These are blockers.

---

# 50. Data Migration / Compatibility

Preserve R0/R1 databases through additive migrations.

Do not throw away existing accepted FollowUps, Evidence, Assertions, Actions, or provenance.

R2 schema changes must have migration tests.

---

# 51. Development Environment

Provide a local development setup where the human owner can run Tessera.

Required:

```text
backend
web
database
scheduler
configured model provider
plugin loader
```

Use existing repository infrastructure where possible.

Avoid introducing Kubernetes as a requirement for local dogfood if normal local execution suffices.

---

# 52. Secrets Configuration

Provide documented configuration for external credentials.

Use:

* environment variables;
* existing secret store;
* configured credential custody.

Never commit secrets.

Provide `.env.example` only with names/placeholders if repository convention permits.

---

# 53. Dogfood Mode

Create an honest dogfood mode.

Dogfood mode is the real product connected to the user's configured services.

It is NOT demo-fixture mode.

Any special debugging UI must be clearly labeled.

---

# 54. Observability

A user/developer should be able to trace:

```text
conversation
→ context
→ model
→ capability request
→ policy
→ approval
→ action
→ provider result
→ verification
→ evidence
→ response
```

For Jobs:

```text
job
→ run
→ context
→ model
→ capability
→ action
→ output
```

Do not expose hidden chain-of-thought.

---

# 55. Required R2 Documents

Create:

```text
docs/tessera/r2/
  R1_BASELINE.md
  R2_PRODUCT_SPEC.md
  R2_PRODUCT_ARCHITECTURE.md
  CHAT_MODEL.md
  MODEL_PROVIDER_MODEL.md
  ACCOUNT_MODEL.md
  PLUGIN_SDK.md
  CAPABILITY_RUNTIME.md
  JOB_MODEL.md
  JOB_SCHEDULER.md
  MEMORY_IN_CHAT.md
  ACTION_APPROVAL_UX.md
  R2_TEST_MATRIX.md
  R2_SECURITY_MODEL.md
  ADVERSARIAL_R2_PRODUCT_REVIEW.md
  ADVERSARIAL_R2_ARCHITECTURE_REVIEW.md
  ADVERSARIAL_R2_SECURITY_REVIEW.md
  R2_DECISION_LOG.md
  R2_REPORT.md
```

---

# 56. R2 Final Report

`R2_REPORT.md` must include:

```text
Status

What can a user actually do now?

Chat:
PASS/BLOCKED

Real model:
PASS/BLOCKED

Accounts:
PASS/BLOCKED

Real connected service:
PASS/BLOCKED

Plugins:
PASS/BLOCKED

Real read capability:
PASS/BLOCKED

Real side-effect capability:
PASS/BLOCKED

Jobs:
PASS/BLOCKED

Recurring scheduler:
PASS/BLOCKED

Restart recovery:
PASS/BLOCKED

Memory:
PASS/BLOCKED

Correction/Why:
PASS/BLOCKED

Approvals:
PASS/BLOCKED

Adversarial product:
PASS/FAIL

Adversarial security:
PASS/FAIL

Adversarial architecture:
PASS/FAIL

Full gate results

External configuration still required

Known limitations

Next recommended phase
```

Do not write “COMPLETE” while major product capabilities are mocked or blocked by implementation.

---

# 57. R2 Definition of Done

R2 is done when Tessera has crossed this threshold:

> **I can run Tessera, configure intelligence, connect at least one real account/service, chat with it, let Chat use capabilities, create durable Jobs, inspect what Tessera remembers and why, approve consequential actions, and restart the system without losing the continuity of the experience.**

That is the Alpha.

---

# 58. What Comes After R2

Do NOT implement these simply because time remains.

After the Alpha is actually usable, the next architecture may include:

* more account connectors;
* broader plugin catalog;
* better model routing;
* semantic retrieval;
* Entity resolution;
* Situation;
* Commitments;
* personal ontology;
* proactive triggers;
* browser execution;
* trusted edge;
* iOS;
* multi-device sync.

Those are later.

First make Tessera usable.

---

# 59. Final Manager Instruction

Stop proving that Tessera could exist.

Build Tessera.

The user should be able to open it tomorrow and say:

> “I can chat with this.
> It remembers.
> I can connect things to it.
> It can use those things.
> I can give it jobs.
> I can see what it is doing.
> I control consequential actions.
> It still knows what happened after I restart it.”

No mock product paths.

No architecture theater.

No provider-specific redefinition of Tessera.

No fake autonomy.

Build the integrated Alpha.
