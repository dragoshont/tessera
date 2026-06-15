# Your first brokered call

In this lesson you will run Tessera on your own machine, write **one** authorisation
rule, and watch Tessera make a real decision. You will see the heart of the broker —
*verify identity → check policy → resolve a credential* — working end to end.

You need **no cloud account**, **no secrets**, and **no network access** to any
provider. Everything runs locally, and the result is guaranteed.

> Time: about 15 minutes. You need the **.NET 10 SDK** and a terminal.

By the end you will have:

- a running Tessera broker on your machine,
- a policy that **allows** one action and **denies** everything else,
- a real authorisation decision you can read in the broker's output.

---

## Step 1 — Get the code and check the build

Clone the repository and confirm the broker builds:

```bash
git clone https://github.com/dragoshont/tessera.git
cd tessera
dotnet build Tessera.slnx
```

You should see `Build succeeded`. (If `dotnet` is not found, install the
[.NET 10 SDK](https://dotnet.microsoft.com/download) first.)

To run the `tessera` command from source in this lesson, we use:

```bash
dotnet run --project src/Tessera.Cli --
```

Everything after the `--` is passed to `tessera`. So `dotnet run --project
src/Tessera.Cli -- version` runs `tessera version`.

---

## Step 2 — Start from a deny-all policy and see fail-closed

Create a folder for this lesson and an **empty** policy:

```bash
mkdir -p lesson
echo '{ "grants": [], "bindings": [], "recipes": [] }' > lesson/grants.json
```

Create a development configuration. It uses `identity.mode: "dev"` (allowed only on
loopback) so you do not need a cloud identity provider:

```bash
cat > lesson/tessera.json <<'JSON'
{
  "server":   { "host": "127.0.0.1", "port": 8080 },
  "identity": { "mode": "dev", "trustDomain": "tessera.local" },
  "policy":   { "default": "deny", "document": "lesson/grants.json" },
  "audit":    { "enabled": true, "path": "-" },
  "egress":   { "enabled": false, "allowedHosts": [] }
}
JSON
```

Now validate it:

```bash
dotnet run --project src/Tessera.Cli -- validate --config lesson/tessera.json
```

You should see something like:

```text
config:  lesson/tessera.json
  identity mode : dev
  listen        : 127.0.0.1:8080
  policy default: deny
  oidc audience : unset (delegation FAILS CLOSED)
policy:  lesson/grants.json  (0 grant(s), 0 binding(s), 0 recipe(s))

OK — configuration is valid and fail-closed.
note: no grants loaded yet, so every request will be denied.
```

Read that last line. With no grants, **every** request is denied. This is *default-deny*
— the safe starting point. Tessera says *no* until you say otherwise.

---

## Step 3 — Write one rule that allows one action

Now allow exactly one thing: a built-in **self-test** caller may perform the action
`read:selftest` on a target we will call `demo`.

The self-test runs inside the broker at startup. Its caller identity is
`spiffe://<trustDomain>/selftest`. With our trust domain that is
`spiffe://tessera.local/selftest`. Write a grant for exactly that caller:

```bash
cat > lesson/grants.json <<'JSON'
{
  "grants": [
    {
      "caller": "spiffe://tessera.local/selftest",
      "target": "demo",
      "actions": ["read:selftest"]
    }
  ],
  "bindings": [],
  "recipes": []
}
JSON
```

Validate again:

```bash
dotnet run --project src/Tessera.Cli -- validate --config lesson/tessera.json
```

This time it reports `1 grant(s)`. The configuration is still fail-closed — but now one
action is allowed.

---

## Step 4 — Run the broker and watch it decide

Start the broker, and tell the startup self-test to evaluate the `demo` target:

```bash
TESSERA_SELFTEST_TARGET=demo \
  dotnet run --project src/Tessera.Cli -- serve --config lesson/tessera.json
```

The broker prints a startup banner as JSON. Find the `selftest` part. It will look like
this (formatted for reading):

```json
{
  "target": "demo",
  "effect": "allow",
  "reason": "granted: spiffe://tessera.local/selftest may read:selftest on demo",
  "credentialStatus": "absent",
  "ok": false
}
```

**Read this carefully — it is the whole point of the lesson:**

- `effect: "allow"` — the policy **allowed** the action. Your grant worked.
- `reason` — Tessera explains *why*, naming the caller, the action, and the target.
- `credentialStatus: "absent"` — there is **no credential** yet (we connected none), so
  there is nothing to inject.
- `ok: false` — the request was *allowed* but *not usable*, because the credential is
  absent.

You just watched Tessera **verify** an identity, **check** policy, and **resolve** a
credential's status — the three steps that decide every call. It made this decision
**without** calling any upstream service, and **without** any secret.

---

## Step 5 — Prove that everything else is denied

Stop the broker (`Ctrl-C`). Change the self-test target to something with **no** grant:

```bash
TESSERA_SELFTEST_TARGET=secret-thing \
  dotnet run --project src/Tessera.Cli -- serve --config lesson/tessera.json
```

Now the self-test reports:

```json
{
  "target": "secret-thing",
  "effect": "deny",
  "reason": "no grant allows spiffe://tessera.local/selftest to read:selftest on secret-thing",
  "ok": false
}
```

`deny`, because no grant allows it. This is default-deny in action: access exists only
where you granted it.

---

## Step 6 — Look at the broker's other signals

While the broker runs, open another terminal and ask it about itself:

```bash
curl -s http://127.0.0.1:8080/healthz   # → {"status":"ok"}
curl -s http://127.0.0.1:8080/status    # → store, broker-endpoint state, delegation, self-test
```

`/status` shows `broker-endpoint: fail-closed` and `delegation: fail-closed` — because in
this dev lesson we configured no caller authenticator and no egress. **This is correct.**
Deploying the broker opens nothing until you deliberately turn it on.

Stop the broker with `Ctrl-C`.

---

## What you learned

You ran a real credential broker and saw its core behaviour:

1. **Default-deny.** With no rules, everything is denied.
2. **A grant allows one precise thing.** Your grant allowed one caller, one action, one
   target — and the decision explained itself.
3. **Verify → policy → resolve.** Tessera decided without any secret and without any
   upstream call.
4. **Fail-closed by default.** Egress and the caller plane stayed closed until asked.

---

## Where to go next

You have the mental model. Now connect something real:

- Understand what you just saw, in depth: [How a call works](../explanation/how-a-call-works.md).
- Give a real workload its own identity: [Register a non-human caller](../how-to/register-a-non-human-caller.md).
- Describe a real provider: [Add a provider recipe](../how-to/add-a-provider-recipe.md).
- Turn on real calls, safely: [Enable egress safely](../how-to/enable-egress-safely.md).
