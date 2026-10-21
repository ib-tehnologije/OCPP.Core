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
| Maintenance | `Maintenance:PendingPaymentTimeoutMinutes`, `Maintenance:ReservationTimeoutMinutes`, `Maintenance:StatusReleaseMinutes`, `Maintenance:CleanupIntervalSeconds`, `Maintenance:IdleWarningSweepSeconds`, `Maintenance:AvailableStatusOpenTransactionGraceMinutes`, `Maintenance:AuthorizationReleaseMaxAttempts`, `Maintenance:AuthorizationReleaseRetryBaseMinutes`, `Maintenance:AuthorizationReleaseInProgressTimeoutMinutes`, `Maintenance:SettlementRetryWindowMinutes` |
| Message retention | `Maintenance:MessageLogRetention:Enabled`, `Maintenance:MessageLogRetention:DryRun`, `Maintenance:MessageLogRetention:RetentionDays`, `Maintenance:MessageLogRetention:BatchSize`, `Maintenance:MessageLogRetention:CleanupIntervalMinutes` |
| Payments | `Payments:Provider`, `Payments:ReturnBaseUrl`, `Payments:Currency`, `Payments:ProductName`, `Payments:RequirePreparingBeforeRemoteStart`, `Payments:RemoteStartIdTokenType`, `Payments:StartWindowMinutes`, `Payments:MinimumSessionFeeKwh`, `Payments:MinimumChargeAmountCents`, `Payments:IdleFeeExcludedWindow`, `Payments:IdleFeeExcludedTimeZoneId`, `Payments:NightTariffTimeZoneId`, `Payments:IdleAutoStopMinutes`, `Payments:ChargerResponseTimeoutMs`, `Payments:Vies:Enabled`, `Payments:Vies:TimeoutSeconds` |
| Stripe | `Stripe:Enabled`, `Stripe:UseMockServices`, `Stripe:ApiKey`, `Stripe:WebhookSecret`, `Stripe:AllowInsecureWebhooks`, `Stripe:Currency`, `Stripe:ReturnBaseUrl`, `Stripe:ProductName`, `Stripe:MockCustomerEmail`, `Stripe:MockDiagnosticsDirectory` |
| Saferpay | `Saferpay:Enabled`, `Saferpay:BaseUrl`, `Saferpay:CustomerId`, `Saferpay:TerminalId`, `Saferpay:ApiUsername`, `Saferpay:ApiPassword`, `Saferpay:SpecVersion`, `Saferpay:NotifyBaseUrl`, `Saferpay:NotifySecret`, `Saferpay:PreAuth`, `Saferpay:PaymentMethods`, `Saferpay:Wallets`, `Saferpay:LanguageCode`, `Saferpay:ConfigSet`, `Saferpay:Condition`, `Saferpay:CollectPayerEmail`, `Saferpay:TimeoutSeconds`, `Saferpay:CheckoutLifetimeMinutes`, `Saferpay:UseMockServices`, `Saferpay:MockCheckoutBaseUrl`, `Saferpay:MockPayerEmail`, `Saferpay:MockAssertOutcome`, `Saferpay:MockInquireNotFoundSeconds` |
| Notifications | `Notifications:EnableCustomerEmails`, `Notifications:IdleWarningLeadMinutes`, `Notifications:SinkDirectory`, `Notifications:FromAddress`, `Notifications:FromName`, `Notifications:ReplyToAddress`, `Notifications:BccAddress`, `Notifications:Smtp:*` |
| Invoices | `Invoices:Enabled`, `Invoices:Provider`, `Invoices:Mode`, `Invoices:ERacuni:*` |
| Management portal | `Users`, `PublicPortal:*`, `Email:*`, `OwnerReportSchedule:*`, `ServerApiTimeoutSeconds`, `Payments:Provider` (admin banner only) |
| Hangfire | `Hangfire:EnableDashboard`, `Hangfire:DashboardPath`, `Hangfire:Queue` |
| Kestrel | `Kestrel:Endpoints:*` |
| Sentry | `Sentry:Dsn` or `SENTRY_DSN` |

The checked `appsettings.json` files include development/sample values. Override secret-like values for any real run.

`Payments:StartWindowMinutes` is applied when payment authorization succeeds and stored as an absolute UTC deadline on the reservation. Confirmation and webhook authorization paths use the same configured value. A reservation remains eligible before that stored deadline and expires at the deadline; later configuration changes do not rewrite already-persisted deadlines.

## Payment Providers

The server supports two payment service providers (PSPs): Stripe, the default, and Worldline Saferpay. Both gateways are always registered. `Payments:Provider` only chooses the provider for new checkouts, so reservations created under the previous provider can still finish after a switch. [ADR 0005](decisions/0005-pluggable-payment-providers.md) records the design. Switching procedures and known gaps are in [Maintenance](maintenance.md#switching-payment-providers).

### Selecting the provider

- `Payments:Provider` (server) selects the provider for new checkouts. Leave it empty or set `Stripe` (the default), or set `Saferpay`. Matching ignores case and surrounding whitespace. Any other value throws while services are being configured, so the server and the `OCPP.Core.Recovery` command refuse to start. A typo never falls back to another provider.
- Each reservation records the provider that created it in `ChargePaymentReservation.PaymentProvider`. That provider handles every later step for the reservation: confirm, resume, cancel, capture, authorization release, webhooks or notifications, and financial recovery. Rows created before the column existed are `Stripe`. A reservation whose provider is unknown or not configured fails closed and is never sent to another provider.
- If the selected provider is not configured, startup still succeeds but new checkouts are refused: `Payments/Create` answers `{"status":"Disabled"}`. Examples: `Saferpay:Enabled=false`, Saferpay without credentials or with a non-`https` `Saferpay:BaseUrl`, or Stripe without `Stripe:ApiKey`. When another provider is still configured, the server logs a startup warning that names the selected provider. It also logs a warning when a provider is configured but the return base URL is empty.
- Reservations that already exist keep working while any provider is configured (`IPaymentCoordinator.IsOperational`). `Payments/Confirm`, `Resume`, `Cancel`, `Stop`, `RequestR1` and `Status`, the idle-fee warnings and explicit financial recovery answer `Disabled` or stop only when no provider is configured. A reservation whose own provider is not configured still fails closed in the coordinator.
- The management app reads its own `Payments:Provider` for one thing only: deciding whether the "Stripe webhook secret is not set" admin banner applies. Keep it equal to the server value. It has no effect on routing.

### Provider-neutral checkout settings

These server keys apply to every provider. Each falls back to the older Stripe key, so existing deployments keep working without changes. When moving to Saferpay, set the `Payments:*` keys so the values no longer depend on the Stripe section.

| Key | Meaning | Fallback |
| --- | --- | --- |
| `Payments:ReturnBaseUrl` | Public base URL of the management app. Payer return URLs and status links in e-mails are built from it. Payments are disabled without it. | `Stripe:ReturnBaseUrl` |
| `Payments:Currency` | ISO 4217 reservation currency, stored in lower case. | `Stripe:Currency`, then `eur` |
| `Payments:ProductName` | Stripe line item name, or Saferpay payment description. | `Stripe:ProductName`. If both are empty, the name is `Charging session (<max kWh> kWh)`. |

### Payer return URL contract

The coordinator builds the payer return URLs from the return base URL:

- Success: `{ReturnBaseUrl}/Payments/Success?reservationId={id}`. For Stripe it adds `&session_id={CHECKOUT_SESSION_ID}`, which Stripe fills in. Public-portal starts add `&origin=public`.
- Cancel: `{ReturnBaseUrl}/Payments/Cancel?reservationId={id}[&origin=public]`. Only Stripe uses it.

Saferpay has a single return URL, so successful, declined and aborted payments all come back to `/Payments/Success`, without `session_id`. The management `Success` action accepts a missing `session_id` and posts `POST /API/Payments/Confirm` with `{"ReservationId": "...", "CheckoutSessionId": null}`. The server reads the checkout again from the reservation's own provider:

- Stripe requires `CheckoutSessionId`, and it must equal the stored Checkout Session id. Otherwise the result is `SessionMismatch`.
- Saferpay confirms with the stored payment page token. If `CheckoutSessionId` is supplied, it must still match that token.
  - Anyone who knows the reservation id can call this, so only a `Pending` reservation is checked with Saferpay. For a reservation that is already authorized and has not ended (for example `Authorized` or `Charging`), Confirm makes no Saferpay call. The server may still send a remote start, but only while the reservation is `Authorized` and no start is under way (see [Starting charging after payment](#starting-charging-after-payment)). The answer is that start attempt's result, or the stored state when nothing was started.
  - Saferpay is asked at most once every 2 seconds per reservation. A throttled call answers `400` with `{"status":"Pending","error":"Payment is still being processed."}`, which the management pages treat as "still in progress" (see [Management payment pages](#management-payment-pages)).
  - The authorization e-mail is sent only on the change from `Pending` to `Authorized`.
- A completed checkout with a held authorization moves the reservation to `Authorized`. An aborted payment moves a pending reservation to `Cancelled`. A declined payment moves it to `Failed`, with a sanitized provider message. A checkout that is still open or has expired leaves the reservation unchanged, and the status is shown to the payer. Public-origin payers see the public result page for failures as well.
- A Saferpay payment that was captured at checkout (a payment method without deferred capture) is refused. The reservation becomes `Failed` with `FailureCode=captured_at_checkout`, the captured amount is stored in `CapturedAmountCents`, no charging starts and no e-mail is sent. The payer must be refunded manually in the Saferpay back office.
- A browser return to a Saferpay reservation that already ended (for example after a portal cancel) re-checks the payment page while it can still be paid, and releases an authorization it finds there.
- A transient provider error during confirmation leaves the reservation pending. A retry, a provider notification, or the stale-pending sweep then finishes it. A non-transient error fails a `Pending` reservation and arms authorization release, so any hold is cancelled. For a reservation that is no longer `Pending` the error is returned and the reservation is not changed.

Other payer actions on a pending Saferpay reservation:

- `Payments/Resume` cannot reopen an open payment page, because its redirect URL is single-use, and answers `ResumeUnavailable`. If the page was already paid, Resume confirms the reservation instead and answers `Status`, so the payer continues on the status page, and charging starts (see below). Resume shares the 2-second throttle with Confirm. A throttled Resume makes no Saferpay call and answers `Status`, so the start page sends the payer to the polling status page instead of offering the cancel while the payment may be being confirmed.
- `Payments/Cancel` first reads the payment page. A held authorization is cancelled at once. A payment captured at checkout is recorded as `captured_at_checkout` instead of being cancelled. If the page is still open or cannot be read, the reservation is cancelled with its authorization release armed, so a late payment is found after the page lifetime (see [Saferpay settings](#saferpay-settings)).

Return URLs carry only the reservation id, the Stripe session id and the origin. Never add credentials or payer data to them.

### Starting charging after payment

For every provider, status changes out of `Pending` (authorize, decline, cancel, abandon) are conditional database updates on SQL Server and SQLite, so the first one wins and the others act on its result. A payment that is confirmed after the reservation was cancelled or abandoned is released as a late authorization and never started. When a browser return and a webhook or notification confirm the same payment, only one of them authorizes the reservation; the other continues with the stored result.

The browser return (`Payments/Confirm`), `Payments/Resume` when it answers `Status`, and the webhook or notification auto-start all use the same start rule. The server re-reads the reservation and sends a remote start only when all of these hold:

- The reservation is `Authorized` and has no linked transaction. A reservation that is `StartRequested`, `Charging` or `WaitingForDisconnect` is not started again (reason `AlreadyRequested`), one with a linked transaction neither (`AlreadyAdvanced`), and any other status is not started (`NotAuthorized`).
- The plug-in trigger does not own the start. A reservation that waits for the cable (`AwaitingPlug`) is started by the connector's `Preparing` status notification (reason `AwaitingPlug`). If the connector is already `Preparing`, the start is claimed with the same conditional update the trigger uses, so only one of them sends it.
- No remote start result was recorded for it within `Payments:ChargerResponseTimeoutMs` (default 60 seconds; reason `RecentRemoteStart`). `RemoteStartSentAtUtc` holds the time the charger's answer, or the timeout, was recorded. After a `Timeout` the reservation stays `Authorized`, and the charger may still start. The plug-in (`Preparing`) trigger also leaves such a reservation alone inside this window, so it keeps `AwaitingPlug` and a later `Preparing` notification can start it.
- No other remote start for the reservation is in progress (reason `StartInProgress`). This guard is in memory. Only the server instance that holds the charger's OCPP connection can send a remote start, so it sees every start for that charger. Inside the guard the reservation is read again right before the remote start is sent and the status, transaction and `RecentRemoteStart` checks are repeated, so a trigger that read it while another start was finishing sends nothing.

A skipped start answers with the reservation status and the reason; `Payments/Confirm` returns them as `status` and `reason`. Before this rule, a repeated or parallel return could send a second remote start while the first was in flight. If the charger rejected the duplicate, the reservation became `StartRejected` and its hold was cancelled, while the accepted start charged a session that could no longer be linked.

`Payments/Resume` answers first and starts afterwards, because the public start page waits only 30 seconds for it. The answer keeps its shape (`status`, `reservationId`, `reservationStatus`, `checkoutUrl`, `error`, `locksConnector`).

### Management payment pages

- `/Payments/Success` with the throttled `Pending` answer sends public payers to the status page, which polls, instead of an error page. The recovery cookie is kept, and the status page keeps it too while the reservation is `PendingPayment`, so the start page can still resume or cancel the checkout. Operator returns show the server message on the result page.
- `/Payments/Cancel` shows "Payment cancelled." only when the server reports `cancellationApplied` (or `Cancelled`). Otherwise:

  - An active session (`Authorized`, `StartRequested`, `Charging` or `WaitingForDisconnect`) sends public payers to the status page. Operators see the status on the result page.
  - Any other status is shown with the server's `message`. The server `Payments/Cancel` answer carries the reservation's `FailureMessage` there when the cancel was not applied, for example the refund notice of a payment captured at checkout. Without a message the page says the session can no longer be cancelled.
  - Without a usable answer, or while the reservation is still pending, the page shows a generic error and keeps the recovery cookie.

### Webhook and notification endpoints

| Route | Methods | Used by | Authentication |
| --- | --- | --- | --- |
| `/API/Payments/Webhook` | `POST` | Stripe (legacy route, so existing Stripe endpoints keep working) | `Stripe-Signature` header verified with `Stripe:WebhookSecret` |
| `/API/Payments/Webhook/Stripe` | `POST` | Stripe | `Stripe-Signature` header verified with `Stripe:WebhookSecret` |
| `/API/Payments/Webhook/Saferpay` | `GET`, `POST` | Saferpay success and fail notifications | `reservationId` and HMAC `sig` query parameters issued by this server |

- These routes skip the `X-API-Key` check, because PSPs cannot send the key. They must be publicly reachable through the reverse proxy. Every other `/API/...` route still requires `X-API-Key` when `ApiKey` is configured.
- The provider segment is case-insensitive. An unknown provider returns `404`, and so does any path below `/Webhook/{provider}`. A method that is not listed returns `405`.
  - Exception for Stripe endpoints registered before provider routing: a `POST` with a `Stripe-Signature` header to an unknown provider segment or a deeper path (for example `/API/Payments/Webhook/prod`) is handled as the legacy route `/API/Payments/Webhook`. The server logs a deprecation warning once per process. Move such endpoints to `/API/Payments/Webhook/Stripe`.
- Status codes:
  - `200` for a processed, ignored or duplicate call, and when the provider is registered but not configured.
  - `400` for a Saferpay notification with a missing or invalid `reservationId` or `sig`, or an `outcome` value other than `success` or `fail`. A missing `outcome` means success; only the fail URL carries `outcome=fail`.
  - `503` for a transient provider failure, including a failed release of a late authorization. The event is not marked processed, so the PSP's retry is evaluated again.
  - `500` for an unexpected error, for example a database failure while a Saferpay notification is processed. Saferpay then delivers the notification again.
  - A Stripe event with a missing or invalid signature is logged and answered with `200` without being processed, as before.
- Charging starts from a webhook only for reservations that the coordinator has verified with the provider and saved as `Authorized`. The start trigger is labelled `StripeWebhook` or `SaferpayWebhook`. The raw request body is never used to start charging.
  - The server answers first. It sets the status code and completes the response, and only then starts charging. A remote start can wait for the charger for up to `Payments:ChargerResponseTimeoutMs`, far longer than the PSP's own timeout (Saferpay documents 10 seconds), so a slow charger no longer makes the PSP retry. The start still runs in the same request scope, re-reads each reservation and starts it only while it is still `Authorized` and no start is under way (see [Starting charging after payment](#starting-charging-after-payment)).
- Duplicate suppression uses the existing `StripeWebhookEvent` entity for every provider. The name predates provider selection.
  - Stripe event ids are stored unchanged; ids from other providers are prefixed with `{Provider}:`.
  - Saferpay notifications have no event id, so nothing is recorded for them. A repeated notification is idempotent because the reservation state is checked again.
- Saferpay notifications come from Saferpay's infrastructure and carry no payload, signature or authentication of their own. The server trusts nothing in the call except its own signed reservation id. It then calls Saferpay `PaymentPage/Assert` for that reservation's stored token before changing any state.

### Stripe settings

- Supply `Stripe:ApiKey` and `Stripe:WebhookSecret` through user secrets or environment variables (`Stripe__ApiKey`, `Stripe__WebhookSecret`). The management admin banner names `Stripe__WebhookSecret`, which is the correct environment variable. Earlier banner text named an environment variable that the applications never read.
- Point the Stripe webhook endpoint at `https://<server>/API/Payments/Webhook/Stripe` (or the legacy `/API/Payments/Webhook`). Enable these events: `checkout.session.completed`, `checkout.session.expired`, `payment_intent.payment_failed` and `payment_intent.amount_capturable_updated`.
- Without `Stripe:WebhookSecret`, webhooks are rejected unless `Stripe:AllowInsecureWebhooks=true`. That setting is for local tests only.
- Stripe Checkout collects the payer e-mail itself. The coordinator can read checkout details from Stripe later, for receipts and invoices.

### Saferpay settings

| Key | Default | Meaning |
| --- | --- | --- |
| `Saferpay:Enabled` | `false` | Turns the gateway on. It must stay on while Saferpay reservations are in flight, including after switching to another provider. |
| `Saferpay:BaseUrl` | `https://test.saferpay.com/api` | JSON API base URL. Live: `https://www.saferpay.com/api`. Outside mock mode it must be an `https` URL, because requests carry the basic-authentication credentials; otherwise the gateway counts as not configured. |
| `Saferpay:CustomerId` | empty | Customer id, the first part of the Saferpay account id (`123456` in `123456-12345678`). |
| `Saferpay:TerminalId` | empty | 8-digit terminal id. |
| `Saferpay:ApiUsername`, `Saferpay:ApiPassword` | empty | JSON API basic-authentication user. Secret. |
| `Saferpay:SpecVersion` | `1.54` | JSON API specification version sent in every request header. |
| `Saferpay:NotifyBaseUrl` | empty | Public base URL of this server's `/API` endpoint, for example `https://ocpp.example.com/API`. Strongly recommended in production. Use `https`: the URLs carry the notification signature. Outside mock mode an `http` value is still sent, and a warning is logged once. See the notes below the table. |
| `Saferpay:NotifySecret` | empty | HMAC key for notification URLs. When empty, a key is derived from `ApiPassword` and `CustomerId`; set it explicitly in production (and always in mock mode, where no real password exists, the derived key is predictable). Secret. See the notes below the table. |
| `Saferpay:PreAuth` | `true` | Requests a pre-authorization instead of a final authorization. See [Going live](#going-live-with-saferpay). |
| `Saferpay:PaymentMethods` | empty, which sends `VISA`, `MASTERCARD`, `MAESTRO` | Payment method filter. A non-empty list replaces the default. Without any filter Saferpay would offer every method enabled on the terminal, including methods that capture at checkout, so an empty list sends the default card brands, which support an authorization with a later capture. Do not add payment methods that capture at checkout: the server refuses such payments (the reservation becomes `Failed` with `FailureCode=captured_at_checkout`), charging does not start, and the payer must be refunded manually in the Saferpay back office. |
| `Saferpay:Wallets` | empty | Optional wallet filter, for example `APPLEPAY`, `GOOGLEPAY`, sent as configured. Wallets pay with a card, so they support the later capture. |
| `Saferpay:LanguageCode` | empty | Payment page language (ISO 639-1, for example `hr`). When empty, Saferpay uses the browser language. |
| `Saferpay:ConfigSet` | empty | Optional payment page configuration set from the Saferpay back office. |
| `Saferpay:Condition` | empty | Optional 3-D Secure condition: `NONE`, `THREE_DS_AUTHENTICATION_SUCCESSFUL_OR_ATTEMPTED` or `WITH_SUCCESSFUL_THREE_DS_CHALLENGE`. |
| `Saferpay:CollectPayerEmail` | `true` | Asks for the payer e-mail on the payment page. See the notes below the table. |
| `Saferpay:TimeoutSeconds` | `100` | HTTP timeout for each JSON API request. `0` or less means 100. |
| `Saferpay:CheckoutLifetimeMinutes` | `60` | How long a payer can still pay a payment page after it was created. Saferpay keeps pages for up to one hour. Release reconciliation waits until this lifetime plus 5 minutes, counted from the reservation's creation, before it treats an open page as closed. `0` or less means 60. |
| `Saferpay:UseMockServices` | `false` | Uses an in-process fake of the JSON API. See [Mock mode](#payment-provider-mock-mode). |
| `Saferpay:MockCheckoutBaseUrl` | empty | Mock mode only: base URL of the management app that hosts `/Payments/MockCheckout`. |
| `Saferpay:MockPayerEmail` | empty (`driver@example.test`) | Mock mode only: the payer e-mail the fake reports. |
| `Saferpay:MockAssertOutcome` | empty (`Authorized`) | Mock mode only: what `PaymentPage/Assert` reports for a page without a transaction yet. `Authorized` creates an authorized transaction; `Captured` creates a transaction captured at checkout; `Declined`, `Aborted` and `InProgress` return `TRANSACTION_DECLINED`, `TRANSACTION_ABORTED` and `TRANSACTION_IN_WRONG_STATE` without a transaction. Case-insensitive; unknown values mean `Authorized`. Read on every Assert. |
| `Saferpay:MockInquireNotFoundSeconds` | `0` | Mock mode only: `Transaction/Inquire` answers `TRANSACTION_NOT_FOUND` for this many seconds after an authorization, like Saferpay's Inquire delay. Capture and Cancel are not delayed. |

Notes on the settings above:

- **Notification URLs.** With `Saferpay:NotifyBaseUrl` set, the notification URLs are `{NotifyBaseUrl}/Payments/Webhook/Saferpay?reservationId=...&sig=...`; the fail URL adds `&outcome=fail`. When it is empty, no notification URLs are sent, and the browser return and the cleanup sweep finish payments.
- **Set `Saferpay:NotifyBaseUrl` in production.** Without notifications the server learns about a payment only from the browser return or from its own lookups. A payment completed after the browser return was lost, or after the reservation was already cancelled or abandoned, is then detected only when release reconciliation re-checks the reservation after the payment page lifetime (`Saferpay:CheckoutLifetimeMinutes` plus 5 minutes) and looks the transaction up by order id. Until then the payer's hold stays in place. With notifications, such a late authorization is released as soon as Saferpay reports it. A transient failure of that release answers `503`, so Saferpay retries the notification.
- **Raise `Maintenance:PendingPaymentTimeoutMinutes` for Saferpay.** The Saferpay payment page usually takes longer than Stripe Checkout: the billing-address form (with `CollectPayerEmail`) and 3-D Secure add steps. The stale-pending sweep abandons a reservation that is still pending after this timeout (the checked `appsettings.json` sets `3` minutes; without the key, `Maintenance:ReservationTimeoutMinutes` applies, then `15`). A payer who is still on the page then pays for an abandoned reservation, and the hold is released instead of charging starting. A value of about 15 to 20 minutes avoids this. The trade-off is that a pending reservation keeps the connector locked, so a payer who walks away from the payment page blocks the connector for that long.
- **Rotating `Saferpay:NotifySecret`.** Changing it invalidates the notification URLs of payments still in progress. While it is empty, changing `ApiPassword` or `CustomerId` has the same effect. Those notifications are then rejected with `400`, and the browser return or cleanup finishes the payments.
- **`Saferpay:CollectPayerEmail`.** When on, Saferpay shows its billing-address form with only the e-mail mandatory. The e-mail Saferpay returns is stored as `PayerEmail` and used for the authorization e-mail, idle-fee warnings, completion e-mails and invoices.
  - When off, Saferpay shows no address form, and `PayerEmail` is set only when `PaymentPage/Assert` still returns an e-mail. That is a billing-address e-mail Saferpay echoes back (possibly the R1 e-mail prefilled from the start page, which has not been verified against the test environment) or a PayPal account e-mail. Apple Pay and Google Pay report none.
  - Without a payer e-mail, the authorization, idle-fee and completion e-mails fall back to the reservation's R1 invoice e-mail (`InvoiceBuyerEmail`) when the payer asked for an R1 invoice, and are skipped otherwise. The same fallback applies to Stripe reservations.

The Saferpay gateway counts as configured when `Saferpay:Enabled` is true and one of these holds:

- `Saferpay:UseMockServices` is true, or
- `BaseUrl`, `CustomerId`, `TerminalId`, `ApiUsername` and `ApiPassword` are all set, and `BaseUrl` is an `https` URL.

Never commit Saferpay credentials. Use user secrets in Development, for example `dotnet user-secrets set "Saferpay:ApiPassword" "<value>" --project OCPP.Core.Server`. Otherwise use environment variables: `Saferpay__ApiUsername`, `Saferpay__ApiPassword`, `Saferpay__NotifySecret`, `Saferpay__CustomerId` and `Saferpay__TerminalId`. Before persisting or showing errors, the gateway removes the API username, password, notify secret and basic-auth value, and redacts long identifier-like tokens.

### Saferpay test account setup

1. Request a Saferpay test account through the test back office sign-up (`https://test.saferpay.com/BO/SignUp`). The confirmation contains the test customer id, terminal id and back-office login.
2. Sign in to the test back office (`https://test.saferpay.com/BO/Login`). If no JSON API user was provided, create one under *Settings > JSON API basic authentication*. The password cannot be displayed again, so store it in a secret store immediately.
3. Configure the server:
   - `Payments:Provider=Saferpay` and `Saferpay:Enabled=true`.
   - Keep the test `Saferpay:BaseUrl`.
   - Supply `CustomerId`, `TerminalId`, `ApiUsername` and `ApiPassword` as secrets.
   - Set `Payments:ReturnBaseUrl` to the management URL that the payer's browser can reach.
   - Set the management app's `Payments:Provider=Saferpay` too.
4. For notifications, set `Saferpay:NotifyBaseUrl` to a public HTTPS URL of the server `/API` endpoint; a tunnel works for local tests. Optionally set `Saferpay:NotifySecret`. Without notifications, test with the browser return only.
5. Pay with Saferpay test cards from <https://docs.saferpay.com/home/integration-guide/testing-and-go-live>. Real cards are rejected on the test environment.
6. Check the results:
   - The reservation becomes `Authorized` and charging starts.
   - The final capture can be lower than the hold.
   - A no-charge session cancels the authorization.
   - The transaction appears in the test back office.

### Going live with Saferpay

- The Saferpay test and live environments are separate systems. Customer ids, terminal ids, API users and transactions do not carry over.
- Configure the live environment:
  - Set `Saferpay:BaseUrl=https://www.saferpay.com/api`.
  - Supply the live `CustomerId` and `TerminalId`.
  - Create a JSON API user in the live back office.
  - Set a new `NotifySecret`.
- Do not switch environments while Saferpay reservations created against the test environment are still in flight. The live API cannot find them, so they cannot be captured or released. Drain them first, as described in [Maintenance](maintenance.md#switching-payment-providers).
- `Saferpay:PreAuth=true` is recommended, because the final charging amount is usually lower than the held amount. Saferpay offers pre-authorization only for some card schemes, and the acquiring contract must enable it.
  - Confirm with the acquirer that pre-authorization is enabled for the merchant category, and how long a hold stays valid.
  - Set `PreAuth=false` (final authorization) only after the acquirer confirms that capturing less than the authorized amount is acceptable.
- Keep `Saferpay:PaymentMethods` empty (cards only) or list only methods with a deferred capture. Check the terminal's enabled methods in the live back office.

### Payment provider mock mode

- Stripe: `Stripe:Enabled=true` with `Stripe:UseMockServices=true` uses the existing mock Stripe services. They can restore state from `Stripe:MockDiagnosticsDirectory`.
- Saferpay: `Saferpay:Enabled=true` with `Saferpay:UseMockServices=true` replaces the JSON API with an in-process fake and needs no credentials.
  - The payer is redirected to the management page `/Payments/MockCheckout?reservationId=...&successUrl=...&cancelUrl=...&provider=Saferpay`. That page names the provider from the optional `provider` parameter and shows a checkout reference only when `session_id` is present.
  - `Saferpay:MockCheckoutBaseUrl` sets the management base URL. Without it, the host of the return URL is used, then `http://127.0.0.1:8082`.
  - The fake reports `Saferpay:MockPayerEmail`, by default `driver@example.test`.
  - `Saferpay:MockAssertOutcome` and `Saferpay:MockInquireNotFoundSeconds` are opt-in test aids: a payment captured at checkout, a decline, an abort, a page the payer has not finished, and Saferpay's Inquire delay. The defaults authorize at once and have no delay.
  - Its state lives only in memory and is lost when the server restarts.
- Use mock mode only for local simulator and browser runs.
- The local E2E harnesses (`Simulators/run_e2e_stack.mjs`, the Playwright stack, and `Simulators/run_local_regression.py`) read `OCPP_E2E_PAYMENT_PROVIDER`.
  - Empty or `Stripe` (the default) keeps the historical mock Stripe setup, and also pins `Payments:Provider=Stripe`, `Payments:ReturnBaseUrl` (the local management URL) and `Saferpay:Enabled=false`. The harnesses run in the Development environment and inherit the shell environment, so `Payments:*` or `Saferpay:*` values from user secrets or environment variables cannot switch the run. The scenarios also check that each checkout URL came from the selected mock: `session_id` and no `provider=Saferpay` for Stripe, `provider=Saferpay` for Saferpay.
  - `Saferpay` also sets `Payments:Provider=Saferpay`, enables the Saferpay mock, and points `Saferpay:NotifyBaseUrl` at the local server API. The mock does not call the notification URLs, so these runs exercise the browser return, not `/API/Payments/Webhook/Saferpay`. The Stripe mock stays configured.
  - `Simulators/tests/payment_provider_env.test.mjs` checks this environment: `node --test Simulators/tests/*.test.mjs`.
  - Any other value stops the harness. Example: `OCPP_E2E_PAYMENT_PROVIDER=Saferpay node Simulators/run_e2e_stack.mjs`.

### Invoices and provider references

- `Invoices:ERacuni:ReferencePrefix` defaults to `STRIPE`. It is joined with `-` to the reservation's authorization reference to form the e-racuni `reference`.
  - The authorization reference is the Stripe PaymentIntent id or the Saferpay transaction id. If it is missing, the checkout reference is used, then the reservation id.
  - The prefix does not change with `Payments:Provider`. Operators using Saferpay may want a different prefix, such as `SAFERPAY`.
  - The provider may replace `reference` with its own payment reference ([ADR 0004](decisions/0004-eracuni-invoice-lookup-by-date-window.md)). Invoice lookup matches on `orderReference`, not `reference`.
- The `RemarksTemplate` placeholders `{StripePaymentIntentId}` and `{StripeCheckoutSessionId}` keep their names. For Saferpay reservations they contain the Saferpay transaction id and the payment page token.

## Charger Reset Operations

The server reset endpoint is `GET /API/Reset/{chargePointId}/{mode?}`. It accepts `Hard` and `Soft` modes; an unsupported mode returns HTTP 400. When mode is omitted, the compatibility default is retained.

| Requested mode | OCPP 1.6 | OCPP 2.0.1 / 2.1 |
| --- | --- | --- |
| omitted | `Soft` | `OnIdle` |
| `Soft` | `Soft` | `OnIdle` |
| `Hard` | `Hard` | `Immediate` |

The management reset action requests `Hard`, which maps to OCPP 2.x `Immediate`. This adds no configuration key or deployment step.

## Database Operations

Confirmed company invoice buyer data is stored as nullable bounded columns on `ChargePaymentReservation`. Migration `AddInvoiceBuyerSnapshot` is non-destructive for existing reservations. Migration `AddInvoiceBuyerVatValidation` adds the original identifier, canonical VAT identifier, local validation status, VIES status, checked time, and bounded reference; its nullable columns do not rewrite historical rows. New public sessions must confirm the complete buyer snapshot before the provider checkout (Stripe Checkout or the Saferpay payment page) is created. Existing legacy R1 reservations can still build from Stripe metadata, while newly confirmed requests use the durable reservation snapshot as the invoice source of truth. The legacy buyer-data endpoint remains available for compatibility. The public status page now exposes that same endpoint as a prefilled correction form until invoice issuance; it creates no new database field or migration.

The public start and status pages do not retain reusable company-buyer details in browser storage. Submitted values survive only the ordinary server-rendered validation-error response; a new start-page visit begins with an empty buyer form. The uncached status page reads the authoritative snapshot for its reservation and includes `InvoiceBuyerConfirmedAtUtc` as the optimistic concurrency version. Identical retries are idempotent; changed requests with a missing or stale version are rejected and reload the latest snapshot. Confirmed-snapshot edits require the complete legal buyer fields even for Croatian buyers; the relaxed Croatian shape remains only for legacy first confirmation.

Foreign VAT identifiers are always normalized and structure-checked locally before the provider checkout is created. Optional VIES verification is disabled by default and can be enabled with `Payments:Vies:Enabled=true`; `Payments:Vies:TimeoutSeconds` is clamped to 1-10 seconds. A timeout, transport failure, malformed response, or VIES/member-state outage becomes `Unavailable` and must not block checkout or a pre-issuance correction. A changed canonical foreign VAT identity runs VIES again, while an unchanged identity preserves its prior result. The request contains only `countryCode` and `vatNumber`. Never add company names, addresses, billing emails, credentials, or requester identity fields to the VIES request. Generic foreign identifiers that are not marked as VAT registrations do not call VIES and retain their entered value.

Support can distinguish local `Valid` plus VIES `Valid`, `Invalid`, `Unavailable`, or `NotChecked` from the reservation fields. The public status API intentionally omits the provider reference and VAT number. Provider validation failures must remain sanitized in customer responses and logs; do not expose VIES response bodies, e-racuni credentials, or raw authenticated request envelopes while diagnosing invoice submission.

The public buyer-data endpoint and status form lock before the invoice provider boundary whenever a submission lease is active or the durable state is `Submitting` or `ProviderUnknown`; submitted status or any external document identifier, number, or URL remains permanently locked. Failure to query invoice state also locks the form rather than assuming that no invoice exists. On relational stores, edit saves and invoice draft/lineage acquisition first take a serializable no-op update lock on the same reservation row. Invoice submission reloads the buyer snapshot under that lock and persists its lease before releasing it, so another application instance either saves first and is included in the draft or observes the durable submission barrier. Corrections after issuance or an indeterminate provider result must use support and the provider-supported reconciliation, correction, storno, or reissue process rather than mutating the reservation snapshot. The R1 request email links to the existing reservation status URL and tells the customer that self-service corrections end at issuance.

Stripe Checkout and PaymentIntent metadata are reconciliation mirrors, not the invoice source of truth. A mirror failure after the reservation save returns `UpdatedMetadataPending` only when `IStripeBuyerMetadataReconciliationJob` is durably enqueued on the configured `Hangfire:Queue` (default `payments`). If enqueueing is unavailable or fails, the response is `UpdatedMetadataRetryRequired` and directs the customer to retry or contact support instead of implying that background work is pending. Hangfire retries a queued job until both mirrors contain the latest `buyer_data_version`; operators should investigate persistent retries or a deployment without durable Hangfire storage. Repeating the same customer save also retries the mirror without changing the authoritative version. Saferpay has no checkout metadata. For Saferpay reservations, a buyer-data save answers `Updated`, no reconciliation job is queued, and the reservation snapshot is the only copy.

SQL Server:

- EF migrations live in `OCPP.Core.Database/Migrations`.
- `AutoMigrateDB=true` applies migrations at server startup.
- `make dbupdate` applies migrations through EF tooling with production environment variables.
- `make add-migration NAME=AddSomething` scaffolds a named migration.
- `make migrate` scaffolds a timestamped migration name.
- Always run `make check-migration-metadata` after migration changes.

Migration `AddPaymentAuthorizationReleaseReconciliation` adds nullable release-state, timestamp, and error fields plus a zero-valued attempt counter to `ChargePaymentReservation`, together with an append-only `PaymentAuthorizationReleaseAttempt` audit table. Nullable state is intentional: existing terminal reservations remain unarmed, so deploying the migration does not start historical cancellation or remediation.

Migration `AddInvoiceSubmissionIdempotency` adds nullable `InvoiceSubmissionLog.SubmissionKey`, a filtered unique index, and nullable bounded lease identifier/expiry columns. Historical rows remain null and are not backfilled. New submit-mode attempts use the provider plus deterministic API transaction reference as their durable lineage and hold a five-minute database lease across the provider create boundary. A repeated, concurrent, or restarted attempt checks submitted/external local evidence first and performs the bounded provider pre-check described under [Explicit financial recovery](#explicit-financial-recovery) before another create. Transport errors, provider error statuses, truncated pages, unrecognized schemas or rows, out-of-window rows, amount or currency disagreement, and multiple order-reference matches remain `ProviderUnknown` and fail closed.

Migration `AddMeterEvidenceSafeguard` adds nullable accepted-meter projection, precision, physical-capacity evidence, and review-state columns to `Transactions`, plus the append-only `MeterEvidenceAnomaly` table with a unique replay key. Existing rows are not backfilled. New OCPP ingestion populates the projection; connector-Available closure validates its raw terminal candidate before settlement; and settlement retries revalidate any changed terminal candidate when projection/capacity evidence is present. Historical closed rows without projection data retain structural compatibility checks, and historical completed captures retain their already-persisted billing breakdown. All newly ingested or recovered transactions pass the meter-evidence boundary before capture.

Migration `AddMeterEvidencePowerCandidateProvenance` adds nullable candidate offered-power raw value, unit, and position-matched multiplier-encoding columns to `MeterEvidenceAnomaly`. Deploy it after `AddMeterEvidenceSafeguard`. Financial recovery settlement now requires a non-blank accepted meter projection and timestamp before any provider capture; a structurally valid meter delta alone is insufficient.

`MeterEvidence:FallbackMaximumPowerKw` (server, default `400`) is the physical capacity ceiling for chargers that do not report `Power.Offered`. Many chargers only sample energy, active power, and current by default, so without this fallback every positive-energy session from them would require review. Lower it only when every connected charger is known to stay below the value; set it to `0` to require offered-power evidence (strict mode). Check a charger's sampled measurands with the server API `GET /API/GetConfiguration/{chargePointId}/MeterValuesSampledData` (OCPP 1.6). No migration is involved.

Migration `AddPaymentProvider` adds three columns to `ChargePaymentReservation`:

- `PaymentProvider` (`nvarchar(32)`, not null, default `Stripe`), so every existing row is attributed to Stripe.
- `PayerEmail` (`nvarchar(254)`, nullable).
- `PayerName` (`nvarchar(200)`, nullable).

The payer columns are filled at authorization from the provider's checkout, which matters for providers such as Saferpay whose checkout is not read again later (see [ADR 0005](decisions/0005-pluggable-payment-providers.md)). Existing rows are not backfilled. The existing `StripeCheckoutSessionId` and `StripePaymentIntentId` columns keep their names and now hold the checkout and authorization references of the row's provider (for Saferpay, the payment page token and the transaction id). Apply the migration before deploying a server build that selects providers, and run `make check-migration-metadata`. SQLite files created before this change lack the columns; recreate them with `make sqlite-reset` (see [Maintenance](maintenance.md#switching-payment-providers)).

SQLite:

- Used for local/test runs.
- Server startup uses `EnsureCreated()` instead of SQL Server migrations.
- `make sqlite-reset` removes the configured local SQLite file and WAL/SHM files.

Unknown / verify:

- Whether `SQL-Server/*.sql` scripts are still authoritative for any supported deployment path.
- Whether checked SQLite files are intended long-term fixtures.

## Background Jobs and Startup Maintenance

Server app:

- `StartupMaintenance.Run` executes on startup to repair reservation active keys, abandon stale pending reservations, and release stale connector statuses. A pending reservation that changed after it was read (status or `UpdatedAtUtc`), for example because another instance confirmed it, is skipped.
- `PaymentReservationCleanupService` runs periodically to abandon stale pending reservations (arming their authorization release, so a hold the payer completed meanwhile is released through the provider; a reservation that changed after it was read is skipped), time out starts (the reservation is set to `StartTimeout` only while no transaction is linked, before its hold is cancelled), retry settlements marked `settlement_retry_pending` (see below), recover open transactions on available connectors, retry stuck `Charging` completion when an exact linked transaction is already stopped and the same connector has remained `Available` at or after that stop for the configured grace period, complete waiting-for-disconnect reservations, and retry due authorization releases that were explicitly armed by the application. Missing, older, non-available, cross-connector, or still-within-grace evidence is ignored.
- `IdleFeeWarningEmailService` periodically sends customer idle-fee warning emails when customer notifications are enabled and at least one payment provider is configured (`IsOperational`), so sessions of a previous provider still get warnings. The recipient comes from the provider's checkout when it can be read later (Stripe), then from the persisted `PayerEmail`, then from the R1 invoice e-mail (`InvoiceBuyerEmail`).
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

Before resolving services, the standalone command registers its built configuration with the service collection. Program-level tests cover both an empty-manifest dry-run and a synthetic invoice dry-run.

1. Confirm the database migration is applied through the ordinary deployment process. The command does not migrate a database.
2. Run without `--execute`. A dry-run performs local evidence checks only and prints a SHA-256 digest plus redacted reservation identifiers.
3. Review every decision. Settlement requires exact linked terminal transaction, ordered timestamps, valid meter delta, non-negative pricing snapshots, and a positive derived billable amount. Authorization release requires a reservation that ended without a charging session (`Abandoned`, `Failed`, `Cancelled`, `StartRejected` or `StartTimeout`), has a provider authorization reference that is not already released, and has no transaction, energy, captured funds, or invoice evidence. Invoice recovery requires a completed captured reservation and linked transaction.
   Authorization release may conclude that no matching transaction exists only when charge point, positive connector, charge tag, and an ordered persisted lower-bound-to-start-deadline window are all present. The lower bound is the authorization time when available, otherwise the earlier reservation creation time. Missing or invalid linkage evidence is indeterminate and stops dry-run and execution before any provider read or release action. Automatic release reconciliation applies the same rule, except to reservations that never reached `Authorized`: no authorization time, start deadline, remote start (sent, accepted or result), `AwaitingPlug`, transaction start time or transaction link. The charge tag is not considered. No transaction can belong to them, so they go on to the provider checks.
4. Obtain separate operator approval for the exact manifest digest.
5. Run with `--execute --confirm-sha256 <digest>`. Every row is reloaded and rechecked immediately before its operation. Authorization release delegates to the existing provider ownership and `requires_capture` reconciler. Settlement capture suppresses invoice/customer-notification side effects so invoice recovery stays separately allowlisted. Invoice recovery uses local uniqueness, a durable submission lease, and provider lookup before create.
6. Retain the sanitized report with the operator record. Never store the real manifest, provider response, credentials, customer data, or private handoff evidence in this public repository.

Any changed manifest, missing evidence, provider ambiguity, duplicate provider match, or unavailable dependency stops the affected item. Do not bypass the digest, edit database fields manually, substitute estimated energy or fees, or turn the command into a scheduled task.

The invoice provider pre-check calls e-racuni `SalesInvoiceList` with only the documented `dateFrom` and `dateTo` filters. `SalesInvoiceList` has no `apiTransactionId` filter. The window is the invoice `date` sent on create (the capture date in `Invoices:ERacuni:TimeZoneId`), widened by `Invoices:ERacuni:LookupWindowDaysBefore` and `Invoices:ERacuni:LookupWindowDaysAfter`. Both default to `1`, and the whole window may not exceed 31 days. Rows are matched locally on `orderReference` equal to `<OrderReferencePrefix>-<TransactionId>` (`EVSE-<TransactionId>` by default), plus `documentAmount` equal to the persisted billing total rounded to cents and `documentCurrency`. List rows carry those names; the documented `totalAmount`/`totalCurrency` are used only as a fallback. `reference` is not used, because the provider replaces it with its own payment reference. The outcomes are:

- A page below 500 rows in which every row has an in-window `date` and an `orderReference` property, and no row matches `orderReference`, is a definitive not-found.
- Exactly one match with equal amount and currency and a durable `number` or document identifier is adopted as the issued invoice.
- Everything else is `ProviderUnknown`: a page of 500 or more rows (possible truncation), a non-`ok` provider status, rows without `date` or `orderReference`, rows outside the requested window (the provider ignored the filter), duplicate `orderReference` matches, or a match whose amount or currency differs.

The public provider documentation does not list `orderReference`, `documentAmount`, or `documentCurrency` among `SalesInvoiceList` output fields. A read-only provider check confirmed that list rows return them, and that `date` equals the requested invoice date. If the provider stops returning `orderReference`, every non-empty window fails closed. See [ADR 0004](decisions/0004-eracuni-invoice-lookup-by-date-window.md).

Invoice lookup failures retain sanitized diagnostic structure in the existing submission audit: whether a provider request was attempted, the failure category (for example `TruncatedResponse`, `UnrecognizedRow`, `RowOutsideDateWindow`, `AmountMismatch`, `DuplicateMatch`, or `ProviderErrorStatus`), the nullable HTTP status, the recognized response shape, and `SalesInvoiceList` as the provider operation. Raw provider bodies, credentials, and customer identifiers are not copied into lookup-failure diagnostics. A specific `ProviderUnknown` lookup error is preserved through outer exception handling, and only a definitive not-found lookup may proceed to invoice creation.

## Logging and Monitoring

Observed:

- File logging through `Karambolo.Extensions.Logging.File`.
- Log files are configured under `Logs` by each app.
- Raw OCPP filesystem dumps are disabled when `MessageDumpDir` is empty, which is the default. To diagnose message exchange temporarily, point `MessageDumpDir` at a dedicated directory. Files older than `MessageDumpRetentionHours` (default 24) are removed every `MessageDumpCleanupIntervalMinutes` (default 15); non-positive retention or interval values disable cleanup.
- Database `MessageLog` retention reports cutoff, counts, timestamp bounds,
  batches, duration, and sanitized error types without logging message payloads.
  Follow the dry-run-first activation and rollback sequence in
  [MessageLog online retention](message-log-retention.md).
- Payment coordinator log lines use the prefix `Payments/<Operation> =>` (for example `Payments/Confirm =>`, `Payments/Complete =>`, `Payments/Webhook =>`) with a provider field. Before provider selection the prefix was `Stripe/<Operation> =>`. Saferpay gateway calls log as `Saferpay/<Operation> =>`. `Stripe/BuyerMetadataReconciliation =>` is unchanged. Update log-based alerts and saved searches that match the old prefixes.
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
- `Payments:MinimumSessionFeeKwh` defaults to `1.0`. Completed sessions with missing, inconsistent, or lower delivered-energy readings are no-charge: billable line amounts are zeroed, the uncaptured authorization (Stripe PaymentIntent or Saferpay transaction) is cancelled, and invoice creation plus paid-completion emails are skipped.
- `Payments:MinimumChargeAmountCents` defaults to `50`. Positive final amounts at or above the delivered-energy threshold but below the configured minimum cancel the uncaptured authorization and skip invoice creation and paid-completion emails. Exactly the configured minimum remains capturable.
- Authorization release retries default to four mutation attempts with a one-minute exponential base delay, followed by one final read-only provider verification after an indeterminate last attempt. Set `Maintenance:AuthorizationReleaseMaxAttempts` and `Maintenance:AuthorizationReleaseRetryBaseMinutes` to change those bounds. The budget counts the attempts of the current arming: a release that is armed again after it ended (for example by a new late authorization or explicit recovery) starts a new budget, while attempt numbers and the `authorization_release:{reservationId}:{n}` idempotency keys keep increasing. A five-minute in-progress lease, taken with a conditional update so that only one concurrent reconciliation calls the provider, prevents overlapping sweeps and is configurable with `Maintenance:AuthorizationReleaseInProgressTimeoutMinutes`. Provider state and reservation ownership are rechecked on every attempt; active, captured, invoiced, succeeded, received-funds, or ambiguous cases are not cancelled automatically. A check that only finds a Saferpay payment page still open does not count as an attempt; the next check is scheduled for the end of the page lifetime.
- If checkout completion linkage was missed or reordered, reconciliation retrieves the owned checkout (Stripe Checkout Session or Saferpay payment page) directly before reading its authorization. An authorization that Saferpay returns with the payment page is used without a second lookup.
  - A checkout that expired, was cancelled or failed without any authorization is first looked up by reservation id (`IPaymentGateway.FindAuthorizationByReservation`: Saferpay `Transaction/Inquire` by order id; Stripe has no such lookup). An owned authorization found this way goes through the normal checks and a held one is cancelled. One that is not owned by the reservation requires review. If nothing is found, the release finishes as already released.
  - A Stripe Checkout Session that is still open and unpaid finishes as already released when the reservation never reached `Authorized`; if the payer completes it later, the webhook releases the PaymentIntent. For an authorized reservation it is retried until it links a PaymentIntent. `checkout.session.expired` finishes an armed or `PermanentFailure` release of an ended reservation without a PaymentIntent as already released. A Saferpay page that is still open is checked again after `Saferpay:CheckoutLifetimeMinutes` plus 5 minutes, counted from the reservation's creation, and is then treated as closed.
  - Missing or mismatched session/intent ownership, and inability to verify invoice state, stop automatic release and require review.
- `payment_intent.amount_capturable_updated` must remain enabled on the Stripe webhook endpoint. It closes the race where a terminal reservation becomes capturable after checkout/cleanup ordering, while webhook-event deduplication prevents repeat cancellation.
- Public payment behavior depends on server, management, database, the payment provider (Stripe, Saferpay, or their mocks), and time-based cleanup settings.
- Changing `Payments:Provider` affects only new checkouts. Keep the previous provider configured until its reservations are finished; an owner provider that is missing or unconfigured fails closed (see [Maintenance](maintenance.md#switching-payment-providers)).
- Saferpay notifications reach `/API/Payments/Webhook/Saferpay` only when `Saferpay:NotifyBaseUrl` is public and routed to the server. Without them, a paid checkout whose browser return never arrives is picked up only by the stale-pending sweep (`Maintenance:PendingPaymentTimeoutMinutes`), which abandons the reservation instead of starting the session. Release reconciliation then reads the payment page; while it is still open, it waits until the page lifetime plus 5 minutes has passed, then looks the transaction up by order id and releases a hold it finds. Before the sweep runs, a payer who returns to the start page can resume a paid page, which confirms the reservation. See [Saferpay settings](#saferpay-settings).
- A transient provider error during settlement (for example a Stripe HTTP 5xx, or Saferpay `inquire_not_found`, `capture_state_unknown` or an unconfirmed cancel) does not fail the reservation while it is within `Maintenance:SettlementRetryWindowMinutes` (default `60`, `0` disables the retry, at most `1440`) of the disconnect. The reservation stays `Charging` or `WaitingForDisconnect`, so the connector stays locked, with `FailureCode=settlement_retry_pending` and a sanitized `LastError`. The cleanup sweep retries the settlement at most once a minute per reservation, whatever the connector reports, with the same amount. The retry reads the authorization first; a capture or cancel that is still needed is sent with an idempotency key scoped to the retry (`:r{n}` appended, `n` = whole minutes since the disconnect), so a stored provider answer to an earlier key, such as a Stripe `5xx`, is not replayed on every retry. Past the window the reservation is `Failed` with `FailureCode=settlement_retry_exhausted`.
- Reservations with `FailureCode=settlement_retry_exhausted` need an operator: settle a billable session with `recover-settlement`, otherwise release the hold at the provider. Reservations with `FailureCode=capture_amount_unverified` are `Completed` after a Saferpay capture replay whose amount Saferpay could not show yet (`CAPTURED_UNVERIFIED`); the requested amount is recorded, and no invoice or receipt is issued. Check the captured amount in the Saferpay back office, then issue the invoice with `recover-invoice`.
- Reservations with `FailureCode=captured_at_checkout` were charged in full at checkout and need a manual refund in the Saferpay back office. Reservations with `FailureCode=capture_pending` had a capture that the provider reported as pending; they are `Failed` with authorization release `ReviewRequired` and no invoice, and need the payment checked with the provider.
- Enabling VIES introduces a bounded outbound dependency on the European Commission service. Monitor `Unavailable` rates, but do not retry synchronously or turn an outage into a checkout failure.
- OCPP schema validation is optional and logs/continues on validation errors.
- Do not expose Hangfire dashboard or appsettings-derived secrets without deployment-specific access controls.
