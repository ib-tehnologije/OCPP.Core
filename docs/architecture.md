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
- `Payments/` contains the provider-neutral payment coordinator (`PaymentCoordinator`), reservation lifecycle, public payment API behavior, idle fee calculation, invoice integration, notification email logic, and maintenance jobs. `Payments/Providers/` holds the payment provider gateways (Stripe and Worldline Saferpay), the gateway registry, and their DI registration.

### Payment Providers

Location: `OCPP.Core.Server/Payments/Providers/`. The decision is recorded in [ADR 0005](decisions/0005-pluggable-payment-providers.md).

- **Gateway contract.** `IPaymentGateway` is the only provider-specific seam. It covers creating a hosted checkout, reading a checkout or an authorization, capture, cancel, metadata mirroring, parsing an inbound webhook or notification, and sanitizing provider text.
  - Gateways map provider objects to neutral snapshots (`PaymentCheckoutStatus`, `PaymentAuthorizationSnapshot`) and throw `PaymentProviderException` with a stable `Code` and an `IsTransient` flag.
  - Capability flags tell the coordinator what a provider supports: `SuccessUrlSessionPlaceholder`, `SupportsMetadata` and `SupportsDeferredCheckoutLookup`.
  - Default interface members cover later provider differences without changing existing gateways: `CheckoutLifetime` (Saferpay payment page: `Saferpay:CheckoutLifetimeMinutes`, default 60; release reconciliation treats an open page as closed only after this lifetime plus 5 minutes from the reservation's creation, and pending cancels and late browser returns check the page within it), `CapturedCheckoutRequiresRefund` (Saferpay: an authorization already captured at checkout fails the reservation with `captured_at_checkout` and needs a manual refund) and `FindAuthorizationByReservation` (Saferpay `Transaction/Inquire` by order id once the page is closed or its lifetime is over). Stripe keeps the neutral defaults.
  - Calls are synchronous because the coordinator's lifecycle methods are synchronous.
- **Implementations.**
  - `Stripe/StripePaymentGateway` (Checkout Session plus a manual-capture PaymentIntent) and its mock services.
  - `Saferpay/SaferpayPaymentGateway`: Payment Page `Initialize`/`Assert` plus Transaction `Capture`/`Cancel`/`Inquire`, called through a named `HttpClient` with basic authentication and `Connection: close`.
  - `Saferpay/MockSaferpayApiHandler`, an in-process fake of the Saferpay JSON API used when `Saferpay:UseMockServices` is true.
- **Registry and DI.** `AddPaymentProviders` binds `Payments`, `Stripe` and `Saferpay`, resolves the provider-neutral `PaymentCheckoutSettings`, registers both gateways, and builds `PaymentGatewayRegistry`.
  - The registry resolves provider names case-insensitively.
  - `Active` is the gateway selected by `Payments:Provider`, used for new checkouts.
  - `Resolve(name)` returns the gateway that owns an existing reservation. Null or empty resolves to Stripe; an unknown name resolves to nothing.
  - An unknown `Payments:Provider` value fails while services are configured.
  - `OCPP.Core.Recovery` builds its services from the same `Startup`, so it inherits this behavior.
- **Per-reservation routing.** A new checkout stores `ChargePaymentReservation.PaymentProvider`. The reservation's own gateway handles every later operation: confirm, resume, cancel, capture, authorization release, webhook processing and recovery settlement.
  - A reservation whose provider is unregistered or unconfigured fails closed with the non-transient codes `provider_not_registered` or `provider_not_configured`. It is never routed to another provider.
  - Operations on existing reservations need only the return base URL and the owning gateway to be configured. Stripe rows therefore keep settling after Saferpay becomes active.
  - `IPaymentCoordinator.IsEnabled` (return base URL and active gateway configured) gates only new checkouts. `IsOperational` (return base URL and any gateway configured) gates the payment API actions on existing reservations, the idle-fee warning sweep and explicit financial recovery. `AddPaymentProviders` logs a startup warning when the active gateway is not configured while another one is.
- **Reference columns.** `StripeCheckoutSessionId` and `StripePaymentIntentId` keep their names and hold the owning provider's checkout reference (Stripe Checkout Session id, Saferpay payment page token) and authorization reference (Stripe PaymentIntent id, Saferpay transaction id).
- **Payer details.** `PayerEmail` and `PayerName` are persisted when the checkout is confirmed or notified. Providers whose checkout is not read again later (`SupportsDeferredCheckoutLookup=false`, Saferpay) depend on them for authorization e-mails, idle-fee warnings and invoices. Stripe can still read the checkout later; the persisted fields are its fallback. Without any payer e-mail, customer e-mails go to the R1 invoice e-mail (`InvoiceBuyerEmail`).
- **Confirmation without a checkout reference.** Providers without `SuccessUrlSessionPlaceholder` (Saferpay) can be confirmed with the reservation id alone. The coordinator therefore checks only `Pending` reservations with the provider, throttles that check to one per reservation every 2 seconds (in memory, per server instance), and sends the authorization e-mail only on the change to `Authorized`. Resume of such a reservation uses the same throttle. The browser return and a notification can race; the change to `Authorized` and the charge tag (`OcppIdTag`) claim are one conditional update, so only one of them authorizes the reservation and both continue with the persisted tag.
- **Webhook routing.** The middleware maps `/API/Payments/Webhook` (legacy, Stripe) and `/API/Payments/Webhook/{provider}` to `IPaymentCoordinator.HandleWebhookAsync`, passing a neutral `PaymentWebhookRequest` (provider segment, method, query, headers, body).
  - The middleware checks the method: Stripe accepts `POST` only, other segments accept `GET` or `POST`.
  - For compatibility with endpoints registered before provider routing, a `POST` with a `Stripe-Signature` header to an unknown provider segment or a deeper path is passed on as the legacy Stripe route (`Provider = null`), and a deprecation warning is logged once per process. Without that header such paths return `404`.
  - The coordinator picks the gateway, lets it authenticate and parse the request, and processes the neutral events idempotently.
  - It answers with the status code the PSP should see: `404` for an unknown provider, `400` for a rejected Saferpay notification, `503` for a transient failure.
  - It returns the ids of the reservations it verified and saved as `Authorized`. The middleware first sets the status code and completes the response (`HttpResponse.CompleteAsync`), so the PSP is acknowledged before any charger wait. It then reloads each of those rows in the same request scope and starts charging only when the row is still `Authorized` and no start is under way. The raw request body never drives a start.
- **Conditional status transitions.** Reservation status changes that can race (every exit from `Pending`, cancels, the stale-pending, startup and start-timeout sweeps, settlement failures and the release lease) use `PaymentCoordinator.TryCommitReservationTransition`. On relational providers it writes the tracked changes in one `UPDATE ... WHERE Status = @expected` (plus an optional extra condition), keeps the SQLite `ActiveConnectorKey` in step, and can claim `OcppIdTag` in the same statement. When no row matches, it reloads the entity and the caller acts on the winner's state, for example by releasing an authorization that arrived for a reservation the sweep had abandoned. For an authorization the `ChargeTag` row is created first, in a separate context, so a failure leaves the reservation `Pending`. The EF in-memory provider falls back to `SaveChanges`.
- **One remote start per reservation.** Confirm, Resume and the webhook auto-start share `OCPPMiddleware.TryStartPaymentReservationAsync`. It re-reads the reservation and starts only an `Authorized` row without a linked transaction that is not waiting for the plug-in trigger and has no remote start result within `Payments:ChargerResponseTimeoutMs`. `TryStartChargingAsync` additionally allows one remote start per reservation at a time. That guard is in memory, which is sufficient because only the instance holding the charger's WebSocket can send the start. Resume answers before it starts, like the webhook.

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
- `ChargePaymentReservation*`, `PaymentAuthorizationReleaseAttempt`, `StripeWebhookEvent`, and `InvoiceSubmissionLog` support payment/invoice lifecycle state and release auditability. `ChargePaymentReservation.PaymentProvider` names the provider that owns each reservation. `StripeWebhookEvent` keeps its historical name and deduplicates webhook events for every provider; ids from providers other than Stripe are prefixed with the provider name.

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
3. The server API checks `X-API-Key` unless the route is a payment provider webhook (`/API/Payments/Webhook` or `/API/Payments/Webhook/{provider}`). Those routes are authenticated by the provider gateway instead.
4. Middleware resolves the live WebSocket session and sends the appropriate OCPP request.
5. The management controller returns the server result to the UI.

### Public Payment Flow

1. A driver opens the public map/start page in the management app.
2. `PublicController` builds availability and pricing state from the database and server API.
3. A start request posts to server `Payments/Create`.
4. The server payment coordinator creates or resumes a reservation. It redirects the payer to the active provider's hosted payment page (Stripe Checkout or the Saferpay Payment Page), or in test mode to the management mock checkout, and records the provider on the reservation.
5. The payer returns to management `/Payments/Success`, which posts server `Payments/Confirm`. A verified webhook or Saferpay notification can authorize the reservation first. Either way the server re-reads the checkout from the reservation's provider, requests a charger start through the live OCPP session (once per reservation, while it is `Authorized`), and updates reservation and transaction state. If a Saferpay return was lost and no notification arrived, reopening the start page resumes the reservation, which confirms the paid checkout and starts it the same way.
6. Public status pages poll server payment status without caching, can request stop, and can update the reservation-bound R1 buyer snapshot before invoice issuance. The existing buyer confirmation timestamp is the optimistic concurrency version. Submitted/external evidence, an active submission lease, `Submitting`, `ProviderUnknown`, or an unavailable invoice-state lookup makes the form read-only. A serializable reservation-row lock orders buyer saves and durable invoice-lineage acquisition across application instances; non-relational test stores use the bounded in-process reservation gate.
7. When a newly terminal reservation still owns an uncaptured authorization, the server persists an armed release state before provider work. Webhooks and the cleanup sweep converge on the same strict, idempotent reconciler; retry state survives restarts and historical unarmed rows stay outside the workflow.
8. Exceptional financial recovery is isolated in `OCPP.Core.Recovery`. A manifest allowlists exact reservations, assessors fail closed on incomplete persisted evidence, and execute mode requires the exact manifest digest. Settlement reuses the payment coordinator while suppressing customer side effects; authorization release reuses the durable reconciler; invoice recovery uses a unique submission lineage, a time-bounded database lease, and a bounded provider date-window lookup matched on order reference and amount before create (ADR 0004).

### Database Flow

- Both apps use the shared EF Core model.
- SQL Server uses migrations when `AutoMigrateDB` is enabled.
- SQLite local/test runs use `EnsureCreated()` in server startup because migrations are SQL Server-oriented.

## Integration Points

- Stripe checkout, payment intents, and webhooks (default payment provider).
- Worldline Saferpay JSON API (Payment Page, Transaction capture/cancel/inquire) and its payload-free success/fail notifications (alternative payment provider, selected with `Payments:Provider`).
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
- Payment operations on an existing reservation always use the provider stored on that reservation. Changing `Payments:Provider` affects only new checkouts, and the previous provider must stay configured until its reservations are finished.
- Financial recovery is an explicit operator command, not an HTTP endpoint or hosted service. Keep manifests and private evidence outside the repository.
- A charge point WebSocket session reuses one request-scoped `OCPPCoreContext` for its whole lifetime. `ResetConnectionDbContext` clears its change tracker before every incoming OCPP message so stale entities (for example a reservation whose R1 buyer data the portal confirmed during charging) or changes left by a failed `SaveChanges` cannot break later messages on the same connection. Do not rely on entities tracked across messages.

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
- Payments/public charging: `OCPP.Core.Server/Payments/`, provider gateways in `OCPP.Core.Server/Payments/Providers/`, payment handlers in `OCPPMiddleware.cs`, `OCPP.Core.Management/Controllers/PublicController.cs`, `PaymentsController.cs`, public Razor views.
- Database schema: `OCPP.Core.Database/OCPPCoreContext.cs`, entity classes, and `OCPP.Core.Database/Migrations/`.
- Owner reports/email: `OCPP.Core.Management/Services/OwnerReportService.cs`, `EmailSender.cs`, report controllers.
- Extensions: `OCPP.Core.Server.Extensions/Interfaces/`, `OCPPMiddleware.Extensions.cs`, and `Extensions/`.
- Local/E2E validation: `OCPP.Core.Server.Tests/`, `Simulators/`, `.github/workflows/`.
