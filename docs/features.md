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
- Can dump raw OCPP messages when `MessageDumpDir` explicitly names a dedicated directory; dumps are disabled by default and expire according to `MessageDumpRetentionHours`.

Important edge cases:

- OCPP 1.6 token/idTag length is constrained during remote start.
- Unknown or disconnected charge points cannot receive remote commands.
- Reservation profile operations are intentionally disabled in middleware comments/code.
- Message validation logs schema errors and continues rather than hard failing all messages.

## Server API

Code location: `OCPP.Core.Server/OCPPMiddleware.cs`

Known API areas:

- `/API/Status`
- `GET /API/Reset/{chargePointId}/{mode?}`
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
- Payment provider webhooks are the exception to server API key checking: `POST /API/Payments/Webhook` (legacy Stripe route), `POST /API/Payments/Webhook/Stripe` and `GET`/`POST /API/Payments/Webhook/Saferpay`. The provider gateway authenticates them. Unknown provider segments return `404` and unsupported methods return `405`.
- `POST /API/Payments/Confirm` requires `ReservationId`. `CheckoutSessionId` may be null for providers that append nothing to the return URL (Saferpay); Stripe still requires it and it must match.
- `ChangeConfiguration` takes `{value}` as one URL-encoded path segment and decodes it exactly once from the raw request target, so values containing `/` (for example network-profile blobs with `wss://` URLs) reach the charge point intact. Encode the whole value with `encodeURIComponent`/`Uri.EscapeDataString`; an unencoded `/` splits the route.
- Remote commands are dispatched to protocol-specific methods based on the connected charge point protocol.
- Reset accepts `Hard` and `Soft` modes (case-insensitive). An unsupported mode returns HTTP 400.
- Omitting `mode` preserves the compatibility reset: OCPP 1.6 receives `Soft`; OCPP 2.x receives `OnIdle`. `Hard` selects a full reset and `Soft` selects the compatibility-safe reset for each protocol.

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
- Public availability exposes only genuine `Available` connector state. A `Preparing` connector (vehicle plugged in, no session yet) is shown on the start page as its own `Preparing` status without the in-use warning, and the map counts it as occupied, never available.
- On the generic charger URL (`cp/{cp}`), the start page selects the connector that is the only one to have entered `Preparing` in the last 10 minutes; otherwise it selects the first `Available` connector, then the lowest connector. An explicit `cp/{cp}/{conn}` always wins.
- Connector choices are shown as compact cards side by side (left/right). Each card shows the configured connector name (for example `1A`, `Lijevo`) as a large main label, a small short code without the operator prefix (for example `E5202601*1`) underneath, and the status. The full `HR*...` connector code appears only once, in the header pill for the selected connector. A connector without a configured name falls back to its short code.
- Public payment redirect/status flow. `/Payments/Success` is the return target for every provider and accepts a missing `session_id` (Saferpay). Failed confirmations from public starts show the public result page. A confirmation that is still in progress (the throttled `Pending` answer) sends the payer to the status page and keeps the recovery cookie while the reservation is pending.
- `/Payments/Cancel` reports the server's answer. It says "Payment cancelled." only when the cancel was applied. A session that is already authorized, starting or charging sends public payers to the status page; any other status is shown with its message (for example the refund notice of a payment captured at checkout), or with "This session can no longer be cancelled.".
- Payment texts on the public pages are provider-neutral (for example "Secure online payment. Only the actual cost is charged." in all six portal languages).
- `/Payments/MockCheckout` serves local mock checkouts for both providers. Its optional `provider` parameter names the provider in the title, and the checkout reference is shown only when `session_id` is present. The page renders `successUrl` and `cancelUrl` as links, so it accepts only relative URLs and absolute `http`/`https` URLs. Any other scheme (for example `javascript:` or `data:`), a protocol-relative `//host` URL, backslashes or whitespace render the error result page with HTTP `400`.
- The public start page recovery treats `ResumeUnavailable`, `MissingCheckoutSession`, `ProviderUnavailable` and any provider error status ending in `Error` (for example `StripeError`, `SaferpayError`) as not resumable, and offers only the cancel action. A throttled resume ("Payment is still being processed.") and a `Status` answer send the payer to the status page instead, also while the reservation is still `PendingPayment`.
- Public stop request path.
- R1/company invoice data submission with OIB validation.
- Public map, start, payment result, and status pages support the language selector for visible step, connector, pricing, session-status, known validation/error, recovery-copy, and default portal-branding text.
- Public station cards keep connector/session start as the primary action and offer a localized external directions link only when the station has valid stored coordinates.
- The idle-fee grace label follows the selected language; English renders `grace` while the existing non-English labels remain localized.
- Configurable branding, SEO, QR scanner, light theme, support, and footer settings.
- The checked PWA manifest and favicon assets use the public `EV.Charge` app name and icon.
- Customer notification emails use bilingual Croatian/English templates and are not currently tied to the public portal language selector.
- The static help page `OCPP.Core.Management/wwwroot/faq.html` (served at `/faq.html`) is a self-contained page supplied by the operator: CSS, payment-method logos and the `window.T` HR/EN/SL/IT/DE/FR dictionaries are inline, and it loads Google Fonts, the `html5-qrcode` scanner from unpkg and brand images from the operator's info site. It covers the QR and Click2Charge start flows with an in-page QR scanner (camera access needs HTTPS), payment, pricing notes and an FAQ accordion. Language comes from `?lang=xx`, then the last choice stored in `localStorage`, then the browser language, falling back to Croatian; a key with no dictionary entry keeps the Croatian text written in the HTML. Its JSON-LD `FAQPage` mirrors the visible Croatian questions and answers, and its canonical and hreflang links point at the operator's info-site copy of the same page. The copy is the operator's: replace the file as a whole when they send a new version rather than editing claims here. `public-start-localization.spec.js` checks the dictionaries, the JSON-LD and `?lang=` selection.

Important edge cases:

- Public start depends on database charge point settings, connector status, server API availability, and payment configuration.
- Recovery cookies are scoped to reconnect users to in-progress reservations.
- Idle-fee window display comes from config/database settings.

## Payments and Reservations

Code locations:

- `OCPP.Core.Server/Payments/`
- `OCPP.Core.Server/Payments/Providers/` (gateway contract, registry, Stripe and Saferpay gateways)
- `OCPP.Core.Server/OCPPMiddleware.cs`
- `OCPP.Core.Database/ChargePaymentReservation*.cs`
- `OCPP.Core.Database/StripeWebhookEvent.cs`
- `OCPP.Core.Server.Tests/*Payment*Tests.cs`

Known behavior:

- Stripe and Worldline Saferpay run side by side. The payment provider for a new checkout is the charge point's `PaymentProvider` (set on the charge point edit page: Default, Stripe or Worldline Saferpay), else `Payments:DefaultProvider` (Stripe by default). Each provider is enabled separately (`Stripe:Enabled`, `Saferpay:Enabled`). Each reservation keeps the provider that created it, so switching a charge point never moves a reservation that is in flight.
- The server refuses to start when a charge point overrides to a provider that is unknown or not configured.
- New checkouts (`Payments/Create`) need the charge point's provider to be configured; otherwise they answer `{"status":"Disabled","reason":"ProviderUnavailable"}`. `IPaymentCoordinator.IsEnabled` reports only whether the default provider is configured. The payment API actions on existing reservations (`Confirm`, `Resume`, `Cancel`, `Stop`, `RequestR1`, `Status`), the idle-fee warning sweep and explicit financial recovery need only one configured provider (`IsOperational`). The coordinator then fails closed for a reservation whose own provider is not configured.
- Mock Stripe services and an in-process Saferpay JSON API mock are available for local/test flows.
- Payment reservations lock connectors while pending, authorized, starting, charging, or waiting for disconnect. Terminal reservations and reservations in `ReviewRequired` (settlement held for review after the session stopped) do not lock the connector.
- Hosted cleanup abandons stale pending reservations, marks start timeouts, retries settlements that failed transiently (see below), and retries a stuck `Charging` reservation only when its exact linked transaction is already stopped and the same connector has remained `Available` since at least the stop time for the configured grace period.
- Public payment status exposes reservation and transaction state.
- Idle fee calculation and idle warning emails are supported.
- Sessions with missing, inconsistent, or below-threshold delivered energy are treated as no-charge sessions under `Payments:MinimumSessionFeeKwh` (default `1.0` kWh). The uncaptured payment intent is cancelled, billable line amounts are zeroed, and invoice integration plus paid-completion emails are skipped.
- OCPP 1.6, 2.0.1, and 2.1 share one accepted meter-evidence boundary. Valid cumulative readings advance a durable accepted projection. Malformed, non-finite, negative, unsupported-unit, out-of-order, reset/non-monotonic, and physically impossible readings are retained as separate anomaly evidence and cannot replace that projection.
- Physical jump checks bound each increase by a capacity basis multiplied by the elapsed time, plus numeric precision. The basis is the highest accepted charger-reported offered power (`Power.Offered`; OCPP 2.1 also `Power.Import.Offered`) when available, otherwise the fallback ceiling `MeterEvidence:FallbackMaximumPowerKw` (default `400` kW, above any real charging power). OCPP unit multipliers are applied first. The checks never use `MaxEnergyKwh` as a physical ceiling. A bad terminal reading can settle only from the last accepted projection, without estimating the unobserved tail.
- Chargers that do not report offered power are therefore still validated: a reading that would need more than the fallback ceiling over the elapsed time is retained as `PhysicallyImpossibleIncrease` evidence and does not advance the accepted projection, while plausible readings are accepted and priced normally. Readings that continue from a rejected jump stay rejected until the register returns to the accepted trajectory, so a persistent offset is never billed; a terminal reading in that state requires review. A charger clock that stepped backwards (for example at a daylight-saving change) does not trigger this lock. The bound uses at least 60 seconds of elapsed time so same-second samples and small charger clock corrections are not mistaken for jumps. Reported offered power is capped at the ceiling. A decrease within reading precision keeps the previous accepted value, so settlement never sees a stop below the start.
- Connector-Available recovery uses connector meters only while no later session has started on that connector; otherwise it settles from the transaction's own accepted projection.
- Setting `MeterEvidence:FallbackMaximumPowerKw` to `0` restores strict mode: a positive non-terminal increase without accepted or same-observation offered power is preserved as suspect evidence and cannot advance the projection, and a positive terminal increase without an accepted offered-power basis becomes `ReviewRequired`, even below `MaxEnergyKwh`.
- A non-terminal increase that is plausible only under newly higher offered power is accepted and raises the trusted capacity; the same case on a terminal reading requires review. Legitimate zero-energy samples remain accepted. Candidate offered-power raw value, unit, and multiplier are retained with any anomaly. A terminal reading without a safe projection still requires review. A physically plausible terminal reading above `MaxEnergyKwh` (typically the short overshoot after the max-energy auto-stop) settles automatically at the authorized maximum as `FallbackAccepted` with reason `AuthorizationLimitExceeded`; the actual reading is kept as anomaly evidence. Review-required sessions do not capture, invoice, or send paid-completion notifications automatically, and financial recovery cannot capture from blank accepted-projection state.
- Positive final capture amounts at or above the delivered-energy threshold but below `Payments:MinimumChargeAmountCents` (default `50`) are cancelled before provider capture. Exactly the configured minimum remains capturable; invoice integration and completion emails only run after a successful paid completion.
- Settlement trusts only confirmed provider results. `CapturedAmountCents` is the amount the provider reports as received. A capture that the provider reports as pending is not completed: the reservation becomes `Failed` with `FailureCode=capture_pending` and authorization release `ReviewRequired`, without an invoice. A settlement cancel that finds the funds already captured records the captured amount and completes without an invoice or receipt; a cancel that is not confirmed is retried like a transient error and, once the retry window is over, fails the reservation and arms authorization release.
- A transient provider error during settlement (for example a Stripe HTTP 5xx, or Saferpay `inquire_not_found` or `capture_state_unknown`) does not fail the reservation within `Maintenance:SettlementRetryWindowMinutes` (default 60) of the disconnect. The reservation keeps its status and connector lock with `FailureCode=settlement_retry_pending`, and the cleanup sweep settles it again with the same amount. The retry reads the authorization first; a capture or cancel that is still needed gets an idempotency key scoped to the retry, so a provider answer stored for an earlier key is not replayed. A no-charge retry that finds the authorization already cancelled ends `Cancelled` with the release `Released`; a billable retry that finds it cancelled ends `Failed`. After the window it is `Failed` with `FailureCode=settlement_retry_exhausted`. A Saferpay capture replay whose amount Saferpay could not show yet (`CAPTURED_UNVERIFIED`) completes the reservation with the requested amount and `FailureCode=capture_amount_unverified`, without an invoice or receipt.
- Status changes that can race (out of `Pending`, cancels, the stale-pending and start-timeout sweeps, settlement failures, the release lease) are conditional database updates on SQL Server and SQLite, so the first one wins and the others act on its result. A payment confirmed after the reservation was cancelled or abandoned is released as a late authorization and never started.
- New terminal reservations that still need an uncaptured authorization released are explicitly armed for reconciliation. The coordinator re-reads the owning provider's checkout and authorization state (Stripe Checkout Session and PaymentIntent, or Saferpay payment page and transaction). It verifies reservation ownership and excludes active, captured, invoiced and received-funds cases. It then cancels only a matching authorization that is still capturable with a positive amount (Stripe `requires_capture`, Saferpay `AUTHORIZED`). A checkout that expired, was cancelled or failed without any authorization is first looked up by reservation id (Saferpay `Transaction/Inquire` by order id); only when nothing is found does it finish as already released. Reservations that never reached `Authorized`, such as stale pending ones the sweep abandoned, go straight to these provider checks. For such a reservation, a Stripe Checkout Session that is still open and unpaid finishes as already released after one lookup; a later completion is caught by the webhook. `checkout.session.expired` also finishes an armed or `PermanentFailure` release of an ended reservation without a PaymentIntent as already released.
- Late authorizations are released automatically. A payer can still complete a checkout after the reservation became `Cancelled`, `Abandoned`, `Failed`, `StartRejected` or `StartTimeout`, for example after a portal cancel or the stale-pending sweep. A provider signal (webhook or notification), a browser return to the ended Saferpay reservation, or a later provider lookup can reveal such a held authorization. If the reservation owns it and has no transaction, capture or invoice evidence, the coordinator cancels it (unless a release attempt is already running) and sets `LastError=late_authorization_released`. A reservation that was authorized first gets the reconciliation's transaction-evidence check: when a transaction may belong to it (for example a session that started late and was never linked), or the linkage cannot be evaluated, it keeps its hold with release `ReviewRequired`. A hold already under review for transaction evidence is never released automatically. The reservation stays in its terminal state. A transient provider failure while doing so arms the release and answers `503` to the PSP, so the PSP retries.
- A Saferpay payment page can still be completed for up to 1 hour after it was created (`IPaymentGateway.CheckoutLifetime`, `Saferpay:CheckoutLifetimeMinutes`, default 60), and Saferpay does not reliably signal a late completion without notifications. Until that lifetime plus 5 minutes has passed, counted from the reservation's creation, release reconciliation of a terminal Saferpay reservation schedules its next check for that time instead of treating an open page as closed; the wait does not use up release attempts. After that it looks the transaction up by order id (the reservation id, `Transaction/Inquire`) before it concludes that nothing is held.
- Cancelling a pending Saferpay reservation reads the payment page first. A held authorization is cancelled at once, a payment captured at checkout is recorded instead of cancelled, and a page that is still open or cannot be read leaves the authorization release armed, so the release reconciliation checks it again after the page lifetime.
- Release reconciliation is idempotent across missing or reordered checkout and capturable-amount webhooks, cleanup sweeps, transient provider failures, and server restarts. A bounded in-progress lease, taken with a conditional update, prevents overlapping reconciliations, and retry exhaustion ends with a read-only provider verification. Re-arming a release that ended starts a new retry budget. Attempts and sanitized outcomes are stored for support review. Existing terminal rows are not backfilled or selected unless they were explicitly armed by the new flow.
- Free-tag access can bypass paid flow for configured tag/charge point combinations.

Important edge cases:

- Reservation, transaction, connector, and payment provider state must stay synchronized.
- A reservation whose provider is no longer configured fails closed: capture marks it `Failed`, authorization release ends as `PermanentFailure`, and payer confirm/resume returns `ProviderUnavailable`.
- Cleanup services run on intervals and can change visible state after timeouts.
- A `ReviewRequired` or `PermanentFailure` authorization-release state is terminal for automated retries and requires operator investigation; the application never captures or invoices as part of release reconciliation.
- Server API and UI status pages must be validated together after payment changes.

### Payment providers

`PaymentCoordinator` owns the reservation lifecycle, pricing, settlement, release reconciliation, e-mails and invoice hand-off for every provider. An `IPaymentGateway` per provider talks to the PSP. Configuration is described in [Operations](operations.md#payment-providers) and the design in [ADR 0005](decisions/0005-pluggable-payment-providers.md).

| Behavior | Stripe (default) | Worldline Saferpay |
| --- | --- | --- |
| Hosted checkout | Checkout Session with a manual-capture PaymentIntent | Payment Page `Initialize`, with the reservation id as `OrderId` and `PreAuth` when `Saferpay:PreAuth` is true. By default only `VISA`, `MASTERCARD` and `MAESTRO` are offered (`Saferpay:PaymentMethods`). |
| Payment captured at checkout | Does not occur: the PaymentIntent uses manual capture | Refused. The reservation becomes `Failed` with `FailureCode=captured_at_checkout` and the captured amount in `CapturedAmountCents`. Charging never starts, no e-mail is sent, and the payer must be refunded manually in the Saferpay back office. |
| Stored checkout / authorization reference | Checkout Session id / PaymentIntent id | Payment page token / transaction id |
| Payer return | Success URL with `session_id`, plus a separate cancel URL | One return URL for success, decline and abort, without `session_id`. The outcome comes from `PaymentPage/Assert`. Because the reservation id is enough to confirm, only a `Pending` reservation is checked, at most once every 2 seconds (a throttled call answers `Pending`), and the authorization e-mail is sent once. |
| Webhooks | Signed events (`checkout.session.completed`, `checkout.session.expired`, `payment_intent.payment_failed`, `payment_intent.amount_capturable_updated`) | Payload-free success/fail notification `GET` to a server-signed URL. It is only a signal; the server re-asserts the payment before changing state. |
| Payer details | Read from the checkout at any time; persisted `PayerEmail`/`PayerName` are a fallback | Persisted at authorization and then the only source. With `Saferpay:CollectPayerEmail` the payment page asks for the e-mail. |
| Buyer metadata | Bounded R1 metadata copy mirrored to Checkout and PaymentIntent | None. The database snapshot is the only source of truth for buyer data. |
| Resume an open checkout | Redirects back to the open Checkout Session | Not possible (single-use redirect). The payer cancels and starts again. A page that was already paid is confirmed, charging starts, and the payer continues on the status page. Resume shares the Confirm throttle; a throttled Resume answers `Status`, so the payer waits on the status page. |
| Final settlement | Partial capture of the final amount; the remainder is released | One capture of the final amount (which may be lower than the hold); Saferpay releases the remainder automatically |
| Zero or below-minimum final amount | Authorization cancelled | Authorization cancelled (Saferpay does not allow a zero capture) |

Shared behavior:

- **Verified start only.** A webhook or notification starts charging only for reservations the coordinator verified with the owning provider and saved as `Authorized`. The browser return (`Payments/Confirm`), `Payments/Resume` and the plug-in (`Preparing`) path remain alternative triggers.
- **One remote start per reservation.** Each trigger re-reads the reservation and starts only an `Authorized` one with no start under way: not after a start was requested, not while the plug-in trigger owns it, not within `Payments:ChargerResponseTimeoutMs` of the last remote start result, and not while another start for it is still waiting for the charger. See [Operations](operations.md#starting-charging-after-payment).
- **Idempotent processing.** Repeated returns, notifications and webhook retries are idempotent. Stripe events are deduplicated by event id. Saferpay notifications are idempotent through the reservation state: a notification can authorize, cancel or fail only a reservation that is still pending. On a terminal reservation without a transaction it can only release a late authorization (see above). The browser return and a notification can confirm the same reservation at the same time; on SQL Server and SQLite the change to `Authorized` and the charge tag claim are one conditional update, so only one of them authorizes it and both use the same tag.
- **Payer e-mail.** Authorization, completion and idle-fee e-mails go to the e-mail the provider reports (Stripe can be asked again later), then the persisted `PayerEmail`, then the R1 invoice e-mail (`InvoiceBuyerEmail`).
- **Acknowledge, then start.** The server answers a webhook or notification before it starts charging, so a slow charger never delays the PSP's acknowledgement.
- **Fail closed.** A reservation is never processed through a provider other than the one recorded on it. A transient provider failure in a webhook answers `503`, and a database failure while a Saferpay notification is processed answers `500`, so the PSP retries.
- **Sanitized errors.** Provider error texts are sanitized before they are stored or shown. Saferpay declines are stored with the Saferpay error name as the failure code. Payers see a provider error status such as `StripeError` or `SaferpayError`.

### Night energy tariff

Each station can enable a night energy price in the operator portal (station detail: night tariff switch, night price per kWh, night window start and end in local time, default 22:00–07:00).

- **Snapshot at checkout.** The reservation freezes the night price, window and time zone. The time zone comes from `Payments:NightTariffTimeZoneId`, falling back to `Payments:IdleFeeExcludedTimeZoneId`, then `Europe/Zagreb`. Later station edits never reprice a paid session. Stations without the switch keep single-price behaviour.
- **Window semantics.** The window is half-open in local civil time: the start minute is night, the end minute is day. Daylight-saving nights therefore last 8 or 10 elapsed hours. An unknown time zone disables the discount instead of guessing.
- **Energy allocation.** When the transaction starts, the window is copied onto it. Every *accepted* meter reading then adds the night share of its increase to `Transaction.NightEnergyKwh`, assuming even power between two readings (chargers typically report about once a minute). Rejected or review-required readings never move the split. A session that charges across 22:00 or 07:00 switches price at the boundary without restarting.
- **Settlement.** Day and night energy are priced and rounded separately, and night energy is clamped to the delivered total. The below-minimum-energy no-charge rule, session fee and idle fees are unchanged. The payment hold covers the maximum energy at the higher of the two prices.
- **Invoices.** The e-računi draft gets separate day (`Energy`) and night (`EnergyNight`) lines that sum exactly to the energy cost. `EnergyNight` uses `Invoices:ERacuni:LineItems:EnergyNight` when configured, otherwise the `Energy` product.
- **Customer view.** The public start page shows the night price and window. The status page shows the live split and includes `nightPricePerKwh`, `nightTariffStart`, `nightTariffEnd`, `transactionNightEnergyKwh` and `transactionNightEnergyCost` in the status JSON.

Design record: `docs/superpowers/specs/2026-09-30-night-energy-tariff-design.md`.

## Invoice and Email Integrations

Company invoice requests support a confirmed reservation-bound buyer snapshot. The public start page collects and validates the complete buyer details before creating the provider checkout, so a session cannot finish before the invoice intent and buyer snapshot exist. Croatian companies retain strict OIB checksum validation. Foreign companies provide an ISO two-letter country, legal name and address, billing email, a required tax/VAT/company identifier, and an optional legal registration number. A foreign identifier marked as a VAT registration must carry the selected country's VIES prefix and match the European Commission's published structure; presentation spaces, dots, and hyphens are removed and the canonical uppercase value is used downstream. Greece maps `GR` to the `EL` VAT prefix, while Northern Ireland maps `GB` to `XI`. Foreign identifiers not marked as VAT registrations retain their existing free-form behavior.

Optional VIES verification runs after local validation and before the provider checkout when `Payments:Vies:Enabled` is true. Only the VIES country code and prefix-free VAT number leave the application. Remote `Valid`, `Invalid`, and `Unavailable` results are non-blocking and are retained with a checked time and bounded provider reference; disabled verification is stored as `NotChecked`. The public status page warns about `Invalid`, explains `Unavailable`, and stays silent for `Valid` or `NotChecked`.

The reservation-specific status link in the R1 request email reopens the existing confirmed buyer flow without creating another token. It prefills from the durable reservation snapshot and accepts complete, locally valid corrections until invoice submission starts. Each editable response carries the existing confirmation timestamp as a version; mismatched or concurrent writes fail with `BuyerDataChanged` and must reload rather than overwrite newer data. Active submission leases, `Submitting`, `ProviderUnknown`, submitted/external evidence, and invoice-state lookup failures lock editing until the state is safely editable or support reconciles it. Changed foreign VAT identities rerun the configured non-blocking VIES check; unchanged identities retain their existing verification evidence. After issuance, the same form is read-only and directs the customer to support for the provider-supported correction, storno, or reissue path. The invoice builder continues to read the updated durable snapshot.

The public start and status pages do not retain reusable buyer details in browser storage. Ordinary validation failures preserve the submitted values through the server-rendered form response. For Stripe reservations, Checkout and PaymentIntent receive a bounded, versioned metadata copy for payment reconciliation; serialized writes and a final version reread prevent an older accepted edit from becoming the final mirror. If Stripe mirroring is unavailable after a successful save, the response reports `UpdatedMetadataPending` only after a durable Hangfire reconciliation job is queued. If queueing fails, it reports `UpdatedMetadataRetryRequired` so the customer is explicitly asked to retry or contact support. The reservation snapshot remains authoritative while retries converge the metadata, and e-racuni issuance continues to use that snapshot as its source of truth. Saferpay has no metadata. For Saferpay reservations a save answers `Updated` without any mirror or reconciliation job, and the reservation snapshot is the only copy.

The e-racuni payload maps supported buyer name, street, postal code, city, country, canonical VAT/tax identifier, email, and explicit VAT-registration status. For Stripe reservations, Checkout and PaymentIntent metadata receive the same canonical VAT identifier. The original submitted identifier remains in the internal snapshot for audit. Legal registration numbers also remain in that snapshot because the provider contract has no dedicated supported field; they are not mapped into `buyerCode`.

In submit mode, an existing unfinished, failed, or provider-unknown invoice lineage, and every financial-recovery invoice run, check the provider before creating. The pre-check lists e-racuni invoices in a bounded `dateFrom`/`dateTo` window around the capture date. It adopts the single row whose `orderReference` (`EVSE-<TransactionId>` by default) and total match the draft. It creates only when a complete page below the 500-row provider limit shows no row with that `orderReference`. Truncated, ambiguous, mismatched, or unrecognized results stay `ProviderUnknown` for manual reconciliation. See `docs/operations.md` and [ADR 0004](decisions/0004-eracuni-invoice-lookup-by-date-window.md).

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
