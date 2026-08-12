# Tessera Alpha User Guide

## Start And Sign In

Run `./scripts/devloop/up`, open `http://localhost:8080`, and use the local developer card. Chat is the first product view.

## Configure A Model

If Chat says Model configuration required, choose Open settings. Enter endpoint, model name, and credential, then Save and validate. The credential is write-only. Choose the default model when multiple profiles exist.

## Chat

Create a conversation and send a message. Responses stream while running and become durable after validation. Stop cancels a running generation. Failed/stopped turns retain the user message and offer retry.

## Memory

Use Remember last message or the Memory page for explicit durable state. Open Why / Correct to inspect Evidence and history. Stop using excludes current context; it does not claim to erase copies already present in backups.

## Accounts And Plugins

Plugins shows installed integration types and current readiness. Accounts is where provider identity, repository scope, and credentials are configured. Test connection verifies identity/health. Revoke blocks future Chat/Job use and starts credential cleanup.

## Jobs

Create a one-time/daily/weekday Job. Jobs use the configured default model. Use Job access to grant exact Accounts/capabilities; external communication is separate. Run now, pause/resume, and inspect run history/output/Evidence/Actions.

## Actions

Approval cards show consequence, target, Account, capability version, expiry, and exact payload. Edit creates a new proposal; approval never covers changed data. Approve once or cancel.

## Recovery

Follow the on-screen recovery message. Tessera preserves durable message, Job, and Action history when providers fail. Account auth failures require reconnect/revoke-and-connect; plugin failures require enablement.

## Backup

Run `./scripts/devloop/backup`. Restore to an isolated path with `./scripts/devloop/restore BACKUP`. See the operator guide before replacing active state.