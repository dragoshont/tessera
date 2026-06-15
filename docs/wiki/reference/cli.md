# Command-line interface (`tessera`)

The `tessera` command validates configuration and runs the broker.

> Source of truth: `src/Tessera.Cli/Program.cs`.

```text
tessera — secretless, identity-aware credential broker

usage:
  tessera version
  tessera validate [--config tessera.json] [--grants grants.json]
  tessera serve    [--config tessera.json] [--grants grants.json]
```

---

## Commands

### `tessera version`

Prints the version. Aliases: `--version`, `-v`.

### `tessera validate`

Loads the configuration and the policy document, checks them, and prints a summary. It
makes **no** network call and starts **no** server — safe to run anywhere.

| Flag | Meaning |
|---|---|
| `--config <path>` | Path to `tessera.json`. Omit for defaults + environment. |
| `--grants <path>` | Path to the policy document. Omit to use the config's `policy.document`. |

It prints the identity mode, the listen address, the policy default, whether OIDC
delegation is enabled, and the counts of grants / bindings / recipes. Then:

- On success: `OK — configuration is valid and fail-closed.` (exit `0`). If no grants
  are loaded, it notes that every request will be denied.
- On failure: `NOT OK — fix these:` followed by each problem (exit `1`).

The checks are the [validation rules](configuration.md#validation-rules). Use this in
CI to catch a fail-open or fail-to-start configuration before deploy.

### `tessera serve`

Builds and runs the broker. If the configuration is invalid, it prints
`refusing to serve — <reason>` and exits `1` (it never serves an invalid config).

| Flag | Meaning |
|---|---|
| `--config <path>` | Path to `tessera.json`. |
| `--grants <path>` | Path to the policy document. |

### `tessera help`

Prints usage. Aliases: `--help`, `-h`.

---

## Exit codes

| Code | Meaning |
|---|---|
| `0` | Success. |
| `1` | Validation failed, or the broker refused to serve. |
| `2` | Unknown command. |

---

## Examples

```bash
# Check a config + policy before deploying (CI-friendly):
tessera validate --config tessera.json --grants grants.json

# Run the broker:
tessera serve --config tessera.json --grants grants.json
```

---

## Where to go next

- Every config field: [Configuration reference](configuration.md).
- Run it end-to-end the first time: [Your first brokered call](../tutorials/01-your-first-brokered-call.md).
