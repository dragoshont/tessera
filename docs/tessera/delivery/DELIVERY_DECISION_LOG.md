# Delivery Decision Log

1. Direct official Gmail REST was selected over opaque/unlicensed MCP reuse.
2. Gmail sync retains a cursor and hash-only observations, not mailbox bodies.
3. Draft and send are both approval-bound; Jobs cannot send.
4. Existing isolated RM MCP deployments are reused; no duplicate browser worker is built.
5. RM MCP gained minimal owner identity and non-mutating booking-price preflight.
6. Tessera's Action gate overrides the connector's ungated mutation preference.
7. Reschedule uses the provider's `old_appointment_id` combined path.
8. RM and LiteLLM private HTTP are operator-fixed routes, never arbitrary URL exceptions.
9. Chat includes bounded quoted history; prior text cannot grant authority.
10. SQLite stays one-writer/one-replica with `Recreate` rollout and online verified backup.
11. Infrastructure remains plan-only; no GitOps push/reconcile was performed.