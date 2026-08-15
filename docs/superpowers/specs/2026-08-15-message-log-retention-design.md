# MessageLog Online Retention Design

## Goal

Add an opt-in server maintenance path that keeps the configured recent
`MessageLog` window online and removes only older rows in bounded,
restart-safe batches. The first supported policy is 30 days. Deployment and
the first destructive run remain separate operator actions.

## Existing state

`MessageLog` is an append-only diagnostic table keyed by `LogId`. Current
application code writes rows from OCPP controllers and management login/logout
handling; it does not read historical `MessageLog` rows to drive charging,
transactions, payments, invoices, reservations, or audit state. The model
already has an index on `LogTime`, and `LogId` is the primary key.

The server already uses `BackgroundService` for periodic maintenance. Hangfire
is available only with SQL Server and startup maintenance runs synchronously
during application boot. A dedicated `BackgroundService` therefore matches the
existing architecture without delaying startup or adding a scheduler,
credential, infrastructure service, or database schema dependency.

## Configuration and safety defaults

Add the following keys under `Maintenance:MessageLogRetention`:

- `Enabled`: defaults to `false`.
- `DryRun`: defaults to `true`.
- `RetentionDays`: defaults to `30` and must be between `1` and `36500`.
- `BatchSize`: defaults to `1000` and must be between `1` and `1000`, keeping
  selected-identifier deletes below SQL Server's parameter limit.
- `CleanupIntervalMinutes`: defaults to `60` and must be between `1` and
  `1440`.

Missing values use these safe defaults. Explicit malformed or out-of-range
values fail closed: the service logs a sanitized configuration error and does
not query or delete rows. `Enabled=false` performs no database work. Enabling
the service while leaving `DryRun=true` produces accounting evidence only.

No checked configuration enables destructive cleanup. A later operator must
explicitly enable the service and disable dry-run after reviewing evidence.

## Components

### Options resolver

`MessageLogRetentionOptions` reads and validates the bounded configuration.
It exposes a validation result instead of silently clamping unsafe values.

### Retention runner

`MessageLogRetentionRunner` owns one sweep and uses an
`IServiceScopeFactory` to obtain short-lived `OCPPCoreContext` instances. It
computes one UTC cutoff at sweep start and uses the strict predicate
`LogTime < cutoff`; rows exactly at the cutoff and newer rows are preserved.

The runner first reports candidate count, oldest and newest candidate
timestamps, configured batch size, estimated batch count, cutoff, dry-run
state, and elapsed duration. Empty candidate sets report zero cleanly.

For an executing sweep, every batch:

1. selects at most `BatchSize` identifiers ordered by `LogTime`, then `LogId`;
2. deletes only those selected identifiers in one database operation;
3. commits independently from later batches; and
4. records sanitized batch number, selected/deleted counts, timestamp bounds,
   and duration.

The next batch re-queries the remaining table. Concurrent new logging is not
blocked by a long transaction. Interruption leaves prior batches committed and
a later sweep resumes from the oldest remaining eligible row. Re-running an
already completed sweep is a no-op. Cancellation is checked before every query
and batch.

### Hosted service

`MessageLogRetentionService` schedules the runner with the validated interval.
It waits one interval before the first automatic sweep so application startup
is not coupled to maintenance duration. A failed sweep logs only the exception
type plus bounded context and continues on the next interval; it never logs
row payloads or connection details.

The service is registered unconditionally, but disabled or invalid
configuration exits without database access.

## Data and schema impact

No entity, `DbContext` mapping, model snapshot, migration, GraphQL type, or
other database-facing schema changes are required. The existing `LogTime`
index narrows the cutoff query and the primary key provides deterministic
tie-breaking. Payment, invoice, transaction, reservation, connector, and audit
tables are outside the deletion query.

Database files are never shrunk by this feature. Physical file reclamation is
a separate database-administration decision.

## Testing

Tests use temporary SQLite databases and real EF Core queries. They cover:

- disabled and dry-run modes;
- missing defaults and malformed/out-of-range configuration;
- the strict 30-day boundary;
- deterministic ordering for equal timestamps;
- multiple bounded batches;
- concurrent insertion while a sweep is running;
- cancellation between batches and restart/resume;
- already-clean and repeated sweeps;
- candidate count and timestamp accounting; and
- service registration without Hangfire.

Focused tests must demonstrate red before implementation and green after it.
Final validation includes the full server test suite, solution build, migration
metadata guard, diff check, and a public-data/privacy scan.

## Operations and rollback

The maintenance runbook documents the dry-run-first activation sequence:

1. deploy with cleanup disabled;
2. enable dry-run with 30 days and the reviewed batch size;
3. observe candidate counts, bounds, batch estimate, duration, and errors;
4. separately approve and enable execution;
5. verify counts decrease without touching recent rows; and
6. disable the service to stop future batches.

Rollback is configuration-only for future execution: set `Enabled=false` or
restore `DryRun=true`. Deleted diagnostic rows require an operator-managed
database restore if recovery is ever needed; the application does not invent a
shadow archive or automatic restore path.

## Non-goals

- No production purge, deployment, restart, or database shrink.
- No retention changes for transactions, payments, invoices, reservations,
  connector state, audit evidence, filesystem dumps, or application logs.
- No new scheduler, secret, service, storage backend, or archive.
- No automatic enablement based on environment or database provider.
