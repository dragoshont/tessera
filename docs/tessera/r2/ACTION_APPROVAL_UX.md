# R2 Action Approval UX

An approval card states the exact capability and version, target, canonical payload preview, selected account, plugin version, expiry, and consequence. Approve, Edit, and Cancel are explicit keyboard-reachable commands. Edit cancels the proposal and creates a new Action; approval never mutates a payload and never marks success.

The authorization binds owner, Action ID, payload hash, account ID, target scope, plugin/capability versions, issued/expiry timestamps, and one-use consumption. Dispatch transactionally consumes it and reserves execution. Replay, substitution, expiry, other-owner access, plugin disable, account revoke, or changed grants fails closed. The UI distinguishes pending, running, verified, failed, unknown outcome/reconciliation, and canceled; only verified provider evidence yields success.
