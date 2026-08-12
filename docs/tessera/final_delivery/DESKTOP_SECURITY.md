# Desktop Security

Release settings: `nodeIntegration=false`, `contextIsolation=true`, `sandbox=true`, `webSecurity=true`, WebView disabled, downloads/permissions/popups/navigation denied by default.

Release fuses disable RunAsNode, NODE_OPTIONS, CLI inspect and file-protocol privileges; cookie encryption, ASAR integrity and ASAR-only loading are enabled. CSP is response-header enforced. IPC validates the exact main frame and `app://tessera` origin. OIDC tokens never enter renderer storage and are encrypted through macOS `safeStorage` in a bounded, atomic, mode-0600 file.

The packaged `.app` is secret-scanned and ad-hoc signed for local dogfood. Apple distribution signing/notarization remains an external credential checkpoint.