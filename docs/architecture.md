# Architecture

## High-Level Architecture

The repository contains two ASP.NET Core applications that share one EF Core database model:

- `OCPP.Core.Server` accepts charge point WebSocket sessions, processes OCPP messages, exposes server-side `/API/...` commands, and runs server maintenance/payment services.
- `OCPP.Core.Management` provides the operator UI and public charging/payment UI. It stores and reads shared database data directly, and it calls `OCPP.Core.Server` through `ServerApiUrl` with `X-API-Key` for live status and remote commands.

Both applications use `OCPP.Core.Database` for entities and `OCPPCoreContext`.

## Main Modules

### OCPP Server

Location: `OCPP.Core.Server/`

- `Program.cs` and `Startup.cs` bootstrap configuration, logging, EF Core, Hangfire, payment services, hosted services, WebSockets, and the custom middleware.
- `OCPPMiddleware.cs` owns request routing, live charge point state, `/OCPP/{chargePointId}` WebSocket acceptance, `/API/...` commands, payment API handlers, message dumping, and in-memory request queues.
- `OCPPMiddleware.OCPP16.cs`, `OCPPMiddleware.OCPP20.cs`, and `OCPPMiddleware.OCPP21.cs` contain protocol-specific receive loops and server-to-charger operations.
- `ControllerOCPP*.cs` files process incoming protocol actions.
- `Messages_OCPP*` and `Schema*` folders contain protocol DTOs and optional JSON schema validation files.
- `Payments/` contains Stripe checkout coordination, reservation lifecycle, public payment API behavior, idle fee calculation, invoice integration, notification email logic, and maintenance jobs.

### Management and Public Portal

Location: `OCPP.Core.Management/`

- `Program.cs` and `Startup.cs` bootstrap MVC, cookie authentication, localization, forwarded headers, Hangfire, public/static files, and routes.
- `Controllers/HomeController.*.cs` implement operator pages for charge points, connectors, tags, transactions, reports, public portal settings, and exports.
- `Controllers/ApiController.*.cs` call the server API for operator-triggered remote actions.
- `Controllers/PublicController.cs` implements anonymous public map/start flows.
- `Controllers/PaymentsController.cs` handles checkout redirects, status pages, public stop requests, and R1 invoice requests by proxying to server payment APIs.
- `Services/OwnerReportService.cs` builds CSV/XLSX reports and schedules monthly Hangfire jobs when enabled.
- `Resources/` and `Views/` hold localization and Razor UI.

### Database

Location: `OCPP.Core.Database/`

- `OCPPCoreContext.cs` defines the EF Core model and relationships.
- `DbContextExtensions.cs` selects SQL Server when `ConnectionStrings:SqlServer` exists, otherwise SQLite when `ConnectionStrings:SQLite` exists.
- `Migrations/` contains SQL Server-oriented EF migrations and model snapshot.
- `ChargePaymentReservation*`, `StripeWebhookEvent`, and `InvoiceSubmissionLog` support payment/invoice lifecycle state.

### Extensions

Locations: `OCPP.Core.Server.Extensions/`, `Extensions/`

- `IRawMessageSink` receives incoming and outgoing OCPP messages.
- `IExternalAuthorization` can return allow/deny/null for OCPP authorization decisions.
- The middleware loads DLLs from an output `Extensions` directory when the DLL name contains `Extension`.
- Sample extensions include Azure Service Bus forwarding and token authorization override.

### Tests and Simulators

Locations: `OCPP.Core.Server.Tests/`, `OCPP.Core.Test/`, `Simulators/`

- xUnit tests cover middleware, controllers, payment, invoice, public portal, management behavior, and maintenance services.
- `OCPP.Core.Test` is a console protocol harness.
- `Simulators/run_e2e_stack.mjs` starts local apps with temporary SQLite state, seeds charge points, runs Node simulators, and can run Playwright.
- `Simulators/run_local_regression.py` runs a broader regression matrix after the solution is built.

## Data Flow

### Charge Point Session Flow

1. A charge point connects to `/OCPP/{chargePointId}` with one of the supported WebSocket subprotocols: `ocpp1.6`, `ocpp2.0.1`, or `ocpp2.1`.
2. Middleware looks up the charge point in the database.
3. Optional charge point basic auth or client certificate thumbprint validation runs based on stored charge point fields.
4. Middleware stores live session state in an in-memory dictionary and dispatches messages to the protocol-specific receive loop.
5. Protocol controllers update database state and may trigger payment/reservation linkage.

### Management Remote Command Flow

1. An operator action in `OCPP.Core.Management` calls a management API controller.
2. The management app calls `OCPP.Core.Server` through `ServerApiUrl`.
3. The server API checks `X-API-Key` unless the route is the Stripe webhook.
4. Middleware resolves the live WebSocket session and sends the appropriate OCPP request.
5. The management controller returns the server result to the UI.

### Public Payment Flow

1. A driver opens the public map/start page in the management app.
2. `PublicController` builds availability and pricing state from the database and server API.
3. A start request posts to server `Payments/Create`.
4. The server payment coordinator creates or resumes a reservation and may redirect to Stripe or use mock services in test mode.
5. After checkout confirmation, server code requests charger start through the live OCPP session and updates reservation/transaction state.
6. Public status pages poll server payment status and can request stop or R1 invoice data.

### Database Flow

- Both apps use the shared EF Core model.
- SQL Server uses migrations when `AutoMigrateDB` is enabled.
- SQLite local/test runs use `EnsureCreated()` in server startup because migrations are SQL Server-oriented.

## Integration Points

- Stripe checkout, payment intents, and webhooks.
- e-racuni invoice API through `Invoices:ERacuni` options.
- SMTP or local sink directory for customer and owner-report emails.
- Hangfire with SQL Server storage for background jobs and dashboards.
- Sentry, enabled only when a Sentry DSN is configured.
- Azure Service Bus via sample raw-message extension.
- Reverse proxy forwarded headers in the management app.

## Important Constraints

- Server live charge point state is in memory. Remote commands need the target charge point connected to the same running server instance.
- Management and server API keys must match.
- Charge points must be preconfigured in the database.
- SQL Server migrations should remain SQL Server-shaped; run `scripts/check-mssql-migration-metadata.sh`.
- SQLite is useful for local and automated tests but does not exercise the SQL Server migration path.
- Hangfire jobs and dashboard require SQL Server configuration.
- Payment behavior is spread across `OCPP.Core.Server`, `OCPP.Core.Management`, and database state; do not validate only one layer.

## Fragile or Risky Areas

- `OCPPMiddleware.cs` is large and centralizes routing, live session state, server API, and payment handlers.
- Payment reservation status transitions affect connector locking, remote starts, cleanup, public status UI, invoice creation, and emails.
- Time-based behavior depends on UTC timestamps, configured time zones, idle-fee windows, and cleanup intervals.
- Protocol behavior differs across OCPP 1.6, 2.0.1, and 2.1; keep protocol-specific tests in mind.
- The checked appsettings files contain development/sample values. Do not copy secret-like values into docs, commits, or deployment notes.
- Generated or runtime artifacts appear to be checked in under `SQLite/` and `Simulators/playwright/`; verify intent before deleting or relying on them.

## Where to Look When Changing Behavior

- OCPP connection or message handling: `OCPP.Core.Server/OCPPMiddleware*.cs`, `ControllerOCPP*.cs`, `Messages_OCPP*`, `Schema*`.
- Remote commands from the UI: `OCPP.Core.Management/Controllers/ApiController.*.cs` and matching server `/API` branches in `OCPPMiddleware.cs`.
- Payments/public charging: `OCPP.Core.Server/Payments/`, payment handlers in `OCPPMiddleware.cs`, `OCPP.Core.Management/Controllers/PublicController.cs`, `PaymentsController.cs`, public Razor views.
- Database schema: `OCPP.Core.Database/OCPPCoreContext.cs`, entity classes, and `OCPP.Core.Database/Migrations/`.
- Owner reports/email: `OCPP.Core.Management/Services/OwnerReportService.cs`, `EmailSender.cs`, report controllers.
- Extensions: `OCPP.Core.Server.Extensions/Interfaces/`, `OCPPMiddleware.Extensions.cs`, and `Extensions/`.
- Local/E2E validation: `OCPP.Core.Server.Tests/`, `Simulators/`, `.github/workflows/`.
