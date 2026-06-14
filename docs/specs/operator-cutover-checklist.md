# Morning operator checklist — action-broker cutovers

> Everything in [the implementation plan](action-broker-implementation-plan.md)
> that an operator must do **by hand**, because it needs a real secret, a
> live-egress flip in production, a medical cutover, or the noVNC worker. The
> tessera-repo code for every phase is **built, tested, and pushed**; these steps
> light it up. Do them in order; each is independently revertible.
>
> The public repo uses only the generic example names (`health-portal`, `seerr`,
> `graph-*`, `gmail`); the real provider hosts, identities, and the generic↔real
> mapping live in your **private deployment overlay**, never here.

---

## 0. Decide the merge

The work is on a feature branch (HEAD ahead of `main`). Review the diff and either
open a PR or fast-forward `main`. CI builds `ghcr.io/dragoshont/tessera:sha-<commit>`
per push; your deployment overlay's image pin is what actually deploys — bump it to
the chosen sha when you're ready. **Nothing below changes prod until you bump that
pin**, so you can land all the code first and cut over service-by-service.

---

## 1. Job A — live hand-off worker (plan §1.3–1.4) · the noVNC seeding surface

The broker side is done + fail-closed (503 until wired). To enable in-portal captcha
seeding you must stand up the **worker** per
[the worker contract](live-view-worker-contract.md):

1. Build a thin service in front of the existing browser pool's noVNC (port 6080)
   that satisfies `POST /live-view/arm` — mint a single-use, short-TTL, per-principal
   noVNC URL, return `{ liveViewUrl, targetHostname, … }`, keep the cookie harvest
   worker→KV (unchanged). **Do not** widen the worker trust zone — the cookie must
   never leave the pod.
2. Set a broker→worker bearer in `TESSERA_LIVEVIEW_WORKER_TOKEN` (env/Secret, not the
   ConfigMap).
3. In the deployment tessera config add:
   ```json
   "liveView": { "enabled": true, "workerArmUrl": "http://<worker>.default:8080/live-view/arm", "defaultTtlSeconds": 300 }
   ```
4. Reconcile, then seed one real session end-to-end from the portal's Live stage.

**Why operator:** activates the staged pool + exposes a medical seeding surface; it
needs your security call and real hands on the reCAPTCHA.

---

## 2. Mode U — medical portal credential-free (plan §2.3–2.4) · MEDICAL · most careful

The recipe shape, the sole-owner refresher, and the `RefreshOptions` gate are all
built + tested (inert until egress). The cutover is a **single coordinated commit**
because the medical portal is a live, booking-capable surface and you must not
double-own a single-use session:

1. **Author the real medical recipe** in your deployment overlay (replace the
   status-only stub), modelled exactly on
   [`grants.example.json`](../../deploy/config/grants.example.json): `egress: http`,
   the real `upstreamBaseUrl`, `cookieMap`, `read:appointments`/`read:specialties`/
   `use:book` (`use:book` step-up), `rotation.owner: tessera`, and a real `refreshSpec`.
   Keep `owner: user` on each person's binding.
2. **Switch the domain MCP to Tessera** (the medical MCP repo, *not* tessera): drop
   the direct KV read; call Tessera's egress with the forwarded user OIDC token as
   `subject_token`; feature-flag it so the live single-server path keeps working until
   proven.
3. **Flip egress on** in the same commit that retires the per-person keep-warm, so
   Tessera becomes the **sole** rotation owner (no two components rotating one
   single-use session):
   ```json
   "egress": { "enabled": true, "allowedHosts": ["<medical-api-host>"] },
   "refresh": { "enabled": true, "intervalSeconds": 1800 }
   ```
4. Reconcile; verify **both** identities end-to-end (a read for each person),
   confirm a booking steps up, confirm the awareness dashboard now shows
   `rotation: tessera`.

**Why operator:** live medical data + booking, real cookies in KV, and a
sole-ownership handoff that corrupts the session if mis-sequenced.

**Revert:** set `egress.enabled=false` + re-enable the MCP's direct path (the feature
flag) — back to today's Mode P.

---

## 3. Media action broker (plan §3.4) · lower risk · your own apps

Recipes + grants + the service-key resolver fallback + MCP plane metadata are built +
tested ([`grants.media.example.json`](../../deploy/config/grants.media.example.json)).
To go live:

1. Put the **real API keys** in Key Vault (`seerr-api-key`, `sonarr-api-key`,
   `radarr-api-key`, `qbittorrent-session`) — these are `owner: service` (household
   keys; nobody personally holds them; never revealed to anyone).
2. Copy the media recipes + bindings + grants into your deployment tessera config
   (swap the `*.example` hosts for the real internal hostnames).
3. Add those hosts to `egress.allowedHosts` and ensure `egress.enabled=true`.
4. Wire `tessera_call` into the chat (it already surfaces the plane + step-up). Verify
   a member can `read`/`use` but not `manage`, and that `seerr_approve` / `qbt_delete`
   / any `manage:` steps up.

**Why operator:** real keys + live egress. Lower blast radius than RM (your own apps,
stable keys), but still your call to flip.

---

## 4. Gmail / Microsoft Graph (plan §4.4) · personal data · `owner: user`

The result-class primitive (metadata→preview→full-body, handles, mutation receipts)
and the connectors example are built + tested
([`grants.connectors.example.json`](../../deploy/config/grants.connectors.example.json)).
To go live:

1. **Per-data-class consent + tokens.** Register/confirm the delegated OAuth app
   (Graph: `Calendars.ReadBasic`, `Mail.ReadBasic`/`Mail.Read`/`Mail.Send`; Gmail:
   `gmail.readonly`/`gmail.send`), consent **separately per data class** (calendar
   consent must not grant mail), and store the refresh tokens in KV (`graph-<user>`,
   `gmail-<user>`) — `owner: user`. Never in browser storage.
2. Copy the connector recipes + bindings + grants into your deployment config. Keep
   mail **metadata-first** (search → handles, body by handle) and `use:mail.send` as
   draft→confirm→step-up.
3. Add `graph.microsoft.com` / `gmail.googleapis.com` to `egress.allowedHosts`;
   `egress.enabled=true`. For Graph token refresh, point the recipe's `refreshSpec`
   at the tenant token endpoint and let the Mode U refresher own it.
4. Verify: a mail search returns metadata only; reading a body needs a handle;
   sending steps up; calendar access can't reach mail.

**Why operator:** personal-data consent + real tokens + live egress.

---

## Cross-cutting (already built; surface when you wire the above)

- **Consent receipts** (`ConsentReceipt`, Core) — per `(principal, target, data
  class)`; calendar consent never satisfies mail. Wire to the connector consent flow.
- **Guardian/dependent** (`GuardianRelationships`, Core) — a guardian who seeded an
  `owner: dependent` binding may act-as that dependent; derived from bindings, no new
  store. Use if/when you add a child's account.
- **Step-up / sudo UX** — the PDP already returns `StepUp` for every `manage:` /
  flagged action; the portal + chat need the re-auth prompt UX to honour it
  (ADR 0016 cross-cutting). This is the one remaining UI build before destructive /
  medical actions are click-safe.
- **Reveal path for `owner: user`** — deliberately **not** built (off by default). Add
  only if a real need appears (owner-only · step-up · auto-redact · audited · never to
  an agent).
