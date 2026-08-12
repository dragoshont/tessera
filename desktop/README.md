# Tessera Desktop

Secure Electron shell for the shared Tessera React product UI. Canonical state, Jobs, Accounts, plugins, Actions, and Memory remain in the deployed Tessera backend.

```bash
npm install
npm test
npm run test:electron
npm run package:mac
npm run verify:package
```

The unsigned dogfood artifact is emitted under `desktop/release/`. Public distribution signing and notarization require the operator's Apple credentials.
