# Tessera Mac Host

Optional, nested macOS login-item helper for the closed
`host.repo.identity@1` proof capability. Tessera Server remains canonical for
Jobs, leases, grants, Actions, artifacts, Evidence, and reconciliation.

The helper:

- keeps its P-256 private key and repository paths in helper-owned Keychain data;
- signs the canonical Host protocol and retries uncertain requests exactly;
- reads only descriptor-bound `.git/HEAD` and a closed `refs/heads/*` file;
- launches no subprocess for Host work;
- persists a mode-0600 attempt/outbox journal;
- exposes only `status`, `register`, and `unregister` to Electron.

Pairing, configuration, and repository consent are fixed native control verbs
whose JSON input is read from stdin. Never put a claim secret or repository path
in argv, a URL, a deep link, a notification, a preference, or a log.

```bash
./mac-host/scripts/checks.sh
```

Local checks and ad-hoc packaging do not prove Secure Enclave use,
`SMAppService` approval/login/reboot behavior, Developer ID signing,
notarization, Gatekeeper, sleep/wake behavior, or a real Broker journey. Those
remain physical/signed external gates.

Developer ID packaging must set `TESSERA_TEAM_IDENTIFIER` to the certificate's
10-character Apple Team ID. Package assembly then gives only the two native Host
binaries the shared `TEAMID.ro.hont.tessera.host.shared` Keychain group;
Electron receives no Keychain access-group entitlement.