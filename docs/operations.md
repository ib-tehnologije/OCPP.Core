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
| Maintenance | `Maintenance:PendingPaymentTimeoutMinutes`, `Maintenance:ReservationTimeoutMinutes`, `Maintenance:StatusReleaseMinutes`, `Maintenance:CleanupIntervalSeconds`, `Maintenance:IdleWarningSweepSeconds`, `Maintenance:AvailableStatusOpenTransactionGraceMinutes`, `Maintenance:AuthorizationReleaseMaxAttempts`, `Maintenance:AuthorizationReleaseRetryBaseMinutes`, `Maintenance:AuthorizationReleaseInProgressTimeoutMinutes` |
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

Confirmed company invoice buyer data is stored as nullable bounded columns on `ChargePaymentReservation`. Migration `AddInvoiceBuyerSnapshot` is non-destructive for existing reservations. New public sessions must confirm the complete buyer snapshot before Stripe Checkout is created. Existing legacy R1 reservations can still build from Stripe metadata, while newly confirmed requests use the durable reservation snapshot as the invoice source of truth. The legacy buyer-data endpoint remains available for compatibility, but the public status page no longer offers post-checkout buyer entry.

The public start page does not retain reusable company-buyer details in browser storage. Submitted values survive only the ordinary server-rendered validation-error response; a new visit starts with an empty buyer form.

Foreign tax identifiers are not registry-verified by the application. Provider validation failures must remain sanitized in customer responses and logs; do not expose e-racuni credentials or raw authenticated request envelopes while diagnosing invoice submission.

Once an invoice submission log is marked submitted or contains an external document identifier, number, or URL, the public buyer-data endpoint is locked. Corrections must use the provider-supported correction, storno, or reissue process rather than mutating the reservation snapshot.

SQL Server:

- EF migrations live in `OCPP.Core.Database/Migrations`.
- `AutoMigrateDB=true` applies migrations at server startup.
- `make dbupdate` applies migrations through EF tooling with production environment variables.
- `make add-migration NAME=AddSomething` scaffolds a named migration.
- `make migrate` scaffolds a timestamped migration name.
- Always run `make check-migration-metadata` after migration changes.

Migration `AddPaymentAuthorizationReleaseReconciliation` adds nullable release-state, timestamp, and error fields plus a zero-valued attempt counter to `ChargePaymentReservation`, together with an append-only `PaymentAuthorizationReleaseAttempt` audit table. Nullable state is intentional: existing terminal reservations remain unarmed, so deploying the migration does not start historical cancellation or remediation.

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
- `PaymentReservationCleanupService` runs periodically to abandon stale pending reservations, time out starts, recover open transactions on available connectors, complete waiting-for-disconnect reservations, and retry due authorization releases that were explicitly armed by the application.
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
- `Payments:MinimumSessionFeeKwh` defaults to `1.0`. Completed sessions with missing, inconsistent, or lower delivered-energy readings are no-charge: billable line amounts are zeroed, the uncaptured payment intent is cancelled, and invoice creation plus paid-completion emails are skipped.
- `Payments:MinimumChargeAmountCents` defaults to `50`. Positive final amounts at or above the delivered-energy threshold but below the configured minimum cancel the uncaptured payment intent and skip invoice creation and paid-completion emails. Exactly the configured minimum remains capturable.
- Authorization release retries default to four mutation attempts with a one-minute exponential base delay, followed by one final read-only provider verification after an indeterminate last attempt. Set `Maintenance:AuthorizationReleaseMaxAttempts` and `Maintenance:AuthorizationReleaseRetryBaseMinutes` to change those bounds. A five-minute in-progress lease prevents overlapping sweeps and is configurable with `Maintenance:AuthorizationReleaseInProgressTimeoutMinutes`. Provider state and reservation ownership are rechecked on every attempt; active, captured, invoiced, succeeded, received-funds, or ambiguous cases are not cancelled automatically.
- If checkout completion linkage was missed or reordered, reconciliation retrieves the owned Checkout Session directly before reading its PaymentIntent. Missing or mismatched session/intent ownership, and inability to verify invoice state, stop automatic release and require review.
- `payment_intent.amount_capturable_updated` must remain enabled on the Stripe webhook endpoint. It closes the race where a terminal reservation becomes capturable after checkout/cleanup ordering, while webhook-event deduplication prevents repeat cancellation.
- Public payment behavior depends on server, management, database, Stripe/mock Stripe, and time-based cleanup settings.
- OCPP schema validation is optional and logs/continues on validation errors.
- Do not expose Hangfire dashboard or appsettings-derived secrets without deployment-specific access controls.
