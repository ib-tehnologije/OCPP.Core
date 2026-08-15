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
| OCPP | `MessageDumpDir`, `MessageDumpRetentionHours`, `MessageDumpCleanupIntervalMinutes`, `DbMessageLog`, `ShowIndexInfo`, `MaxMessageSize`, `ValidateMessages`, `DenyConcurrentTx`, `HeartBeatInterval` |
| Maintenance | `Maintenance:PendingPaymentTimeoutMinutes`, `Maintenance:ReservationTimeoutMinutes`, `Maintenance:StatusReleaseMinutes`, `Maintenance:CleanupIntervalSeconds`, `Maintenance:IdleWarningSweepSeconds`, `Maintenance:AvailableStatusOpenTransactionGraceMinutes`, `Maintenance:AuthorizationReleaseMaxAttempts`, `Maintenance:AuthorizationReleaseRetryBaseMinutes`, `Maintenance:AuthorizationReleaseInProgressTimeoutMinutes` |
| Message retention | `Maintenance:MessageLogRetention:Enabled`, `Maintenance:MessageLogRetention:DryRun`, `Maintenance:MessageLogRetention:RetentionDays`, `Maintenance:MessageLogRetention:BatchSize`, `Maintenance:MessageLogRetention:CleanupIntervalMinutes` |
| Payments | `Payments:RequirePreparingBeforeRemoteStart`, `Payments:RemoteStartIdTokenType`, `Payments:StartWindowMinutes`, `Payments:MinimumSessionFeeKwh`, `Payments:MinimumChargeAmountCents`, `Payments:IdleFeeExcludedWindow`, `Payments:IdleFeeExcludedTimeZoneId`, `Payments:IdleAutoStopMinutes`, `Payments:ChargerResponseTimeoutMs`, `Payments:Vies:Enabled`, `Payments:Vies:TimeoutSeconds` |
| Stripe | `Stripe:Enabled`, `Stripe:UseMockServices`, `Stripe:ApiKey`, `Stripe:WebhookSecret`, `Stripe:AllowInsecureWebhooks`, `Stripe:Currency`, `Stripe:ReturnBaseUrl`, `Stripe:ProductName`, `Stripe:MockCustomerEmail`, `Stripe:MockDiagnosticsDirectory` |
| Notifications | `Notifications:EnableCustomerEmails`, `Notifications:IdleWarningLeadMinutes`, `Notifications:SinkDirectory`, `Notifications:FromAddress`, `Notifications:FromName`, `Notifications:ReplyToAddress`, `Notifications:BccAddress`, `Notifications:Smtp:*` |
| Invoices | `Invoices:Enabled`, `Invoices:Provider`, `Invoices:Mode`, `Invoices:ERacuni:*` |
| Management portal | `Users`, `PublicPortal:*`, `Email:*`, `OwnerReportSchedule:*`, `ServerApiTimeoutSeconds` |
| Hangfire | `Hangfire:EnableDashboard`, `Hangfire:DashboardPath`, `Hangfire:Queue` |
| Kestrel | `Kestrel:Endpoints:*` |
| Sentry | `Sentry:Dsn` or `SENTRY_DSN` |

The checked `appsettings.json` files include development/sample values. Override secret-like values for any real run.

`Payments:StartWindowMinutes` is applied when payment authorization succeeds and stored as an absolute UTC deadline on the reservation. Confirmation and webhook authorization paths use the same configured value. A reservation remains eligible before that stored deadline and expires at the deadline; later configuration changes do not rewrite already-persisted deadlines.

## Charger Reset Operations

The server reset endpoint is `GET /API/Reset/{chargePointId}/{mode?}`. It accepts `Hard` and `Soft` modes; an unsupported mode returns HTTP 400. When mode is omitted, the compatibility default is retained.

| Requested mode | OCPP 1.6 | OCPP 2.0.1 / 2.1 |
| --- | --- | --- |
| omitted | `Soft` | `OnIdle` |
| `Soft` | `Soft` | `OnIdle` |
| `Hard` | `Hard` | `Immediate` |

The management reset action requests `Hard`, which maps to OCPP 2.x `Immediate`. This adds no configuration key or deployment step.

## Database Operations

Confirmed company invoice buyer data is stored as nullable bounded columns on `ChargePaymentReservation`. Migration `AddInvoiceBuyerSnapshot` is non-destructive for existing reservations. Migration `AddInvoiceBuyerVatValidation` adds the original identifier, canonical VAT identifier, local validation status, VIES status, checked time, and bounded reference; its nullable columns do not rewrite historical rows. New public sessions must confirm the complete buyer snapshot before Stripe Checkout is created. Existing legacy R1 reservations can still build from Stripe metadata, while newly confirmed requests use the durable reservation snapshot as the invoice source of truth. The legacy buyer-data endpoint remains available for compatibility, but the public status page no longer offers post-checkout buyer entry.

The public start page does not retain reusable company-buyer details in browser storage. Submitted values survive only the ordinary server-rendered validation-error response; a new visit starts with an empty buyer form.

Foreign VAT identifiers are always normalized and structure-checked locally before Stripe. Optional VIES verification is disabled by default and can be enabled with `Payments:Vies:Enabled=true`; `Payments:Vies:TimeoutSeconds` is clamped to 1-10 seconds. A timeout, transport failure, malformed response, or VIES/member-state outage becomes `Unavailable` and must not block checkout. The request contains only `countryCode` and `vatNumber`. Never add company names, addresses, billing emails, credentials, or requester identity fields to the VIES request. Generic foreign identifiers that are not marked as VAT registrations do not call VIES and retain their entered value.

Support can distinguish local `Valid` plus VIES `Valid`, `Invalid`, `Unavailable`, or `NotChecked` from the reservation fields. The public status API intentionally omits the provider reference and VAT number. Provider validation failures must remain sanitized in customer responses and logs; do not expose VIES response bodies, e-racuni credentials, or raw authenticated request envelopes while diagnosing invoice submission.

Once an invoice submission log is marked submitted or contains an external document identifier, number, or URL, the public buyer-data endpoint is locked. Corrections must use the provider-supported correction, storno, or reissue process rather than mutating the reservation snapshot.

SQL Server:

- EF migrations live in `OCPP.Core.Database/Migrations`.
- `AutoMigrateDB=true` applies migrations at server startup.
- `make dbupdate` applies migrations through EF tooling with production environment variables.
- `make add-migration NAME=AddSomething` scaffolds a named migration.
- `make migrate` scaffolds a timestamped migration name.
- Always run `make check-migration-metadata` after migration changes.

Migration `AddPaymentAuthorizationReleaseReconciliation` adds nullable release-state, timestamp, and error fields plus a zero-valued attempt counter to `ChargePaymentReservation`, together with an append-only `PaymentAuthorizationReleaseAttempt` audit table. Nullable state is intentional: existing terminal reservations remain unarmed, so deploying the migration does not start historical cancellation or remediation.

Migration `AddInvoiceSubmissionIdempotency` adds nullable `InvoiceSubmissionLog.SubmissionKey`, a filtered unique index, and nullable bounded lease identifier/expiry columns. Historical rows remain null and are not backfilled. New submit-mode attempts use the provider plus deterministic API transaction reference as their durable lineage and hold a five-minute database lease across the provider create boundary. A repeated, concurrent, or restarted attempt checks submitted/external local evidence first and performs an exact provider lookup before another create. Transport errors, non-empty unmatched responses, unrecognized schemas, and multiple exact provider matches remain `ProviderUnknown` and fail closed.

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
- `MessageLogRetentionService` is disabled by default. When explicitly enabled,
  it assesses rows older than the configured cutoff and either reports them in
  dry-run mode or removes selected identifiers in bounded batches. See
  [MessageLog online retention](message-log-retention.md).
- Hangfire server starts only when SQL Server connection string is configured. The server uses a configurable queue, defaulting to `payments`.

Management app:

- Hangfire server starts only when SQL Server connection string is configured.
- `OwnerReportService.ScheduleRecurringReport` registers `owner-report-recurring` when `OwnerReportSchedule:Enabled` is true.

## Explicit financial recovery

The `OCPP.Core.Recovery` command is not a background job and never scans for candidates. Store the real manifest outside the repository; each entry names exactly one reservation and one of `recover-settlement`, `release-authorization`, or `recover-invoice`.

1. Confirm the database migration is applied through the ordinary deployment process. The command does not migrate a database.
2. Run without `--execute`. A dry-run performs local evidence checks only and prints a SHA-256 digest plus redacted reservation identifiers.
3. Review every decision. Settlement requires exact linked terminal transaction, ordered timestamps, valid meter delta, non-negative pricing snapshots, and a positive derived billable amount. Authorization release requires a terminal unused reservation without transaction, energy, captured funds, or invoice evidence. Invoice recovery requires a completed captured reservation and linked transaction.
   Authorization release may conclude that no matching transaction exists only when charge point, positive connector, charge tag, and an ordered persisted lower-bound-to-start-deadline window are all present. The lower bound is the authorization time when available, otherwise the earlier reservation creation time. Missing or invalid linkage evidence is indeterminate and stops dry-run, execution, and reconciliation before any provider read or release action.
4. Obtain separate operator approval for the exact manifest digest.
5. Run with `--execute --confirm-sha256 <digest>`. Every row is reloaded and rechecked immediately before its operation. Authorization release delegates to the existing provider ownership and `requires_capture` reconciler. Settlement capture suppresses invoice/customer-notification side effects so invoice recovery stays separately allowlisted. Invoice recovery uses local uniqueness, a durable submission lease, and provider lookup before create.
6. Retain the sanitized report with the operator record. Never store the real manifest, provider response, credentials, customer data, or private handoff evidence in this public repository.

Any changed manifest, missing evidence, provider ambiguity, duplicate provider match, or unavailable dependency stops the affected item. Do not bypass the digest, edit database fields manually, substitute estimated energy or fees, or turn the command into a scheduled task.

## Logging and Monitoring

Observed:

- File logging through `Karambolo.Extensions.Logging.File`.
- Log files are configured under `Logs` by each app.
- Raw OCPP filesystem dumps are disabled when `MessageDumpDir` is empty, which is the default. To diagnose message exchange temporarily, point `MessageDumpDir` at a dedicated directory. Files older than `MessageDumpRetentionHours` (default 24) are removed every `MessageDumpCleanupIntervalMinutes` (default 15); non-positive retention or interval values disable cleanup.
- Database `MessageLog` retention reports cutoff, counts, timestamp bounds,
  batches, duration, and sanitized error types without logging message payloads.
  Follow the dry-run-first activation and rollback sequence in
  [MessageLog online retention](message-log-retention.md).
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
- Enabling VIES introduces a bounded outbound dependency on the European Commission service. Monitor `Unavailable` rates, but do not retry synchronously or turn an outage into a checkout failure.
- OCPP schema validation is optional and logs/continues on validation errors.
- Do not expose Hangfire dashboard or appsettings-derived secrets without deployment-specific access controls.
