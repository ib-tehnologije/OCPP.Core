# MessageLog Online Retention Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add disabled-by-default, dry-run-first 30-day `MessageLog` retention with bounded restart-safe deletion batches and operator accounting.

**Architecture:** A validated options object feeds a dedicated server `BackgroundService`. Each sweep fixes one UTC cutoff, assesses candidates, then repeatedly selects `(LogTime, LogId)` ordered identifiers and deletes one bounded batch per short-lived EF Core scope. Existing `LogTime` and primary-key indexes are sufficient, so the model and migrations remain unchanged.

**Tech Stack:** .NET 8, ASP.NET Core `BackgroundService`, EF Core 8, SQL Server/SQLite, xUnit.

## Global Constraints

- `Maintenance:MessageLogRetention:Enabled` defaults to `false`.
- `Maintenance:MessageLogRetention:DryRun` defaults to `true`.
- Retention defaults to exactly `30` days; only `LogTime < cutoff` is eligible.
- Retention days must remain between `1` and `36500` so cutoff arithmetic is
  representable.
- Batch size defaults to `1000` and must remain between `1` and `1000` so the
  selected-identifier delete remains below SQL Server's parameter limit.
- Cleanup interval defaults to `60` minutes and must remain between `1` and
  `1440`.
- Explicit invalid configuration fails closed without database access.
- Every deletion batch commits independently and is ordered by `LogTime`, then `LogId`.
- Never delete or mutate transactions, payments, invoices, reservations, connector state, or audit evidence.
- Never shrink database files, add infrastructure, or enable cleanup automatically.
- All committed text must remain collaborator-neutral and public-safe.
- All commit messages must be written in Croatian.

---

### Task 1: Validate retention configuration

**Files:**
- Create: `OCPP.Core.Server/Maintenance/MessageLogRetentionOptions.cs`
- Create: `OCPP.Core.Server.Tests/MessageLogRetentionOptionsTests.cs`

**Interfaces:**
- Produces: `MessageLogRetentionOptions.TryRead(IConfiguration, out MessageLogRetentionOptions, out string error)`.
- Produces: immutable properties `Enabled`, `DryRun`, `RetentionDays`, `BatchSize`, and `CleanupInterval`.

- [ ] **Step 1: Write failing default and validation tests**

Add table-driven tests with literal expected values. The production change each
test catches is either an unsafe enabled default, silent coercion of malformed
input, or acceptance of an unbounded batch.

```csharp
[Fact]
public void TryRead_UsesDisabledDryRunThirtyDayDefaults()
{
    IConfiguration configuration = new ConfigurationBuilder()
        .AddInMemoryCollection()
        .Build();

    bool valid = MessageLogRetentionOptions.TryRead(
        configuration,
        out var options,
        out string error);

    Assert.True(valid, error);
    Assert.False(options.Enabled);
    Assert.True(options.DryRun);
    Assert.Equal(30, options.RetentionDays);
    Assert.Equal(1000, options.BatchSize);
    Assert.Equal(TimeSpan.FromMinutes(60), options.CleanupInterval);
}

[Theory]
[InlineData("RetentionDays", "0")]
[InlineData("RetentionDays", "abc")]
[InlineData("RetentionDays", "36501")]
[InlineData("BatchSize", "0")]
[InlineData("BatchSize", "1001")]
[InlineData("CleanupIntervalMinutes", "0")]
[InlineData("CleanupIntervalMinutes", "1441")]
[InlineData("Enabled", "not-bool")]
public void TryRead_RejectsMalformedOrUnsafeValues(string key, string value)
{
    IConfiguration configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"Maintenance:MessageLogRetention:{key}"] = value
        })
        .Build();

    Assert.False(MessageLogRetentionOptions.TryRead(
        configuration,
        out _,
        out string error));
    Assert.Contains(key, error, StringComparison.Ordinal);
}
```

- [ ] **Step 2: Run tests and verify RED**

Run:

```bash
LANG=en_US.UTF-8 LC_ALL=en_US.UTF-8 DOTNET_ROLL_FORWARD=Major \
  dotnet test OCPP.Core.Server.Tests/OCPP.Core.Server.Tests.csproj \
  --no-restore --nologo --filter 'FullyQualifiedName~MessageLogRetentionOptionsTests'
```

Expected: compilation fails because `MessageLogRetentionOptions` does not exist.

- [ ] **Step 3: Implement strict parsing**

Create an internal immutable class under `OCPP.Core.Server.Maintenance`. Read
raw section values with `bool.TryParse`/`int.TryParse` so malformed values do
not throw or silently become defaults. Missing values use the safe defaults.
Return an error containing only the configuration key and accepted range, never
the raw value.

```csharp
internal sealed class MessageLogRetentionOptions
{
    internal const string SectionName = "Maintenance:MessageLogRetention";
    internal const int MaximumRetentionDays = 36500;
    internal const int MaximumBatchSize = 1000;
    internal const int MaximumCleanupIntervalMinutes = 1440;

    private MessageLogRetentionOptions(
        bool enabled,
        bool dryRun,
        int retentionDays,
        int batchSize,
        int cleanupIntervalMinutes)
    {
        Enabled = enabled;
        DryRun = dryRun;
        RetentionDays = retentionDays;
        BatchSize = batchSize;
        CleanupInterval = TimeSpan.FromMinutes(cleanupIntervalMinutes);
    }

    internal bool Enabled { get; }
    internal bool DryRun { get; }
    internal int RetentionDays { get; }
    internal int BatchSize { get; }
    internal TimeSpan CleanupInterval { get; }

    internal static bool TryRead(
        IConfiguration configuration,
        out MessageLogRetentionOptions options,
        out string error)
    {
        IConfigurationSection section = configuration.GetSection(SectionName);
        if (!TryBoolean(section, "Enabled", false, out bool enabled, out error) ||
            !TryBoolean(section, "DryRun", true, out bool dryRun, out error) ||
            !TryInteger(section, "RetentionDays", 30, 1, MaximumRetentionDays,
                out int retentionDays, out error) ||
            !TryInteger(section, "BatchSize", 1000, 1, MaximumBatchSize,
                out int batchSize, out error) ||
            !TryInteger(section, "CleanupIntervalMinutes", 60, 1,
                MaximumCleanupIntervalMinutes,
                out int cleanupIntervalMinutes, out error))
        {
            options = null;
            return false;
        }

        options = new MessageLogRetentionOptions(
            enabled,
            dryRun,
            retentionDays,
            batchSize,
            cleanupIntervalMinutes);
        error = string.Empty;
        return true;
    }

    private static bool TryBoolean(
        IConfigurationSection section,
        string key,
        bool defaultValue,
        out bool value,
        out string error)
    {
        string raw = section[key];
        if (raw == null)
        {
            value = defaultValue;
            error = string.Empty;
            return true;
        }

        if (bool.TryParse(raw, out value))
        {
            error = string.Empty;
            return true;
        }

        error = $"{SectionName}:{key} must be true or false.";
        return false;
    }

    private static bool TryInteger(
        IConfigurationSection section,
        string key,
        int defaultValue,
        int minimum,
        int maximum,
        out int value,
        out string error)
    {
        string raw = section[key];
        if (raw == null)
        {
            value = defaultValue;
            error = string.Empty;
            return true;
        }

        if (int.TryParse(raw, out value) && value >= minimum && value <= maximum)
        {
            error = string.Empty;
            return true;
        }

        error = $"{SectionName}:{key} must be between {minimum} and {maximum}.";
        return false;
    }
}
```

- [ ] **Step 4: Run focused tests and verify GREEN**

Run the Step 2 command. Expected: all option tests pass with no warnings.

- [ ] **Step 5: Commit the validated options**

```bash
git add OCPP.Core.Server/Maintenance/MessageLogRetentionOptions.cs \
  OCPP.Core.Server.Tests/MessageLogRetentionOptionsTests.cs
git commit -m 'Dodaj sigurne postavke zadržavanja MessageLog zapisa'
```

### Task 2: Assess and delete bounded batches

**Files:**
- Create: `OCPP.Core.Server/Maintenance/MessageLogRetentionRunner.cs`
- Create: `OCPP.Core.Server.Tests/MessageLogRetentionRunnerTests.cs`

**Interfaces:**
- Consumes: `MessageLogRetentionOptions` from Task 1.
- Produces: `MessageLogRetentionRunner.RunAsync(MessageLogRetentionOptions options, DateTime utcNow, CancellationToken token)`.
- Produces: `MessageLogRetentionSweepResult` with status, cutoff, candidate count, deleted count, batch count, oldest/newest eligible timestamps, estimated batches, and elapsed duration.
- Produces: overridable `OnBatchCompletedAsync(MessageLogRetentionBatchResult, CancellationToken)` for real progress logging and deterministic concurrency/cancellation tests.

- [ ] **Step 1: Write failing cutoff and dry-run tests**

Use one temporary SQLite file per test and the real `OCPPCoreContext`. Seed rows
at `now - 30 days - 1 tick`, exactly `now - 30 days`, and one tick newer.

```csharp
[Fact]
public async Task RunAsync_DryRunReportsOnlyStrictlyOlderRows()
{
    DateTime now = new(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc);
    using var provider = BuildSqliteProvider();
    await SeedAsync(provider,
        NewLog(now.AddDays(-30).AddTicks(-1)),
        NewLog(now.AddDays(-30)),
        NewLog(now.AddDays(-30).AddTicks(1)));

    var result = await CreateRunner(provider).RunAsync(
        EnabledOptions(dryRun: true, batchSize: 2), now, default);

    Assert.Equal(MessageLogRetentionSweepStatus.DryRun, result.Status);
    Assert.Equal(1, result.CandidateCount);
    Assert.Equal(0, result.DeletedCount);
    Assert.Equal(3, await CountLogsAsync(provider));
}
```

- [ ] **Step 2: Run the cutoff test and verify RED**

Run:

```bash
LANG=en_US.UTF-8 LC_ALL=en_US.UTF-8 DOTNET_ROLL_FORWARD=Major \
  dotnet test OCPP.Core.Server.Tests/OCPP.Core.Server.Tests.csproj \
  --no-restore --nologo --filter 'FullyQualifiedName~MessageLogRetentionRunnerTests.RunAsync_DryRunReportsOnlyStrictlyOlderRows'
```

Expected: compilation fails because the runner and result types do not exist.

- [ ] **Step 3: Implement assessment-only behavior**

Use a short-lived scope and `AsNoTracking()` for `LogTime < cutoff`. Query
count and first/last eligible timestamps. Return an empty result without `Min`
or `Max` on an empty set. In dry-run mode, calculate estimated batches as
`(candidateCount + batchSize - 1) / batchSize` and perform no delete.

- [ ] **Step 4: Run the cutoff test and verify GREEN**

Run the Step 2 command. Expected: one test passes.

- [ ] **Step 5: Write failing batching, tie-order, restart, and concurrency tests**

Add real SQLite tests that catch unbounded deletes, unstable equal-time
ordering, a cursor that skips rows after restart, and a moving cutoff that can
delete a concurrently inserted recent row.

```csharp
[Fact]
public async Task RunAsync_DeletesEligibleRowsInBoundedStableBatches()
{
    DateTime now = new(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc);
    using var provider = BuildSqliteProvider();
    await SeedEligibleLogsAsync(provider, now, count: 5, equalTimestamps: true);
    var runner = new RecordingRunner(provider, cancelAfterBatch: null);

    var result = await runner.RunAsync(
        EnabledOptions(dryRun: false, batchSize: 2), now, default);

    Assert.Equal(new[] { 2, 2, 1 }, runner.BatchSelectedCounts);
    Assert.Equal(5, result.DeletedCount);
    Assert.Equal(3, result.BatchCount);
    Assert.Equal(0, await CountLogsAsync(provider));
}

[Fact]
public async Task RunAsync_CancellationLeavesCommittedBatchesForRestart()
{
    DateTime now = new(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc);
    using var provider = BuildSqliteProvider();
    await SeedEligibleLogsAsync(provider, now, count: 5);
    using var cancellation = new CancellationTokenSource();
    var interrupted = new RecordingRunner(provider, cancelAfterBatch: 1, cancellation);

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => interrupted.RunAsync(
        EnabledOptions(dryRun: false, batchSize: 2), now, cancellation.Token));
    Assert.Equal(3, await CountLogsAsync(provider));

    var resumed = await CreateRunner(provider).RunAsync(
        EnabledOptions(dryRun: false, batchSize: 2), now, default);
    Assert.Equal(3, resumed.DeletedCount);
    Assert.Equal(0, await CountLogsAsync(provider));
}
```

For concurrent logging, insert a row with `LogTime=now` from
`OnBatchCompletedAsync` after batch one and assert it remains after the sweep.

- [ ] **Step 6: Run the new tests and verify RED**

Run all `MessageLogRetentionRunnerTests`. Expected: assessment passes while
batching/restart/concurrency assertions fail because deletion is not implemented.

- [ ] **Step 7: Implement one-transaction-per-batch deletion**

For each batch, create a new scope, select the literal identifiers with:

```csharp
var candidates = await db.MessageLogs
    .AsNoTracking()
    .Where(log => log.LogTime < cutoff)
    .OrderBy(log => log.LogTime)
    .ThenBy(log => log.LogId)
    .Select(log => new { log.LogId, log.LogTime })
    .Take(options.BatchSize)
    .ToListAsync(token);
```

Delete only `candidates.Select(x => x.LogId)` with `ExecuteDeleteAsync(token)`.
Dispose the scope before invoking `OnBatchCompletedAsync`, then re-query the
next batch. Check cancellation before every query. Record selected/deleted
counts and timestamp bounds without logging row payloads.

- [ ] **Step 8: Run runner tests and verify GREEN**

Run all `MessageLogRetentionRunnerTests`. Expected: all cutoff, dry-run,
batching, restart, repeat, empty, and concurrency tests pass.

- [ ] **Step 9: Commit the bounded runner**

```bash
git add OCPP.Core.Server/Maintenance/MessageLogRetentionRunner.cs \
  OCPP.Core.Server.Tests/MessageLogRetentionRunnerTests.cs
git commit -m 'Dodaj ograničeno čišćenje MessageLog zapisa'
```

### Task 3: Schedule the runner without enabling deletion

**Files:**
- Create: `OCPP.Core.Server/Maintenance/MessageLogRetentionService.cs`
- Create: `OCPP.Core.Server.Tests/MessageLogRetentionServiceTests.cs`
- Modify: `OCPP.Core.Server/Startup.cs`
- Modify: `OCPP.Core.Server/appsettings.json`

**Interfaces:**
- Consumes: validated options and runner from Tasks 1-2.
- Produces: hosted `MessageLogRetentionService` registered with `AddHostedService`.
- Produces: one delayed periodic sweep with sanitized summary/error logging.

- [ ] **Step 1: Write failing disabled, invalid, and registration tests**

Build a service provider without `OCPPCoreContext` for disabled and invalid
configuration. Calling the internal single-cycle method must return Disabled or
InvalidConfiguration without resolving a database scope. Build a normal
`Startup.ConfigureServices` collection with SQLite configuration and assert
exactly one hosted `MessageLogRetentionService` is registered independently of
Hangfire.

- [ ] **Step 2: Run service tests and verify RED**

Run:

```bash
LANG=en_US.UTF-8 LC_ALL=en_US.UTF-8 DOTNET_ROLL_FORWARD=Major \
  dotnet test OCPP.Core.Server.Tests/OCPP.Core.Server.Tests.csproj \
  --no-restore --nologo --filter 'FullyQualifiedName~MessageLogRetentionServiceTests'
```

Expected: compilation or registration assertions fail because the hosted
service does not exist.

- [ ] **Step 3: Implement delayed periodic scheduling and sanitized logs**

The hosted service parses options once at construction. `ExecuteAsync` returns
immediately for disabled/invalid configuration. Otherwise it waits
`CleanupInterval`, calls the runner, logs cutoff/count/batches/duration, and
repeats. Catch sweep exceptions outside cancellation and log only
`ex.GetType().Name`; do not include exception messages, SQL, rows, or connection
details. Keep an internal `RunOnceAsync(DateTime utcNow, CancellationToken)` for
behavioral tests.

- [ ] **Step 4: Register safe defaults**

In `Startup.ConfigureServices`, add one `MessageLogRetentionRunner` singleton
and one `MessageLogRetentionService` hosted service after database registration.
In `appsettings.json`, add:

```json
"MessageLogRetention": {
  "Enabled": false,
  "DryRun": true,
  "RetentionDays": 30,
  "BatchSize": 1000,
  "CleanupIntervalMinutes": 60
}
```

inside the existing `Maintenance` object. Do not alter `DbMessageLog` or any
other maintenance default.

- [ ] **Step 5: Run service and runner tests and verify GREEN**

Run both retention test classes. Expected: all pass, including no database
resolution when disabled/invalid and no Hangfire requirement.

- [ ] **Step 6: Commit service wiring**

```bash
git add OCPP.Core.Server/Maintenance/MessageLogRetentionService.cs \
  OCPP.Core.Server.Tests/MessageLogRetentionServiceTests.cs \
  OCPP.Core.Server/Startup.cs OCPP.Core.Server/appsettings.json
git commit -m 'Poveži periodično zadržavanje MessageLog zapisa'
```

### Task 4: Document dry-run-first operations

**Files:**
- Create: `docs/message-log-retention.md`
- Modify: `docs/operations.md`
- Modify: `docs/maintenance.md`

**Interfaces:**
- Consumes: exact configuration and logging behavior from Tasks 1-3.
- Produces: public-safe activation, observation, stop/retry, verification, and rollback guidance.

- [ ] **Step 1: Write the runbook**

Document every key/default, the strict cutoff, stable bounded ordering, and the
following explicit sequence: deploy disabled; enable dry-run; review candidate
count/bounds/batch estimate/duration/errors; separately approve execution;
disable or restore dry-run to stop future batches; verify recent rows remain;
restore from an operator-managed backup if deleted diagnostic data is required.
State that the feature never shrinks MDF/LDF files and has no archive.

- [ ] **Step 2: Update operations and maintenance indexes**

Add `Maintenance:MessageLogRetention:*` to the configuration table, add the
hosted service to background jobs, link the dedicated runbook, and add the
focused retention test command to the maintenance validation matrix.

- [ ] **Step 3: Verify public safety and formatting**

Run:

```bash
rg -n 'REQ-|CODEX-|ChatGPT|Google Drive|client|customer identifier|private host' \
  docs/message-log-retention.md docs/operations.md docs/maintenance.md
git diff --check
```

Expected: no private routing/context match and no whitespace errors.

- [ ] **Step 4: Commit the runbook**

```bash
git add docs/message-log-retention.md docs/operations.md docs/maintenance.md
git commit -m 'Dokumentiraj sigurno čišćenje MessageLog zapisa'
```

### Task 5: Validate the complete change

**Files:**
- Verify all files changed by Tasks 1-4.

**Interfaces:**
- Produces: a clean, public-safe request branch ready for one draft pull request.

- [ ] **Step 1: Run focused retention tests**

```bash
LANG=en_US.UTF-8 LC_ALL=en_US.UTF-8 DOTNET_ROLL_FORWARD=Major \
  dotnet test OCPP.Core.Server.Tests/OCPP.Core.Server.Tests.csproj \
  --no-restore --nologo --filter 'FullyQualifiedName~MessageLogRetention'
```

Expected: all focused tests pass.

- [ ] **Step 2: Run full solution validation**

```bash
LANG=en_US.UTF-8 LC_ALL=en_US.UTF-8 DOTNET_ROLL_FORWARD=Major \
  dotnet restore OCPP.Core.sln
LANG=en_US.UTF-8 LC_ALL=en_US.UTF-8 DOTNET_ROLL_FORWARD=Major \
  dotnet build OCPP.Core.sln --no-restore --nologo
LANG=en_US.UTF-8 LC_ALL=en_US.UTF-8 DOTNET_ROLL_FORWARD=Major \
  dotnet test OCPP.Core.Server.Tests/OCPP.Core.Server.Tests.csproj \
  --no-build --no-restore --nologo
bash ./scripts/check-mssql-migration-metadata.sh
```

Expected: restore/build succeed without errors, all tests pass, and migration
metadata guard passes despite no migration change.

- [ ] **Step 3: Audit diff and resource state**

```bash
git diff --check origin/main...HEAD
git diff --stat origin/main...HEAD
git status --short --branch
rg -n 'REQ-|CODEX-|ChatGPT|Google Drive|client|private host|password|token' \
  OCPP.Core.Server/Maintenance OCPP.Core.Server.Tests/MessageLogRetention* \
  docs/message-log-retention.md docs/operations.md docs/maintenance.md || true
df -k /Users/igbenic/Projects
```

Inspect every match; configuration key names such as `token` are acceptable
only when they do not expose a value. Confirm no migration/model snapshot,
generated artifact, temporary database, process, or container is tracked.

- [ ] **Step 4: Review the whole diff**

Verify that cleanup remains disabled and dry-run-first, the cutoff is fixed per
sweep, every delete uses selected IDs and a bounded `Take`, all errors are
sanitized, and no unrelated table or production operation is reachable.

- [ ] **Step 5: Publish one draft pull request**

Confirm intended scope with `git status -sb` and `git diff origin/main...HEAD`.
Push only `req/2026-08-14-007-messagelog-30d-retention`, then open one draft PR
against `main`. Include configuration defaults, no-migration impact, tests,
activation/rollback order, and explicit no-production-purge status in the PR
body. Add `codex` and `codex-automation` labels when available.
