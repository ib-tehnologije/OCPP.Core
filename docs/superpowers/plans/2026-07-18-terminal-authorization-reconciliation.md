# Terminal Authorization Reconciliation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Release newly stranded manual-capture authorizations for strictly eligible terminal no-charge reservations without touching historical rows, active sessions, charged payments, invoices, or ambiguous provider state.

**Architecture:** Add nullable prevention state plus an append-only attempt audit table, then implement one Stripe-backed reconciler shared by late webhooks and the existing cleanup sweep. Every action re-reads provider state, verifies local/provider ownership and exclusion rules, and uses bounded persisted retry state so missing webhooks and restarts converge safely.

**Tech Stack:** .NET 8, C#, EF Core 8, SQL Server migrations, SQLite/InMemory tests, Stripe.net 50.1.0, xUnit.

## Global Constraints

- Do not backfill or arm historical terminal reservations.
- Do not access production or use real provider, customer, invoice, or deployment data.
- Do not change the below-1-kWh rule, the minimum-charge-cents guard, invoice/email policy, normal billing, or reservation concurrency semantics.
- Only `Abandoned` or `Failed` reservations explicitly armed by new code can reach provider cancellation.
- Active, charged, invoiced, missing-identifier, mismatched-ownership, and ambiguous states must never be canceled automatically.
- Store bounded sanitized provider errors separately from generic reservation terminal reasons.
- Use a proper EF migration and run the SQL Server migration metadata guard.

---

### Task 1: Persist Prevention State and Attempt Audit

**Files:**
- Create: `OCPP.Core.Database/PaymentAuthorizationReleaseAttempt.cs`
- Modify: `OCPP.Core.Database/ChargePaymentReservation.cs`
- Modify: `OCPP.Core.Database/OCPPCoreContext.cs`
- Create: `OCPP.Core.Server.Tests/PaymentAuthorizationReleasePersistenceTests.cs`
- Create: `OCPP.Core.Database/Migrations/20260718132858_AddPaymentAuthorizationReleaseReconciliation.cs`
- Create: `OCPP.Core.Database/Migrations/20260718132858_AddPaymentAuthorizationReleaseReconciliation.Designer.cs`
- Modify: `OCPP.Core.Database/Migrations/OCPPCoreContextModelSnapshot.cs`

**Interfaces:**
- Produces: `DbSet<PaymentAuthorizationReleaseAttempt> PaymentAuthorizationReleaseAttempts`.
- Produces: nullable reservation summary fields `AuthorizationReleaseState`, `AuthorizationReleaseAttemptCount`, `AuthorizationReleaseLastAttemptAtUtc`, `AuthorizationReleaseNextAttemptAtUtc`, `AuthorizationReleasedAtUtc`, and `AuthorizationReleaseLastError`.
- Produces: audit fields `PaymentAuthorizationReleaseAttemptId`, `ReservationId`, `StripePaymentIntentId`, `AttemptNumber`, `Trigger`, `StartedAtUtc`, `FinishedAtUtc`, `ProviderStatus`, `AmountCapturableCents`, `Outcome`, `ErrorCode`, `ErrorMessage`, and `NextRetryAtUtc`.

- [ ] **Step 1: Write the failing model test**

Add an xUnit test that reads `context.Model`, asserts the new entity/table exists, verifies the reservation foreign key, the unique `(ReservationId, AttemptNumber)` index, bounded string lengths, and nullable prevention fields with no default/backfill value.

- [ ] **Step 2: Run the focused test and verify RED**

Run:

```sh
LC_ALL=en_US.UTF-8 LANG=en_US.UTF-8 DOTNET_ROLL_FORWARD=Major /Users/igbenic/.dotnet/dotnet test OCPP.Core.Server.Tests/OCPP.Core.Server.Tests.csproj --filter FullyQualifiedName~PaymentAuthorizationReleasePersistenceTests
```

Expected: build or test failure because the new entity and reservation properties do not exist.

- [ ] **Step 3: Add the model and mapping**

Create the audit entity, add the nullable summary properties, add the `DbSet`, and map exact SQL Server-safe lengths, relationship, unique attempt-number index, and due-retry/history indexes. Do not assign default values that could arm old rows.

- [ ] **Step 4: Scaffold and normalize the migration**

Run:

```sh
ASPNETCORE_ENVIRONMENT=Production DOTNET_ENVIRONMENT=Production ConnectionStrings__SQLite= DOTNET_ROLL_FORWARD=Major /Users/igbenic/.dotnet/dotnet ef migrations add AddPaymentAuthorizationReleaseReconciliation --project OCPP.Core.Database --startup-project OCPP.Core.Server --context OCPPCoreContext
```

Rename the generated migration pair and update its migration attribute to the exact `20260718132858_AddPaymentAuthorizationReleaseReconciliation` filenames above. Inspect `Up`, `Down`, designer metadata, and snapshot; the migration must add nullable reservation fields and the new audit table without an update/backfill statement.

- [ ] **Step 5: Run GREEN and migration metadata checks**

Run the focused test and:

```sh
bash ./scripts/check-mssql-migration-metadata.sh
```

Expected: focused test passes and metadata guard reports success.

- [ ] **Step 6: Commit the persistence slice**

```sh
git add OCPP.Core.Database OCPP.Core.Server.Tests/PaymentAuthorizationReleasePersistenceTests.cs
git commit -m "feat: persist authorization release attempts"
```

### Task 2: Implement the Strict Provider-State Reconciler

**Files:**
- Create: `OCPP.Core.Server/Payments/PaymentAuthorizationRelease.cs`
- Create: `OCPP.Core.Server/Payments/StripePaymentCoordinator.AuthorizationRelease.cs`
- Modify: `OCPP.Core.Server/Payments/IPaymentCoordinator.cs`
- Modify: `OCPP.Core.Server/Payments/StripePaymentCoordinator.cs`
- Modify: `OCPP.Core.Server/Payments/MockStripeServices.cs`
- Create: `OCPP.Core.Server.Tests/PaymentAuthorizationReleaseTests.cs`

**Interfaces:**
- Produces: `PaymentAuthorizationReleaseResult ReconcileTerminalPaymentAuthorization(OCPPCoreContext dbContext, ChargePaymentReservation reservation, string trigger)`.
- Produces: constants for `Pending`, `InProgress`, `RetryScheduled`, `Released`, `AlreadyReleased`, `ReviewRequired`, and `PermanentFailure` plus attempt outcomes.
- Changes: `IStripePaymentIntentService.Cancel` returns the provider `PaymentIntent` so the persisted outcome records the returned status.

- [ ] **Step 1: Write failing eligibility and provider-state tests**

Cover successful `requires_capture` cancellation, already `canceled`, `succeeded`, unexpected provider status, missing identifier, missing/mismatched `reservation_id` metadata, active transaction, captured fields, submitted/external invoice evidence, unarmed historical row, repeated call after success, and sanitized provider failure persistence.

- [ ] **Step 2: Run the focused suite and verify RED**

Run:

```sh
LC_ALL=en_US.UTF-8 LANG=en_US.UTF-8 DOTNET_ROLL_FORWARD=Major /Users/igbenic/.dotnet/dotnet test OCPP.Core.Server.Tests/OCPP.Core.Server.Tests.csproj --filter FullyQualifiedName~PaymentAuthorizationReleaseTests
```

Expected: failure because the reconciliation API and state types are absent.

- [ ] **Step 3: Implement the minimum reconciler**

Implement the exact local allowlist before provider mutation, persist an attempt before the provider GET, re-read provider state, verify `Metadata["reservation_id"]`, require `requires_capture` plus positive `AmountCapturable`, cancel with `authorization_release:{reservationId}:{attemptNumber}`, and persist the returned provider status. Store only bounded sanitized error code/message fields; never copy raw response bodies.

- [ ] **Step 4: Add bounded retry classification**

Use `Maintenance:AuthorizationReleaseMaxAttempts` with default `4` and bounds `1..10`, plus `Maintenance:AuthorizationReleaseRetryBaseMinutes` with default `1` and bounds `1..60`. Treat connection, rate-limit, timeout, conflict, and 5xx provider errors as transient; schedule exponential delays capped at 24 hours. Re-read provider state on every retry.

- [ ] **Step 5: Run GREEN and existing focused payment tests**

Run the new focused suite plus `StripePaymentCoordinatorTests` and `MockStripeServicesTests`. Expected: all pass with no warnings from the test runner.

- [ ] **Step 6: Commit the reconciler slice**

```sh
git add OCPP.Core.Server/Payments OCPP.Core.Server.Tests/PaymentAuthorizationReleaseTests.cs
git commit -m "feat: reconcile terminal payment authorizations"
```

### Task 3: Trigger Reconciliation from Late and Reordered Webhooks

**Files:**
- Modify: `OCPP.Core.Server/Payments/StripePaymentCoordinator.cs`
- Modify: `OCPP.Core.Server.Tests/PaymentAuthorizationReleaseTests.cs`

**Interfaces:**
- Consumes: `ReconcileTerminalPaymentAuthorization(...)`.
- Produces: handling for `payment_intent.amount_capturable_updated` and terminal `checkout.session.completed` without changing normal pending-to-authorized behavior.

- [ ] **Step 1: Write failing webhook-order tests**

Add tests for cleanup-before-checkout webhook, capturable webhook with matching metadata, capturable-before-checkout reordering, duplicate event delivery, and normal checkout-before-cleanup. Assert only armed terminal reservations cancel and every event remains idempotently audited.

- [ ] **Step 2: Run tests and verify RED**

Expected: late terminal and amount-capturable scenarios do not invoke the reconciler on the base implementation.

- [ ] **Step 3: Add the two trigger paths**

Keep normal pending checkout behavior unchanged. After linking the PaymentIntent, call reconciliation only when the reservation is armed and terminal. Resolve the amount-capturable reservation by the stored PaymentIntent id or exact `reservation_id` metadata, then apply the same armed-terminal gate.

- [ ] **Step 4: Run GREEN and commit**

Run `PaymentAuthorizationReleaseTests` and `StripePaymentCoordinatorTests`, then commit:

```sh
git add OCPP.Core.Server/Payments/StripePaymentCoordinator.cs OCPP.Core.Server.Tests/PaymentAuthorizationReleaseTests.cs
git commit -m "fix: reconcile late capturable webhooks"
```

### Task 4: Arm New Terminal Failures and Recover Them in the Existing Sweep

**Files:**
- Modify: `OCPP.Core.Server/Payments/PaymentReservationCleanupService.cs`
- Modify: `OCPP.Core.Server/Payments/StripePaymentCoordinator.cs`
- Modify: `OCPP.Core.Server.Tests/PaymentReservationCleanupServiceTests.cs`
- Modify: `OCPP.Core.Server.Tests/PaymentAuthorizationReleaseTests.cs`

**Interfaces:**
- Consumes: persisted summary state and coordinator reconciliation method.
- Produces: prevention arming only for newly abandoned cleanup rows and no-charge/minimum-capture release failures; due retry sweep processing for `Pending`, `InProgress`, and `RetryScheduled`.

- [ ] **Step 1: Write failing sweep and restart tests**

Cover stale cleanup arming, preserved detailed error, immediate successful release, transient retry scheduling, a second service/context completing the retry after restart, repeated sweep no-op after success, missing webhook recovery, retry exhaustion, and an unarmed historical row remaining untouched.

- [ ] **Step 2: Run tests and verify RED**

Run `PaymentReservationCleanupServiceTests` and the release tests. Expected: no arming or due-retry scan exists yet.

- [ ] **Step 3: Arm only new prevention cases**

When cleanup transitions a new stale row to `Abandoned`, set state `Pending` before reconciliation. Preserve an existing specific `LastError`; keep the generic cleanup reason in `FailureMessage` when appropriate. In `CompleteReservation`, arm release only when a provider exception occurs on a path already determined to be no-charge or below the configured minimum; paid capture failures must remain reviewable and must not be armed for release.

- [ ] **Step 4: Add due retry/restart recovery to the existing sweep**

Load only armed rows whose next attempt is due, save terminal transitions before provider calls, call the shared reconciler, and save results. The sweep must run even when no stale/start/disconnect work exists.

- [ ] **Step 5: Run GREEN and commit**

Run both focused suites and commit:

```sh
git add OCPP.Core.Server/Payments OCPP.Core.Server.Tests
git commit -m "fix: retry stranded authorization release"
```

### Task 5: Document the Public-Safe Operational Contract

**Files:**
- Modify: `docs/features.md`
- Modify: `docs/operations.md`
- Modify: `docs/maintenance.md`
- Modify: `docs/architecture.md`

**Interfaces:**
- Produces: public documentation for triggers, strict exclusions, audit persistence, retry configuration, migration impact, and prevention-only/no-backfill rollout.

- [ ] **Step 1: Update the four focused docs**

Describe generic manual-capture reconciliation only. Do not mention clients, private evidence, historical counts, deployment hosts, credentials, or private identifiers.

- [ ] **Step 2: Run documentation safety and diff checks**

```sh
rg -n "REQ-|CODEX-|Tehnoline|evcharge|Drive|production host|customer identifier" docs OCPP.Core.Server OCPP.Core.Database OCPP.Core.Server.Tests
git diff --check
```

Expected: no newly introduced private context and no whitespace errors.

- [ ] **Step 3: Commit documentation**

```sh
git add docs
git commit -m "docs: explain authorization release recovery"
```

### Task 6: Verify, Review, and Publish

**Files:**
- Verify all changed files from Tasks 1-5.

**Interfaces:**
- Produces: exact verified branch head and a draft GitHub PR labeled `codex` and `codex-automation`.

- [ ] **Step 1: Run focused and full validation**

```sh
bash ./scripts/check-mssql-migration-metadata.sh
LC_ALL=en_US.UTF-8 LANG=en_US.UTF-8 DOTNET_ROLL_FORWARD=Major /Users/igbenic/.dotnet/dotnet test OCPP.Core.Server.Tests/OCPP.Core.Server.Tests.csproj --filter "FullyQualifiedName~PaymentAuthorizationRelease|FullyQualifiedName~PaymentReservationCleanupServiceTests|FullyQualifiedName~StripePaymentCoordinatorTests"
LC_ALL=en_US.UTF-8 LANG=en_US.UTF-8 DOTNET_ROLL_FORWARD=Major /Users/igbenic/.dotnet/dotnet test OCPP.Core.Server.Tests/OCPP.Core.Server.Tests.csproj --configuration Release
DOTNET_ROLL_FORWARD=Major /Users/igbenic/.dotnet/dotnet build OCPP.Core.sln --configuration Release
python3 Simulators/run_local_regression.py
git diff origin/main...HEAD --check
```

Expected: all request-relevant tests/checks pass. Record any verified base-only failure separately; do not hide it.

- [ ] **Step 2: Review scope and safety**

Inspect `git diff --stat`, `git diff origin/main...HEAD`, migration SQL shape, test names, public docs, `git status`, and repository paths. Confirm no production access, secret/config mutation, real identifiers, historical remediation, deployment, client notification, or unrelated repository change occurred.

- [ ] **Step 3: Push and open a draft PR**

```sh
git push -u origin req/2026-07-18-005-terminal-authorization-reconciliation
gh pr create --draft --base main --head req/2026-07-18-005-terminal-authorization-reconciliation --title "Prevent stranded terminal payment authorizations" --body-file /tmp/req-2026-07-18-005-pr.md
gh pr edit --add-label codex --add-label codex-automation
```

Use a public-safe PR body containing the behavior, migration/config impact, tests, rollback, and explicit statement that historical rows are not armed or processed.
