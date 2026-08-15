# MessageLog Online Retention

The server can assess and remove expired rows from the diagnostic
`MessageLog` table. The feature is disabled by default and starts in dry-run
mode when enabled. It does not affect transactions, payments, invoices,
reservations, connector state, or other audit data.

## Configuration

All keys are under `Maintenance:MessageLogRetention`.

| Key | Default | Valid values | Effect |
| --- | --- | --- | --- |
| `Enabled` | `false` | `true` or `false` | Enables periodic assessment. |
| `DryRun` | `true` | `true` or `false` | Reports candidates without deletion when `true`. |
| `RetentionDays` | `30` | `1` through `36500` | Rows with `LogTime` strictly older than the fixed UTC cutoff are eligible. |
| `BatchSize` | `1000` | `1` through `1000` | Maximum selected identifiers in one delete operation. |
| `CleanupIntervalMinutes` | `60` | `1` through `1440` | Delay before the first sweep and between later sweeps. |

An explicit malformed or out-of-range value disables the service. It is not
clamped. Environment-variable names use double underscores, for example
`Maintenance__MessageLogRetention__DryRun`.

The service reads configuration when the server process starts. A change to
these values takes effect on the next process start.

## Selection and batching

Each sweep computes one UTC cutoff. The eligibility predicate is
`LogTime < cutoff`; a row exactly at the cutoff is preserved. The service first
reports the candidate count, oldest and newest candidate timestamps, estimated
batch count, cutoff, mode, and duration.

Execution orders candidates by `LogTime`, then by the `LogId` primary key. It
selects at most `BatchSize` identifiers and deletes only those identifiers in a
single database operation. The database scope and transaction end after every
batch. This keeps the operation bounded and lets current logging continue
without one sweep holding a long transaction.

If the process stops between batches, completed batches remain committed. The
next sweep re-queries the remaining eligible rows and resumes safely. Repeating
a completed sweep is a no-op. Payload fields from `MessageLog` are never added
to retention logs.

## Dry-run-first activation

1. Leave `Enabled=false` while deploying or upgrading the application.
2. Verify that the environment's existing database backup and restore process
   is current. This feature does not create its own backup or archive.
3. Set `Enabled=true`, keep `DryRun=true`, use `RetentionDays=30`, and choose a
   reviewed batch size no larger than `1000`.
4. Restart the server through the environment's normal supported process so
   the settings are loaded.
5. Review at least one complete dry-run record: cutoff, candidate count,
   oldest/newest timestamps, estimated batches, duration, and sanitized error
   type if a sweep failed.
6. Confirm independently that recent rows and required operational evidence
   fall outside the candidate window.
7. Obtain the environment's normal approval for destructive maintenance.
8. Set `DryRun=false` and restart the server. Observe selected/deleted counts
   and duration for every batch.
9. Re-query counts and timestamp bounds after the sweep. Confirm all remaining
   rows are at or newer than the cutoff used by the completed sweep.

Enabling assessment does not authorize database-file shrinking. MDF/LDF or
other physical file reclamation remains a separate database-administration
operation.

## Stop, retry, and rollback

To stop future batches, set `Enabled=false` (or restore `DryRun=true`) and use
the normal supported process restart. Shutdown cancellation is checked before
each query and batch; a batch already committed is not rolled back with later
batches.

After a transient database error, leave the service disabled until database
health and the last completed batch are understood. Re-enable dry-run first,
verify the remaining candidate set, and only then resume execution. A retry
does not need a cursor or repair row because selection is rebuilt from the
database.

Configuration rollback prevents future deletion but cannot recreate rows that
were already removed. Recovering deleted diagnostic rows requires the
environment's operator-managed database restore procedure. Do not invent a
shadow archive, copy rows into unrelated tables, or automatically shrink files
as a rollback mechanism.

## Validation

Focused tests:

```sh
LANG=en_US.UTF-8 LC_ALL=en_US.UTF-8 DOTNET_ROLL_FORWARD=Major \
  dotnet test OCPP.Core.Server.Tests/OCPP.Core.Server.Tests.csproj \
  --filter 'FullyQualifiedName~MessageLogRetention'
```

The tests use temporary SQLite databases and cover strict cutoff behavior,
dry-run accounting, stable bounded ordering, interruption/restart, repeat
sweeps, concurrent recent logging, configuration validation, and hosted-service
registration. SQL Server migration metadata checks remain part of full
validation even though this feature changes no entity, mapping, snapshot, or
migration.
