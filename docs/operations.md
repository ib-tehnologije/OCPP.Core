# Operations

This document is public-safe. Do not add production hostnames, private IPs, credentials, tokens, private Drive links, client context, or deployment-specific secrets.

## Local Runbook

1. Restore and build:

```sh
dotnet restore OCPP.Core.sln
dotnet build OCPP.Core.sln
```

2. Choose database provider.

For SQLite local/test runs:

```sh
export ConnectionStrings__SqlServer=
export ConnectionStrings__SQLite='Filename=./SQLite/OCPP.Core.test.sqlite;foreign keys=True'
```

For SQL Server runs, set `ConnectionStrings__SqlServer` to a local or environment-specific connection string through environment variables or user secrets. Do not commit real connection strings.

3. Start server:

```sh
export ApiKey='replace-with-local-api-key'
dotnet run --project OCPP.Core.Server
```

4. Start management app in another terminal:

```sh
export ApiKey='replace-with-local-api-key'
export ServerApiUrl='http://localhost:8081/API'
dotnet run --project OCPP.Core.Management
```

5. Open the management app at `http://localhost:8082`.

6. Configure or seed charge points before connecting chargers. The server rejects `/OCPP/{chargePointId}` sessions for unknown charge point identifiers.

## Configuration

Configuration sources observed:

- `appsettings.json`
- optional `appsettings.{Environment}.json`
- environment variables
- user secrets IDs on server and management app projects

Use double underscores for nested environment variables, for example `ConnectionStrings__SQLite`.

Important configuration areas:

| Area | Keys |
| --- | --- |
| Database | `ConnectionStrings:SqlServer`, `ConnectionStrings:SQLite`, `AutoMigrateDB` |
| Server API | `ApiKey`, `ServerApiUrl` in management |
| OCPP | `MessageDumpDir`, `DbMessageLog`, `ShowIndexInfo`, `MaxMessageSize`, `ValidateMessages`, `DenyConcurrentTx`, `HeartBeatInterval` |
| Maintenance | `Maintenance:PendingPaymentTimeoutMinutes`, `Maintenance:ReservationTimeoutMinutes`, `Maintenance:StatusReleaseMinutes`, `Maintenance:CleanupIntervalSeconds`, `Maintenance:IdleWarningSweepSeconds`, `Maintenance:AvailableStatusOpenTransactionGraceMinutes` |
| Payments | `Payments:RequirePreparingBeforeRemoteStart`, `Payments:RemoteStartIdTokenType`, `Payments:StartWindowMinutes`, `Payments:MinimumSessionFeeKwh`, `Payments:MinimumChargeAmountCents`, `Payments:IdleFeeExcludedWindow`, `Payments:IdleFeeExcludedTimeZoneId`, `Payments:IdleAutoStopMinutes`, `Payments:ChargerResponseTimeoutMs` |
| Stripe | `Stripe:Enabled`, `Stripe:UseMockServices`, `Stripe:ApiKey`, `Stripe:WebhookSecret`, `Stripe:AllowInsecureWebhooks`, `Stripe:Currency`, `Stripe:ReturnBaseUrl`, `Stripe:ProductName`, `Stripe:MockCustomerEmail`, `Stripe:MockDiagnosticsDirectory` |
| Notifications | `Notifications:EnableCustomerEmails`, `Notifications:IdleWarningLeadMinutes`, `Notifications:SinkDirectory`, `Notifications:FromAddress`, `Notifications:FromName`, `Notifications:ReplyToAddress`, `Notifications:BccAddress`, `Notifications:Smtp:*` |
| Invoices | `Invoices:Enabled`, `Invoices:Provider`, `Invoices:Mode`, `Invoices:ERacuni:*` |
| Management portal | `Users`, `PublicPortal:*`, `Email:*`, `OwnerReportSchedule:*`, `ServerApiTimeoutSeconds` |
| Hangfire | `Hangfire:EnableDashboard`, `Hangfire:DashboardPath`, `Hangfire:Queue` |
| Kestrel | `Kestrel:Endpoints:*` |
| Sentry | `Sentry:Dsn` or `SENTRY_DSN` |

The checked `appsettings.json` files include development/sample values. Override secret-like values for any real run.

## Database Operations

SQL Server:

- EF migrations live in `OCPP.Core.Database/Migrations`.
- `AutoMigrateDB=true` applies migrations at server startup.
- `make dbupdate` applies migrations through EF tooling with production environment variables.
- `make add-migration NAME=AddSomething` scaffolds a named migration.
- `make migrate` scaffolds a timestamped migration name.
- Always run `make check-migration-metadata` after migration changes.

SQLite:

- Used for local/test runs.
- Server startup uses `EnsureCreated()` instead of SQL Server migrations.
- `make sqlite-reset` removes the configured local SQLite file and WAL/SHM files.

Unknown / verify:

- Whether `SQL-Server/*.sql` scripts are still authoritative for any supported deployment path.
- Whether checked SQLite files are intended long-term fixtures.

## Background Jobs and Startup Maintenance

Server app:

- `StartupMaintenance.Run` executes on startup to repair reservation active keys, abandon stale pending reservations, and release stale connector statuses.
- `PaymentReservationCleanupService` runs periodically to abandon stale pending reservations, time out starts, recover open transactions on available connectors, and complete waiting-for-disconnect reservations.
- `IdleFeeWarningEmailService` periodically sends customer idle-fee warning emails when notifications and Stripe are configured.
- Hangfire server starts only when SQL Server connection string is configured. The server uses a configurable queue, defaulting to `payments`.

Management app:

- Hangfire server starts only when SQL Server connection string is configured.
- `OwnerReportService.ScheduleRecurringReport` registers `owner-report-recurring` when `OwnerReportSchedule:Enabled` is true.

## Logging and Monitoring

Observed:

- File logging through `Karambolo.Extensions.Logging.File`.
- Log files are configured under `Logs` by each app.
- Sentry is enabled only when a DSN is present in configuration.
- Hangfire dashboards can be enabled with `Hangfire:EnableDashboard` and `Hangfire:DashboardPath`.

Unknown / verify:

- Production log collection, retention, alerting, and Sentry project ownership.

## Deployment Hints

Observed public-safe deployment hints:

- `OCPP.Core.Server/Dockerfile` publishes the server app and exposes HTTP port `8081`.
- `OCPP.Core.Management/Dockerfile` publishes the management app and exposes HTTP port `8082`.
- `.github/workflows/docker-build.yml` builds and pushes GHCR images for server and management on `main` and workflow dispatch.
- Management app trusts forwarded proxy headers.

Unknown / verify:

- Whether production uses these Dockerfiles directly.
- Reverse proxy, TLS, database, secrets, and storage topology.
- Backup and restore procedures.

## Common Operational Gotchas

- Management remote actions fail if `ServerApiUrl` is wrong or `ApiKey` differs from the server's configured key.
- WebSocket remote actions fail if the charge point is offline or connected to a different server instance.
- Hangfire-dependent behavior is absent under SQLite.
- SQL Server migrations are not validated by SQLite E2E runs.
- `Payments:MinimumSessionFeeKwh` defaults to `1.0`. Completed sessions with missing, inconsistent, or lower delivered-energy readings suppress only the fixed session fee. If no other energy or time fee remains, the uncaptured payment intent is cancelled and invoice creation is skipped.
- `Payments:MinimumChargeAmountCents` defaults to `50`. Positive final amounts below the configured minimum cancel the uncaptured payment intent and skip invoice creation and paid-completion emails. Exactly the configured minimum remains capturable.
- Public payment behavior depends on server, management, database, Stripe/mock Stripe, and time-based cleanup settings.
- OCPP schema validation is optional and logs/continues on validation errors.
- Do not expose Hangfire dashboard or appsettings-derived secrets without deployment-specific access controls.
