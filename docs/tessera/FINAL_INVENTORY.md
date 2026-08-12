# Kernel v1 Final Inventory

**Captured UTC:** `2026-08-09T22:02:19Z`
**HEAD baseline:** `723aa31`
**Source/project/test files:** 24

Generated `bin/` and `obj/` output is excluded.

```text
src/Tessera.Core/Kernel/Actions.cs
src/Tessera.Core/Kernel/Assertions.cs
src/Tessera.Core/Kernel/Capabilities.cs
src/Tessera.Core/Kernel/Context.cs
src/Tessera.Core/Kernel/DomainRecords.cs
src/Tessera.Core/Kernel/Intelligence.cs
src/Tessera.Core/Kernel/KernelValidation.cs
src/Tessera.Core/Kernel/PersistencePorts.cs
src/Tessera.Core/Kernel/PrincipalRef.cs
src/Tessera.Persistence.Sqlite/KernelMigrations.cs
src/Tessera.Persistence.Sqlite/SqliteKernelStore.Execution.cs
src/Tessera.Persistence.Sqlite/SqliteKernelStore.State.cs
src/Tessera.Persistence.Sqlite/SqliteKernelStore.cs
src/Tessera.Persistence.Sqlite/Tessera.Persistence.Sqlite.csproj
tests/Tessera.Core.Tests/Kernel/ContextCapabilityTests.cs
tests/Tessera.Core.Tests/Kernel/DomainSemanticTests.cs
tests/Tessera.Core.Tests/Kernel/PrincipalRefTests.cs
tests/Tessera.Persistence.Sqlite.Tests/KernelEndToEndTests.cs
tests/Tessera.Persistence.Sqlite.Tests/KernelTestData.cs
tests/Tessera.Persistence.Sqlite.Tests/SqliteExecutionPersistenceTests.cs
tests/Tessera.Persistence.Sqlite.Tests/SqliteMigrationTests.cs
tests/Tessera.Persistence.Sqlite.Tests/SqliteStatePersistenceTests.cs
tests/Tessera.Persistence.Sqlite.Tests/TemporaryDatabase.cs
tests/Tessera.Persistence.Sqlite.Tests/Tessera.Persistence.Sqlite.Tests.csproj
```

## Project/Test Gate Inventory

| Project | Final result |
|---|---:|
| `Tessera.Broker.Tests` | 141 passed |
| `Tessera.Core.Tests` | 329 passed |
| `Tessera.Identity.Tests` | 13 passed |
| `Tessera.Mcp.Tests` | 8 passed |
| `Tessera.Persistence.Sqlite.Tests` | 23 passed |
| `Tessera.Providers.Tests` | 77 passed |
| `Tessera.Stores.AzureKeyVault.Tests` | 8 passed |
| **Backend total** | **599 passed** |
| Web Vitest | **74 passed** |

The official aggregate backend gate also passed Kubernetes render, kubeconform (4/4), and deployment secret scanning.