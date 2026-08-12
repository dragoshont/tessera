# Desktop Packaging

Version: Tessera Alpha 0.1.0, macOS arm64.

```bash
npm --prefix desktop run package:mac
npm --prefix desktop run verify:package
npm --prefix desktop run test:packaged
```

Outputs: `desktop/release/Tessera-Alpha-0.1.0-arm64.dmg` and `.zip`. Manual update replaces the app; canonical data remains server-side.