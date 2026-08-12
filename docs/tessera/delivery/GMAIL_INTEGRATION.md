# Gmail Integration

Implemented:

- owner-bound OAuth state and PKCE;
- fixed Google authorize/token/revoke endpoints;
- refresh-token custody, expiry tracking, hosted refresh, rotated access-token persistence, `AUTH_REQUIRED` on rejected grants;
- identity, metadata search, bounded message/thread reads, labels;
- structural MIME parsing, text-only HTML conversion, no remote image fetch, attachment metadata only;
- draft create/update and message send using deterministic RFC `Message-ID` values;
- exact one-use Actions, payload-change/replay protection, provider read-back, unknown-outcome reconciliation;
- restart-safe Gmail History cursor, five-page/500-change bounds, 30-day/25-message initial window, duplicate-safe hash-only Evidence;
- Chat read/write proposals and read-only/draft-only Job tools; Jobs never receive a send tool;
- provider revoke plus guaranteed local credential deletion on disconnect.

The UI opens Google OAuth without asking for a token and displays scopes/health. Real OAuth, mailbox read, draft, and approved safe send remain external checkpoints after deployment.