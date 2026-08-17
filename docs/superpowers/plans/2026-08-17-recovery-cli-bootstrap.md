# Recovery CLI Bootstrap Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the standalone financial-recovery command reach dry-run assessment by registering the configuration instance it already builds.

**Architecture:** Keep the existing console entry point and server service registrations. Add executable-level integration coverage around `Program.Main`, then add one DI registration before resolving the existing recovery dependencies.

**Tech Stack:** .NET 8, C#, xUnit, EF Core SQLite, Microsoft.Extensions.DependencyInjection

## Global Constraints

- Dry-run remains the default.
- Mutation still requires `--execute` plus the exact `--confirm-sha256` digest.
- Tests use only synthetic manifests and isolated local SQLite databases.
- No provider, payment, invoice, production database, schema, secret, or configuration change is allowed.
- Public repository files remain collaborator-neutral and contain no private operational context.

---

### Task 1: Program-Level Dry-Run Regressions

**Files:**
- Create: `OCPP.Core.Server.Tests/FinancialRecoveryProgramTests.cs`
- Modify: `OCPP.Core.Server.Tests/OCPP.Core.Server.Tests.csproj`
- Modify: `OCPP.Core.Recovery/OCPP.Core.Recovery.csproj`

**Interfaces:**
- Consumes: `OCPP.Core.Recovery.Program.Main(string[] args)` and `OCPPCoreContext`.
- Produces: two executable-level tests plus test-only assembly visibility for the internal entry point.

- [ ] **Step 1: Add the recovery executable as a test project reference**

Add this project reference to the existing test-project `ItemGroup`:

```xml
<ProjectReference Include="..\OCPP.Core.Recovery\OCPP.Core.Recovery.csproj" />
```

Add test-only internal visibility to the recovery project:

```xml
<ItemGroup>
  <InternalsVisibleTo Include="OCPP.Core.Server.Tests" />
</ItemGroup>
```

- [ ] **Step 2: Write the failing empty-manifest Program test**

Create a non-parallel xUnit collection and a fixture that writes a temporary manifest, creates a temporary SQLite database, supplies local-only environment configuration, captures stdout/stderr, calls `Program.Main`, and restores all global state in `finally`.

Assert these hand-derived results for `{ "schemaVersion": 1, "entries": [] }`:

```csharp
Assert.Equal(0, result.ExitCode);
Assert.Contains("mode=dry-run manifestSha256=", result.StdOut, StringComparison.Ordinal);
Assert.DoesNotContain("Financial recovery stopped", result.StdErr, StringComparison.Ordinal);
```

- [ ] **Step 3: Write the failing synthetic invoice dry-run test**

Seed one completed, captured reservation linked to one stopped transaction whose literal billing breakdown totals the captured 300 cents. Run a one-entry `recover-invoice` manifest and assert:

```csharp
Assert.Equal(0, result.ExitCode);
Assert.Contains("operation=recover-invoice", result.StdOut, StringComparison.Ordinal);
Assert.Contains("eligible=True", result.StdOut, StringComparison.Ordinal);
Assert.Contains("outcome=DryRunEligibleProviderLookupRequired", result.StdOut, StringComparison.Ordinal);
Assert.Empty(verificationContext.InvoiceSubmissionLogs);
```

- [ ] **Step 4: Verify RED**

Run:

```sh
DOTNET_ROLL_FORWARD=Major dotnet test OCPP.Core.Server.Tests/OCPP.Core.Server.Tests.csproj --filter 'FullyQualifiedName~FinancialRecoveryProgramTests' --nologo
```

Expected: both tests fail because `Program.Main` returns `1` after service resolution cannot find `IConfiguration`.

### Task 2: Minimal Bootstrap Fix and Documentation

**Files:**
- Modify: `OCPP.Core.Recovery/Program.cs`
- Modify: `docs/operations.md`

**Interfaces:**
- Consumes: the `IConfigurationRoot` already returned by `ConfigurationBuilder.Build()`.
- Produces: an `IConfiguration` registration available to all existing service factories.

- [ ] **Step 1: Register the built configuration**

Immediately after creating the service collection, add:

```csharp
services.AddSingleton<IConfiguration>(configuration);
```

Do not change `Startup.ConfigureServices`, service lifetimes, configuration sources, or recovery logic.

- [ ] **Step 2: Verify GREEN**

Run the Program-level filter from Task 1. Expected: two passing tests, zero failures.

- [ ] **Step 3: Document the bootstrap invariant**

Add a public-safe operations note that the standalone command registers its built configuration before service resolution and that Program-level tests cover both empty-manifest and synthetic invoice dry-runs.

- [ ] **Step 4: Run focused and full validation**

```sh
DOTNET_ROLL_FORWARD=Major dotnet test OCPP.Core.Server.Tests/OCPP.Core.Server.Tests.csproj --filter 'FullyQualifiedName~FinancialRecovery' --nologo
DOTNET_ROLL_FORWARD=Major dotnet test OCPP.Core.Server.Tests/OCPP.Core.Server.Tests.csproj --nologo
dotnet build OCPP.Core.sln --configuration Release --nologo
bash ./scripts/check-mssql-migration-metadata.sh
git diff --check
```

Expected: every command exits zero; no migration or model snapshot file changes.

- [ ] **Step 5: Commit the bounded change**

Stage only the new tests, project references, one-line bootstrap fix, operations note, design, and plan. Commit with a Croatian message:

```sh
git commit -m "Ispravi pokretanje alata za financijski oporavak"
```
