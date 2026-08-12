# Gmail Plugin Acquisition Decision

## Decision

Implement Gmail directly with Google's supported OAuth and Gmail REST API.

## Reason

Public candidates reviewed during the run did not provide a clearly licensed, pinned package that preserved Tessera's Account, custody, Context, Action, Evidence, and Job boundaries. Google's official remote MCP remains a preview and its broader composition semantics do not replace Tessera's trust plane. ChatGPT's Gmail integration is not a portable runtime artifact and was not reverse engineered.

## Result

The curated `gmail@1.0.0` declarative plugin is pinned by SHA-256. Credentials stay in Tessera custody; provider output cannot authorize actions; email send is an exact Action.