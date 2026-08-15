# Guarded financial recovery implementation plan

**Goal:** Add an explicit, dry-run-first recovery command for terminal settlement, authorization release, and duplicate-safe invoice reconciliation without touching unlisted rows.

**Architecture:** A small console project orchestrates pure/evidence-driven assessors and existing payment/provider services. Shared settlement calculation keeps normal completion and recovery consistent. Invoice submission gains a deterministic local uniqueness boundary and lookup-before-create state machine.

**Tech stack:** .NET 8, Entity Framework Core, SQL Server migrations, xUnit.

## Task 1: Pin the safety contract with failing tests

**Files:**

- Create: `OCPP.Core.Server.Tests/FinancialRecoveryManifestTests.cs`
- Create: `OCPP.Core.Server.Tests/FinancialRecoverySettlementTests.cs`
- Create: `OCPP.Core.Server.Tests/FinancialRecoveryAuthorizationTests.cs`
- Modify: `OCPP.Core.Server.Tests/InvoiceIntegrationServiceTests.cs`

Add behavior tests proving default dry-run, execute digest confirmation, exact allowlisting, settlement fail-closed boundaries, authorization refusal boundaries, local invoice preflight, provider lookup-before-create, and provider-unknown handling. Run the focused tests and confirm they fail because the production recovery types and behaviors do not yet exist.

## Task 2: Add the recovery manifest and settlement assessor

**Files:**

- Create: `OCPP.Core.Server/Payments/Recovery/FinancialRecoveryManifest.cs`
- Create: `OCPP.Core.Server/Payments/Recovery/FinancialRecoverySettlementAssessor.cs`
- Modify: `OCPP.Core.Server/Payments/StripePaymentCoordinator.cs`
- Modify: `OCPP.Core.Server.Tests/StripePaymentCoordinatorTests.cs`

Implement strict manifest validation and digest calculation. Extract the normal completion calculation behind a shared internal service and use it from both normal completion and the recovery assessor. Make the assessor return a complete decision or blocking reasons, never a partial amount. Run focused settlement and coordinator tests to green.

## Task 3: Add guarded authorization recovery

**Files:**

- Create: `OCPP.Core.Server/Payments/Recovery/FinancialRecoveryAuthorizationService.cs`
- Modify: `OCPP.Core.Server.Tests/FinancialRecoveryAuthorizationTests.cs`

Implement local eligibility assessment. Dry-run must not arm or invoke the coordinator. Execute must reload, reassess, arm only an eligible row, persist the arm, and delegate to the existing authorization-release state machine. Run the focused tests to green.

## Task 4: Add durable invoice uniqueness and lookup

**Files:**

- Modify: `OCPP.Core.Database/InvoiceSubmissionLog.cs`
- Modify: `OCPP.Core.Database/OCPPCoreContext.cs`
- Modify: `OCPP.Core.Server/Payments/Invoices/InvoiceIntegrationService.cs`
- Modify: `OCPP.Core.Server/Payments/Invoices/ERacuni/IERacuniApiClient.cs`
- Modify: `OCPP.Core.Server/Payments/Invoices/ERacuni/ERacuniApiClient.cs`
- Modify: `OCPP.Core.Server.Tests/InvoiceIntegrationServiceTests.cs`
- Modify: `OCPP.Core.Server.Tests/ERacuniApiClientTests.cs`

Add deterministic `SubmissionKey`, a local acquisition/preflight state machine, and an exact provider-reference lookup result with `Found`, `NotFound`, and `Unknown`. Only a definitive not-found decision may reach create; exceptions after the provider boundary persist `ProviderUnknown`. Run focused tests to green.

## Task 5: Generate and inspect the database migration

**Files:**

- Create through repository command: `OCPP.Core.Database/Migrations/*_AddInvoiceSubmissionIdempotency.cs`
- Create through repository command: matching designer file
- Modify through repository command: `OCPP.Core.Database/Migrations/OCPPCoreContextModelSnapshot.cs`

Run `make add-migration NAME=AddInvoiceSubmissionIdempotency` using the repository-supported EF command. Inspect `Up`, `Down`, designer metadata, and the snapshot. The migration must add a nullable bounded column and filtered unique index, with no data update or backfill. Run `make check-migration-metadata`.

## Task 6: Add the operator command

**Files:**

- Create: `OCPP.Core.Recovery/OCPP.Core.Recovery.csproj`
- Create: `OCPP.Core.Recovery/Program.cs`
- Create: `OCPP.Core.Recovery/FinancialRecoveryRunner.cs`
- Modify: `OCPP.Core.sln`
- Create: `examples/financial-recovery-manifest.example.json`
- Create: `OCPP.Core.Server.Tests/FinancialRecoveryRunnerTests.cs`

Compose the three operations behind a runner that is dry-run by default. Require `--execute --confirm-sha256 <exact digest>` for mutation. Reload and reassess immediately before each execute step. Emit only sanitized results and a non-zero exit code when any item is blocked or ambiguous. Test the runner with synthetic data and fake external services.

## Task 7: Document operation and preservation boundaries

**Files:**

- Modify: `docs/operations.md`
- Modify: `docs/architecture.md`
- Modify: `README.md`

Document offline manifest custody, dry-run, digest confirmation, provider-lookup uncertainty, database migration prerequisite, and the rule that this tool is never an automatic background job or production deployment mechanism.

## Task 8: Verify the whole change

Run focused tests after each task, then:

```bash
LANG=en_US.UTF-8 LC_ALL=en_US.UTF-8 DOTNET_ROLL_FORWARD=Major dotnet test OCPP.Core.sln
LANG=en_US.UTF-8 LC_ALL=en_US.UTF-8 DOTNET_ROLL_FORWARD=Major dotnet build OCPP.Core.sln --no-restore
make check-migration-metadata
git diff --check
```

Review the whole branch diff for private identifiers, secrets, accidental production operations, unsupported migration edits, and preservation-boundary violations. Verify the command only against synthetic/local fixtures.

## Task 9: Publish for independent QA

Commit in coherent Croatian-language commits, push the exact branch, and open a draft pull request. Verify remote branch/head durability and record checks. Retain the isolated implementation worktree intentionally with no running processes. Do not deploy, migrate a live database, run a live provider operation, notify a client, or execute a real recovery manifest.
