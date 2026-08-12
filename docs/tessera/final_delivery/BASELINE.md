# Final Delivery Baseline

Verified 2026-08-12. Repository candidate: branch `2.0-beta`, SQLite schema v15, Web and Desktop share the React routes and deployed API contracts.

- Backend: 769 .NET tests green.
- Web: 105 Vitest and 34 Playwright checks green.
- Desktop: Electron 43.4.0, 7 unit/security tests, development and packaged launch smoke green.
- Desktop artifact: `desktop/release/Tessera-Alpha-0.1.0-arm64.{dmg,zip}` (unsigned public-distribution identity; ad-hoc signed dogfood app).
- Live Web: `https://tessera.hont.ro`, schema v15, image digest `sha256:3545c49d…64c57`.
- RM target: two v0.5.38 deployments with exact-action authorization and MCP SDK 1.28.1; account B awaits independent authorization.
- Live LiteLLM: v1.94.0 healthy; real `claude-haiku-4.5` completion returned HTTP 200.
- Desktop Alpha 0.1.0 is installed at `/Applications/Tessera.app`; human OIDC sign-in is pending.

No production mock satisfies a final criterion.