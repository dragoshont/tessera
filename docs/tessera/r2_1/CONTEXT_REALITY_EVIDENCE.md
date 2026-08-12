# Context Reality Evidence

## Scope

Evidence is anonymized and derived from deterministic integration tests; no prompt, hidden reasoning, credential, or personal data is included.

## Observed Envelope

A Chat/Job request builds Context from current owner-scoped Assertions only. Example shape:

```text
Current fact: user appointment.preference = morning
Provenance: evidence:user-explicit
Sensitivity: Confidential
Request: bounded independently
```

The persisted context snapshot contains:

- snapshot reference;
- selected provenance references;
- omission count;
- selected sensitivity classes.

It does not contain assembled prompt text.

## Controls Verified

- current accepted Memory can be selected;
- superseded/rejected Memory is excluded from current state;
- `includeMemory=false` removes Job Memory candidates;
- context uses allowed sensitivity classes and size budget;
- conversation/Job capability and Account lists come from explicit grants;
- provider tool results are quoted as untrusted data;
- global memory/account dumps are not assembled.

## Live Evidence

A real-model context influence measurement remains `BLOCKED_EXTERNAL`. The live checklist asks the model about an explicitly remembered preference after restart and records only the user-visible answer plus Memory Why provenance.