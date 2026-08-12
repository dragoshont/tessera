# Tournament of Options

## Option A - Minimal Safe Fix

Wrap current provider switches behind a Broker factory. Low immediate diff and test burden, but Broker still owns provider identities and schemas and no actual MCP protocol is introduced. Rejected as a symptomatic patch.

## Option B - Module Extraction Without MCP

Extend `Tessera.Plugin.Abstractions`, add an optional module loader, and isolate current code in `Tessera.Plugins.*`. This corrects compile dependencies but would falsely call wrappers MCP-backed and would skip mandated reuse discovery. Rejected.

## Option C - MCP-First Reuse and Isolated Fallbacks

Complete local/public reuse discovery, implement a real provider-neutral MCP client runtime for the transports selected integrations need, and project discovered tools through stable Tessera capability/risk overlays. Reuse the existing local RM MCP if its source, license, auth, isolation, and action-token boundary pass review. Use a self-hosted safe Gmail MCP if one passes; otherwise keep the official Google API implementation as an isolated first-party plugin. Prefer the official GitHub MCP when its deployment/auth/tool coverage fits; otherwise extract the existing bounded REST implementation into an isolated plugin. Highest migration and test burden, but it satisfies the mandate, preserves portable integration replacement, and concentrates Tessera on trust semantics. Selected.

## Option D - Defer / Ask More

Document the violation and postpone extraction. No immediate regression risk, but no acceptance criterion is met and recurrence remains certain. Rejected.

## Decision Matrix

| Option | Contract fit | Risk | Durability | Verification burden | Result |
|---|---|---|---|---|---|
| Broker wrapper | Fail | Medium | None | Low | Lose |
| Module extraction without MCP | Partial | Medium | Partial | Medium | Lose |
| MCP-first reuse plus isolated fallbacks | Pass | Medium-high | High | High | Win |
| Defer | Fail | Low now | None | None | Lose |

## Winner

MCP-first reuse plus isolated fallbacks. It uses the accepted abstraction and execution coordinator, evaluates dependencies from evidence rather than assuming none, and requires the test-MCP replacement proof before claiming portability.
