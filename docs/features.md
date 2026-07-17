# Features

## OCPP Server

Code locations:

- `OCPP.Core.Server/OCPPMiddleware*.cs`
- `OCPP.Core.Server/ControllerOCPP*.cs`
- `OCPP.Core.Server/Messages_OCPP*`
- `OCPP.Core.Server/Schema*`

Known behavior:

- Accepts WebSocket connections under `/OCPP/{chargePointId}`.
- Supports WebSocket subprotocols `ocpp1.6`, `ocpp2.0.1`, and `ocpp2.1`.
- Rejects unknown charge point identifiers.
- Supports optional stored charge point basic auth and client certificate thumbprint checks.
- Maintains live charge point status in memory.
- Handles incoming boot, heartbeat, authorize, status, meter, transaction, data transfer, firmware/log status, charging-limit/profile, reset, unlock, and reservation-adjacent messages depending on protocol.
- Can optionally validate incoming messages against bundled JSON schemas when `ValidateMessages` is enabled.
- Can dump raw OCPP messages to `MessageDumpDir`.

Important edge cases:

- OCPP 1.6 token/idTag length is constrained during remote start.
- Unknown or disconnected charge points cannot receive remote commands.
- Reservation profile operations are intentionally disabled in middleware comments/code.
- Message validation logs schema errors and continues rather than hard failing all messages.

## Server API

Code location: `OCPP.Core.Server/OCPPMiddleware.cs`

Known API areas:

- `/API/Status`
- `/API/Reset/{chargePointId}`
- `/API/UnlockConnector/{chargePointId}/{connectorId}`
- `/API/SetChargingProfile/{chargePointId}/{connectorId}/{limit}`
- `/API/ClearChargingProfile/{chargePointId}/{connectorId}`
- `/API/GetConfiguration/{chargePointId}/{key}`
- `/API/ChangeConfiguration/{chargePointId}/{key}/{value}`
- `/API/StartTransaction/{chargePointId}/{connectorId}/{token}`
- `/API/StopTransaction/{chargePointId}/{connectorId}`
- `/API/Payments/...`

Known behavior:

- API routes require `X-API-Key` when `ApiKey` is configured.
- Stripe webhook handling is the exception to server API key checking.
- Remote commands are dispatched to protocol-specific methods based on the connected charge point protocol.

Unknown / verify:

- Whether these routes are considered public API contracts or internal app-to-app endpoints.

## Operator Management Portal

Code locations:

- `OCPP.Core.Management/Controllers/HomeController.*.cs`
- `OCPP.Core.Management/Controllers/ApiController.*.cs`
- `OCPP.Core.Management/Views/`
- `OCPP.Core.Management/Resources/`

Known behavior:

- Cookie-based login from configured users.
- Localized MVC UI with supported cultures configured in startup.
- Charge point, connector, charge tag, owner, transaction, and report views.
- Remote start, stop, reset, unlock, charging profile, and configuration actions by calling the server API.
- CSV and XLSX export paths for charge and owner reports.
- Public portal settings editor.
- Reverse proxy forwarded headers are trusted so generated URLs can respect public HTTPS/proxy headers.

Important edge cases:

- Management live status depends on `ServerApiUrl` and matching `ApiKey`.
- Operator actions that need live charger state can fail when the charger is disconnected from the server instance.

## Public Charging Portal

Code locations:

- `OCPP.Core.Management/Controllers/PublicController.cs`
- `OCPP.Core.Management/Controllers/PaymentsController.cs`
- `OCPP.Core.Management/Views/Public/`
- `OCPP.Core.Management/Views/Payments/`
- `OCPP.Core.Management/wwwroot/css/public-portal.css`
- `OCPP.Core.Management/wwwroot/js/public-portal.js`

Known behavior:

- Anonymous public map and start pages.
- Routes include `cp/{cp}` and `cp/{cp}/{conn:int}`.
- Connector selection, busy/offline handling, and recovery cookie handling.
- Public payment redirect/status flow.
- Public stop request path.
- R1/company invoice data submission with OIB validation.
- Public map, start, payment result, and status pages support the language selector for visible step, connector, pricing, session-status, known validation/error, recovery-copy, and default portal-branding text.
- Configurable branding, SEO, QR scanner, light theme, support, and footer settings.
- The checked PWA manifest and favicon assets use the public `EV.Charge` app name and icon.
- Customer notification emails use bilingual Croatian/English templates and are not currently tied to the public portal language selector.

Important edge cases:

- Public start depends on database charge point settings, connector status, server API availability, and payment configuration.
- Recovery cookies are scoped to reconnect users to in-progress reservations.
- Idle-fee window display comes from config/database settings.

## Payments and Reservations

Code locations:

- `OCPP.Core.Server/Payments/`
- `OCPP.Core.Server/OCPPMiddleware.cs`
- `OCPP.Core.Database/ChargePaymentReservation*.cs`
- `OCPP.Core.Database/StripeWebhookEvent.cs`
- `OCPP.Core.Server.Tests/*Payment*Tests.cs`

Known behavior:

- Stripe can be enabled/disabled by configuration.
- Mock Stripe services are available for local/test flows.
- Payment reservations lock connectors during pending/authorized/start windows.
- Hosted cleanup abandons stale pending reservations and marks start timeouts.
- Public payment status exposes reservation and transaction state.
- Idle fee calculation and idle warning emails are supported.
- Sessions with missing, inconsistent, or below-threshold delivered energy are treated as no-charge sessions under `Payments:MinimumSessionFeeKwh` (default `1.0` kWh). The uncaptured payment intent is cancelled, billable line amounts are zeroed, and invoice integration plus paid-completion emails are skipped.
- Positive final capture amounts at or above the delivered-energy threshold but below `Payments:MinimumChargeAmountCents` (default `50`) are cancelled before Stripe capture. Exactly the configured minimum remains capturable; invoice integration and completion emails only run after a successful paid completion.
- Free-tag access can bypass paid flow for configured tag/charge point combinations.

Important edge cases:

- Reservation, transaction, connector, and Stripe state must stay synchronized.
- Cleanup services run on intervals and can change visible state after timeouts.
- Server API and UI status pages must be validated together after payment changes.

## Invoice and Email Integrations

Company invoice requests support a confirmed reservation-bound buyer snapshot. The public start page collects and validates the complete buyer details before creating Stripe Checkout, so a session cannot finish before the invoice intent and buyer snapshot exist. Croatian companies retain strict OIB checksum validation. Foreign companies provide an ISO two-letter country, legal name and address, billing email, a required free-form tax/VAT/company identifier, and an optional legal registration number. Foreign identifiers are treated as user-supplied and unverified; registry or VIES availability does not block payment or invoice issuance. First confirmation is concurrency-protected, and customer-side edits are rejected after confirmation or provider submission; corrections use the provider-supported correction, storno, or reissue path.

The public start page does not retain reusable buyer details in browser storage. Ordinary validation failures preserve the submitted values through the server-rendered form response. Stripe Checkout and PaymentIntent receive a bounded metadata copy for payment reconciliation; e-racuni issuance uses the durable reservation snapshot as its source of truth.

The e-racuni payload maps supported buyer name, street, postal code, city, country, tax identifier, email, and explicit VAT-registration status. Legal registration numbers remain in the internal confirmed snapshot because the provider contract has no dedicated supported field; they are not mapped into `buyerCode`.

Failed provider validation is exposed on payment status only as a bounded customer-safe message. Raw provider bodies, credentials, and buyer diagnostics remain outside the public response.

Code locations:

- `OCPP.Core.Server/Payments/Invoices/`
- `OCPP.Core.Server/Payments/EmailNotificationService.cs`
- `OCPP.Core.Management/Services/EmailSender.cs`
- `OCPP.Core.Management/Services/OwnerReportService.cs`

Known behavior:

- e-racuni invoice integration is configurable under `Invoices`.
- Invoice modes include disabled/log-only style behavior inferred from options and tests.
- Customer notification emails can use SMTP or a sink directory.
- Owner reports can be generated as workbooks and sent on a Hangfire recurring schedule when enabled.

Unknown / verify:

- Production invoice mode and provider configuration.
- Required legal/tax fields for each deployment.

## Extensions

Code locations:

- `OCPP.Core.Server.Extensions/Interfaces/`
- `OCPP.Core.Server/OCPPMiddleware.Extensions.cs`
- `Extensions/OCPP.Core.Extensions.AzureServiceBus/`
- `Extensions/OCPP.Core.Extensions.Authorization/`

Known behavior:

- Raw incoming/outgoing messages can be forwarded to extension sinks.
- External authorization extensions can explicitly allow, deny, or defer to default logic.
- Sample Azure Service Bus extension reads its own appsettings file from the extension output directory.
- Sample authorization extension reads simple allow/deny token values from its extension appsettings file.

## Tests and Tooling

Code locations:

- `OCPP.Core.Server.Tests/`
- `OCPP.Core.Test/`
- `Simulators/`
- `.github/workflows/`
- `scripts/check-mssql-migration-metadata.sh`

Known behavior:

- xUnit tests cover many server, payment, portal, invoice, and management behaviors.
- Node simulators exercise OCPP protocols and payment/public flows.
- Playwright tests validate public portal status and UI behavior.
- CI runs migration metadata guard, restore, build, and server tests on pushes and pull requests.
- Main branch CI also runs E2E/browser regressions and Docker image publishing workflows.
