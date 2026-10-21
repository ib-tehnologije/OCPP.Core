# Maintenance

## How to Make Changes Safely

1. Identify the affected surface:
   - OCPP protocol handling
   - server API
   - management UI
   - public charging/payment UI
   - database schema
   - payments/invoices/emails
   - background jobs
   - extensions
   - simulators/tests
2. Read the closest existing code and tests before editing.
3. Keep public repository changes free of private/client/deployment context.
4. Update documentation in the same task when behavior, config, schema, commands, integrations, jobs, deployment, or architecture changes.
5. Add or update an ADR in `docs/decisions/` for durable architecture decisions.

## Validation Matrix

Default documentation-only validation:

```sh
git diff --check
```

Default code validation:

```sh
bash ./scripts/check-mssql-migration-metadata.sh
dotnet restore OCPP.Core.sln
dotnet build OCPP.Core.sln
dotnet test OCPP.Core.Server.Tests/OCPP.Core.Server.Tests.csproj --no-build
```

Focused database-log retention validation:

```sh
LANG=en_US.UTF-8 LC_ALL=en_US.UTF-8 DOTNET_ROLL_FORWARD=Major \
  dotnet test OCPP.Core.Server.Tests/OCPP.Core.Server.Tests.csproj \
  --filter 'FullyQualifiedName~MessageLogRetention'
```

For payment/public portal changes:

```sh
npm ci --prefix Simulators/playwright
npx --prefix Simulators/playwright playwright install chromium
node Simulators/run_e2e_stack.mjs
```

For payment provider changes, also run the focused provider tests and the E2E stack against the Saferpay mock:

```sh
dotnet test OCPP.Core.Server.Tests/OCPP.Core.Server.Tests.csproj \
  --filter 'FullyQualifiedName~Saferpay|FullyQualifiedName~PaymentProvider|FullyQualifiedName~PaymentCoordinatorProvider'
OCPP_E2E_PAYMENT_PROVIDER=Saferpay node Simulators/run_e2e_stack.mjs
```

For broad regression changes after building:

```sh
python3 Simulators/run_local_regression.py
```

For database schema changes:

```sh
make add-migration NAME=DescriptiveName
make check-migration-metadata
dotnet test OCPP.Core.Server.Tests/OCPP.Core.Server.Tests.csproj
```

Also inspect the generated migration and model snapshot manually for provider-specific type drift.

## Common Failure Points

- `OCPPMiddleware.cs` has several responsibilities in one class: route parsing, WebSocket state, server API, payment handlers, and helper logic. Small changes can have wide effects.
- Live charge point state is in memory, so multi-instance behavior is not implied by the code.
- Server API authentication depends on matching `ApiKey` values between server and management app.
- SQL Server and SQLite paths differ: migrations for SQL Server, `EnsureCreated()` for SQLite.
- Hangfire jobs only start with SQL Server configured.
- Payment reservation state touches connector locks, remote start/stop, the reservation's payment provider (Stripe or Saferpay), cleanup services, status UI, invoice logs, and emails.
- Provider-specific code belongs in an `IPaymentGateway` implementation under `OCPP.Core.Server/Payments/Providers/`. `PaymentCoordinator` must stay provider-neutral and must route every operation on an existing reservation through `ChargePaymentReservation.PaymentProvider`, never through the active provider.
- The one-reservation-per-connector database lock is defined twice. On SQL Server it is the computed column `ChargePaymentReservation.ActiveConnectorKey` (SQL in `OCPPCoreContext`) behind the unique index `UX_PaymentReservations_ActiveConnector`. On SQLite and the in-memory provider, `SaveChanges` writes the same column from `ChargePaymentReservationState.InactiveStatuses` through `ChargePaymentReservationActiveKey.Compute`. Every status that does not lock the connector (`ChargePaymentReservationState.LocksConnector`) must be in both lists; `ReviewRequired` is one of them. Changing the computed column needs a migration that drops and recreates the index around it. `ChargePaymentReservationActiveKeyTests` fails if the SQL list, `InactiveStatuses`, the lock statuses, or the model snapshot disagree.
- Timeouts and idle-fee behavior depend on UTC timestamps, configured local time zones, and sweep intervals.
- OCPP 1.6, 2.0.1, and 2.1 can need protocol-specific behavior even when a feature name is shared.
- `ChargePaymentReservation.InvoiceBuyerConfirmedAtUtc` is an optimistic concurrency token and the public portal can change it while a session is charging. Code that updates a reservation from a long-lived context must work on a freshly loaded row (see `RefreshTrackedReservation` in `PaymentCoordinator`); otherwise the write fails with `DbUpdateConcurrencyException`.
- Reservation status changes that can race (browser return, provider signals, cancels, sweeps, settlement failures, the release lease) go through `PaymentCoordinator.TryCommitReservationTransition`, a compare-and-set `UPDATE` on the expected status. A new transition of that kind must use it too, and must handle the `false` result (the row was reloaded with the winner's state). The helper writes the SQLite `ActiveConnectorKey` itself, because `ExecuteUpdate` bypasses `SaveChanges`. On the in-memory test provider it falls back to `SaveChanges`, so races are covered only by the SQLite race tests (`PaymentCoordinatorProviderTests.Races.cs`).
- Public portal changes may require Razor, CSS, JavaScript, server API, and Playwright updates.

## Recurring Maintenance Tasks

- Keep migrations and `OCPPCoreContextModelSnapshot.cs` aligned with entity/model changes.
- Run `scripts/check-mssql-migration-metadata.sh` after migration scaffolding.
- For authorization-release changes, test webhook ordering, restart/due-retry behavior, ownership mismatches, active/captured/invoiced exclusions, provider retry limits, and historical unarmed rows.
- Keep public docs current with config keys and validation commands.
- Review checked runtime artifacts under `SQLite/` and `Simulators/playwright/` before adding new generated files.
- Keep simulator scenarios in sync with payment and protocol behavior.
- Review payment reservations that need operator action, by `FailureCode`:
  - `captured_at_checkout`: charged in full at checkout; refund in the Saferpay back office.
  - `capture_pending`: check the capture with the provider.
  - `settlement_retry_exhausted`: transient provider errors outlasted `Maintenance:SettlementRetryWindowMinutes`. Settle a billable session with `recover-settlement`; otherwise release the hold at the provider.
  - `capture_amount_unverified`: a Saferpay capture replay whose amount could not be confirmed (`CAPTURED_UNVERIFIED`). The reservation is `Completed` with the requested amount recorded and no invoice or receipt. Check the captured amount in the Saferpay back office, then issue the invoice with `recover-invoice`.
  - Also review authorization releases in `ReviewRequired` or `PermanentFailure`.
- Review package versions periodically for .NET, Hangfire, EF Core, Stripe, Sentry, Azure Identity, ClosedXML, and Playwright.
- Review the Saferpay JSON API release notes periodically. `Saferpay:SpecVersion` defaults to `1.54`; raise it only after checking the changed request and response fields against `SaferpayPaymentGateway` and its tests.

## Switching Payment Providers

`Payments:Provider` selects the payment provider for new checkouts only. Each reservation stays with the provider recorded in `ChargePaymentReservation.PaymentProvider`. Configuration keys are described in [Operations](operations.md#payment-providers), and the design in [ADR 0005](decisions/0005-pluggable-payment-providers.md).

### Upgrade to provider tracking

1. Apply migration `AddPaymentProvider` through the normal deployment path. It adds `PaymentProvider` (default `Stripe`), `PayerEmail` and `PayerName` to `ChargePaymentReservation` and does not rewrite existing data. Use `AutoMigrateDB=true` or `make dbupdate`.
   - After any migration change, run `make check-migration-metadata`.
   - Local SQLite files are created with `EnsureCreated()`, which never adds columns to an existing file. Recreate them with `make sqlite-reset`, or delete the configured file.
2. Update log-based alerts. Payment coordinator log lines now start with `Payments/<Operation> =>` instead of `Stripe/<Operation> =>`, for example `Payments/Complete =>` and `Payments/Webhook =>`. Saferpay gateway lines use `Saferpay/<Operation> =>`.
3. Existing Stripe webhook endpoints at `/API/Payments/Webhook` keep working. New Stripe endpoints can use `/API/Payments/Webhook/Stripe`.
   - A Stripe endpoint registered with an extra suffix (for example `/API/Payments/Webhook/prod`) also keeps working, but it is deprecated. A `POST` with a `Stripe-Signature` header to a path below `/API/Payments/Webhook` that is not a known provider route is handled as the legacy Stripe route, and the server logs a deprecation warning once per process. Move such endpoints to `/API/Payments/Webhook/Stripe`. Without the `Stripe-Signature` header these paths return `404`.

Behaviour changes that also affect Stripe-only deployments:

- **Webhooks and the payment API.**
  - Webhooks start charging only for reservations that the server verified with Stripe. Before, the start could follow from the raw webhook body.
  - The webhook is answered before charging starts. The server sets the status code and completes the response, then runs the auto-start, so Stripe gets its `2xx` at once instead of after up to `Payments:ChargerResponseTimeoutMs`.
  - Signed Stripe deliveries to a suffixed webhook path, such as `/API/Payments/Webhook/prod`, still work through the legacy route and log a deprecation warning (see step 3).
  - Payment API actions on existing reservations (`Confirm`, `Resume`, `Cancel`, `Stop`, `RequestR1` and `Status`) answer `Disabled` only when no registered provider is configured (`IPaymentCoordinator.IsOperational`). Only `Create` needs the active provider (`IsEnabled`). The idle-fee warning sweep and explicit financial recovery use the same operational gate. With only Stripe configured both gates are equal, so this changes nothing until a second provider is configured.
  - The webhook auto-start, the browser return and `Payments/Resume` share one start rule: only an `Authorized` reservation with no start under way is started (see [Operations](operations.md#starting-charging-after-payment)). A skipped start answers with the reservation status and a `reason`. A repeated or parallel return, or a webhook racing the return, no longer sends a second remote start. Before, Confirm always tried to start, and a rejected duplicate could set `StartRejected` and cancel the PaymentIntent of a session that was charging.
  - `Payments/Resume` answering `Status` for an `Authorized` reservation with no start under way now sends a remote start after the answer. Before, Resume never started charging.
- **Late PaymentIntents of ended reservations.**
  - `checkout.session.completed` for a reservation that already ended without a charging session (`Cancelled`, `Abandoned`, `Failed`, `StartRejected` or `StartTimeout`) now reads the PaymentIntent. If the reservation owns it and it is `requires_capture`, it is cancelled (idempotency key `cancel:{reservationId}`), `LastError` becomes `late_authorization_released` and the release state `Released`. `payment_intent.amount_capturable_updated` does the same with the event's PaymentIntent. A transient Stripe failure answers `503`, so Stripe retries; a failed cancel also arms the release.
  - An armed release with retries left still goes through release reconciliation first. One whose retries are used up now takes the cancel above instead of a read-only verification that ended in `PermanentFailure`.
  - A reservation that was authorized (for example it has `AuthorizedAtUtc` or a remote start) first gets the reconciler's transaction-evidence check. A matching unlinked transaction, or linkage that cannot be evaluated, sets the release to `ReviewRequired` and links the PaymentIntent without cancelling it. A release already in `ReviewRequired` for transaction evidence is never released by a webhook.
  - `checkout.session.expired` for an ended reservation without a PaymentIntent, whose release is armed or `PermanentFailure`, now finishes the release as `Released` (attempt outcome `AlreadyReleased`). Before, the event was ignored.
- **Confirming the browser return.**
  - A transient Stripe error (timeout, throttling, HTTP 5xx, connection error) now leaves the reservation pending. Stripe.net connection failures (`HttpRequestException`) and request timeouts are mapped to transient provider errors with codes `network_error` and `timeout` for every Stripe call, so they take the same retry paths as Stripe 5xx answers instead of escaping as unhandled exceptions. The connector stays locked until a retry, a webhook or the stale-pending sweep (`Maintenance:PendingPaymentTimeoutMinutes`) finishes it. Before, any Stripe error failed the reservation at once.
  - A non-transient error fails a `Pending` reservation and now also arms authorization release, so any hold is cancelled. For a reservation that is no longer `Pending` (for example `Authorized` or `StartRequested`) it returns `StripeError` and changes nothing. Before, it failed the reservation and armed the release.
- **Management payment pages.**
  - When `/Payments/Success` fails for a payer who started on the public portal (`origin=public`), the public result page is shown instead of the default result page. An operator return whose answer is `Authorized` or `StartRequested` shows "Payment authorized. Charging session will start shortly." instead of the start-error text.
  - `/Payments/Cancel` (the Stripe cancel URL and the status page's cancel link) now reports the server's answer. "Payment cancelled." appears only when the cancel was applied. An active session sends public payers to the status page, and other statuses are shown with their message. Without a usable answer the page shows "Something went wrong." and keeps the recovery cookie. Before, the page always said "Payment cancelled.". The server `Payments/Cancel` answer has a new `message` field.
  - `/Payments/Status` keeps the recovery cookie while the reservation is still `PendingPayment`.
- **Authorization release.**
  - An armed release whose checkout expired, was cancelled or failed without any PaymentIntent now ends as already released. Before, it retried linkage and then ended as `PermanentFailure`.
  - A stale or startup-abandoned reservation that never reached `Authorized` is now checked with Stripe instead of going straight to `ReviewRequired`. Such a reservation has no `AuthorizedAtUtc`, `StartDeadlineAtUtc`, remote start (sent, accepted or result), `AwaitingPlug`, `StartTransactionAtUtc` or transaction link; the charge tag is not considered. A `requires_capture` PaymentIntent linked by the Checkout Session is cancelled. A session that is still open and unpaid finishes as already released after one session lookup, instead of four linkage retries and `PermanentFailure`; if the payer completes it later, the webhook releases the PaymentIntent. Authorized reservations keep the linkage retries.
  - Release reconciliation, the cleanup sweep, the webhook's armed-release check and explicit financial recovery accept `Cancelled`, `StartRejected` and `StartTimeout` reservations as well as `Abandoned` and `Failed`.
  - Cancelling the PaymentIntent of a reservation (portal cancel, start rejection, start timeout) now arms the release and saves it when the cancel cannot be confirmed: Stripe is not configured, the PaymentIntent lookup returns nothing or fails, or the cancel returns a status other than `canceled`. Before, only `LastError` was set, and it was lost after the start-rejection save.
  - A portal cancel and the start-timeout sweep arm the release already in the conditional update that writes `Cancelled` or `StartTimeout`, when the reservation has a PaymentIntent. A failure or restart before Stripe confirms the cancel therefore leaves a release for the cleanup sweep. For a portal cancel the first release attempt waits `Maintenance:AuthorizationReleaseInProgressTimeoutMinutes` (default 5), so the sweep does not race the request's own cancel. A confirmed cancel, or a PaymentIntent that is already `canceled`, marks the release `Released`; any other PaymentIntent status stays armed, and the release reconciliation decides (a `succeeded` PaymentIntent ends in `ReviewRequired`). Before, the release state stayed empty after a confirmed cancel or a non-cancelable PaymentIntent.
  - Arming a release that was not armed resets `AuthorizationReleaseAttemptCount` to `0`, so each arming (including explicit recovery) gets a new retry budget. The count now means attempts of the current arming. Attempt numbers and the `authorization_release:{reservationId}:{n}` idempotency keys keep increasing.
- **Settlement.**
  - `CapturedAmountCents` is the PaymentIntent's `amount_received`, which equals the requested amount for Stripe.
  - A capture that returns `processing` is no longer marked `Completed`. The reservation becomes `Failed` with `FailureCode=capture_pending`, authorization release `ReviewRequired`, and no invoice.
  - A transient Stripe error during settlement, within `Maintenance:SettlementRetryWindowMinutes` (default 60) of the disconnect, no longer fails the reservation. It stays `Charging` or `WaitingForDisconnect`, so the connector stays locked, with `FailureCode=settlement_retry_pending` and a sanitized `LastError`. The cleanup sweep retries the settlement about once a minute, whatever the connector reports, with the same amount; idle minutes are not counted twice. A retry reads the PaymentIntent first, so a capture or cancel that went through is recognized instead of repeated. A capture or no-charge cancel that is still needed is sent with a retry key, `capture:{reservationId}:{amount}:r{n}` or `cancel:{reservationId}:r{n}`, where `n` is the number of whole minutes since the disconnect; Stripe replays a stored answer, a `5xx` included, for a reused key. The first attempt keeps `capture:{reservationId}:{amount}` and `cancel:{reservationId}`. The outcome message of the retry (for example "No billable amount to capture.") is kept. Past the window, or with the window set to `0`, the reservation is `Failed` as before, now with `FailureCode=settlement_retry_exhausted`. Before, any such error failed the reservation at once.
  - The result of a settlement cancel (zero or below-minimum amount) is checked. A `succeeded` result records the captured amount and completes the reservation without an invoice or receipt. Any other result counts as a transient failure and is retried as above; past the window it fails the reservation and arms the release. A retry that finds the PaymentIntent already `canceled` (the earlier cancel went through but its answer was lost) ends `Cancelled` with `CapturedAmountCents=0` and release `Released`. Before, a failed no-charge cancel gave `Failed` plus an armed release that ended `ReviewRequired`, because the reservation has a transaction.
  - Settlement failures are written conditionally, so a failing run no longer overwrites a reservation that a concurrent run completed. A run that settles writes its outcome over a retry marker, failure or release arming that a concurrent run of the same settlement saved meanwhile.
  - While a settlement waits for a retry (`FailureCode=settlement_retry_pending`), a `Charging` or `WaitingForDisconnect` reservation is no longer relinked to a new transaction that starts with the same tag on the same connector. Outside a retry the earlier behaviour stays: a charger that stops and restarts with the same tag hands the reservation to the new transaction.
- **Payer data and e-mails.**
  - Stripe payer e-mail and name are now stored in `ChargePaymentReservation.PayerEmail` and `PayerName` when the checkout is confirmed or a webhook processes it. Before, they were only read from Stripe when needed. This is new personal data at rest: include it in privacy notices and in the retention or deletion procedures for reservation rows.
  - Authorization, completion and idle-fee e-mails fall back to the R1 invoice e-mail (`InvoiceBuyerEmail`) when the Stripe session e-mail and `PayerEmail` are empty. The recipient address is trimmed.
- **Concurrency (SQL Server and SQLite).** Without a race the results are unchanged.
  - Reservation status changes that can race are conditional updates: a change is written only if the row still has the status it was read with, and the loser reloads and acts on the winner's state. This covers the browser return and Confirm errors, the status changes made by `checkout.session.completed`, `checkout.session.expired` and `payment_intent.payment_failed`, portal cancels, the stale-pending sweep and startup abandon (which also require an unchanged `UpdatedAtUtc`), the start-timeout sweep (which also requires no linked transaction), settlement failures and the release lease.
  - `Pending` to `Authorized` also claims `OcppIdTag` in the same `UPDATE`. The `ChargeTag` row is inserted first, through a separate database context, and deleted again if the transition loses. When the webhook authorizes first, the browser return still prepares the tag and sends the authorization e-mail. When a cancel or sweep ended the reservation first, neither path authorizes it; the PaymentIntent found is released and the reservation is never started.
  - A portal cancel writes `Cancelled`, with the release armed when a PaymentIntent is set, before it reads or cancels the PaymentIntent. If the reservation changed meanwhile, it is cancelled in its new state; `Charging` and terminal reservations are left alone. The final `LastError` is the cancel reason, as before.
  - The start-timeout sweep writes `StartTimeout`, with the release armed, before it cancels the PaymentIntent, so a reservation that a `StartTransaction` linked meanwhile keeps its hold.
  - The stale-pending sweep and startup maintenance skip a reservation that changed after they read it; it is neither abandoned nor released.
  - Of two concurrent release reconciliations of one reservation, only the one that takes the lease calls Stripe. The other returns `SkippedNotEligible`.
  - Each authorization costs a few more SQL statements: a `ChargeTag` `INSERT` in its own context, the conditional `UPDATE` and a `SELECT` of the tag, plus a `DELETE` of the unused tag when the transition loses.
- **Tooling and logging.**
  - `/Payments/MockCheckout` (used by the Stripe mock) rejects `successUrl` and `cancelUrl` values that are not relative or absolute `http`/`https` URLs, with HTTP `400`. The mock Stripe URLs are unaffected.
  - The server logs startup warnings when the active provider is not configured while another one is, and when the return base URL is empty while a provider is configured. Nothing is logged when no provider is configured.
  - The E2E harnesses in Stripe mode pin `Payments:Provider=Stripe`, `Payments:ReturnBaseUrl` and `Saferpay:Enabled=false` on the server and `Payments:Provider=Stripe` on management. Scenario checkout URLs must carry `session_id` and must not carry `provider=Saferpay`.

### Switching the active provider

1. Configure the new provider section, for example `Saferpay:*`, with credentials supplied as secrets. Set `Payments:ReturnBaseUrl`, `Payments:Currency` and `Payments:ProductName` so they no longer depend on the `Stripe:*` fallbacks.
2. Make the new provider's webhook or notification route publicly reachable. For Saferpay, set `Saferpay:NotifyBaseUrl` to the public server `/API` base.
3. Set `Payments:Provider` on the server, and on the management app for the admin banner.
4. Keep the previous provider enabled and its credentials configured, together with its webhook endpoint. For Stripe this means `Stripe:Enabled`, `Stripe:ApiKey` and `Stripe:WebhookSecret`.
5. Restart the server and the management app. Provider settings are read at startup, and an unknown `Payments:Provider` value stops the server before it accepts requests.
   - If the selected provider is registered but not configured while another provider is, the server still starts. It logs a warning that names the selected provider, refuses new checkouts (`{"status":"Disabled"}` from `Payments/Create`), and keeps processing existing reservations through their own provider. Fix the configuration and restart.
6. Start one test session through the public portal and check that the new reservation row has the new `PaymentProvider`.
7. Wait until the previous provider has no unfinished reservations. Check with SQL (it works on SQL Server and SQLite; replace `Stripe` with the previous provider):

   ```sql
   SELECT PaymentProvider, Status, AuthorizationReleaseState, COUNT(*) AS Reservations
   FROM ChargePaymentReservation
   WHERE PaymentProvider = 'Stripe'
     AND (Status NOT IN ('Completed', 'Cancelled', 'Failed', 'ReviewRequired',
                         'StartRejected', 'StartTimeout', 'Abandoned')
          OR AuthorizationReleaseState IN ('Pending', 'InProgress', 'RetryScheduled'))
   GROUP BY PaymentProvider, Status, AuthorizationReleaseState;
   ```

   Also resolve the previous provider's `ReviewRequired` reservations, and its authorization releases in `ReviewRequired` or `PermanentFailure`. Explicit financial recovery (`OCPP.Core.Recovery`) for those rows also calls the reservation's own provider.
8. Only then disable the previous provider and remove its credentials and webhook endpoint.

If the previous provider is removed too early, its reservations fail closed. They are never sent to another provider:

- Payer confirm and resume return `ProviderUnavailable`. The public portal then offers only the cancel recovery.
- Capture at session end does not take any money. The reservation becomes `Failed` (`provider_not_configured`, or `provider_not_registered` for an unknown name), and any authorization release it needs is armed.
- That authorization release ends as `PermanentFailure` and needs review. The hold stays at the provider until it expires or someone releases it manually.
- Cancelling the provider authorization (for example after a start timeout) cannot reach the provider. It is recorded in `LastError` and arms the authorization release, which then ends as `PermanentFailure`. The reservation itself can still be cancelled locally.
- Webhooks and notifications for that provider are answered `200` and ignored.
- Financial recovery for those reservations stops.

To recover, restore the previous provider's configuration and restart. Then handle the affected reservations with the reviewed `recover-settlement` or `release-authorization` manifest flow in [Operations](operations.md#explicit-financial-recovery). Do not edit `PaymentProvider` or the reference columns by hand.

Moving Saferpay from the test environment to live is the same kind of switch: test-environment transactions cannot be read through the live API, so finish them before changing `Saferpay:BaseUrl` and the credentials.

### Known gaps

- There is no refund API flow for either provider. Refunds are made in the provider's dashboard or back office, and no reservation or invoice state is updated automatically.
- The Saferpay mock (`Saferpay:UseMockServices`) keeps its tokens and transactions in memory, so a server restart in the middle of an E2E flow loses them. The Stripe mock can persist to `Stripe:MockDiagnosticsDirectory`; the Saferpay mock has no equivalent.
- An open Saferpay payment page cannot be resumed, because its redirect URL is single-use. Resume reports `ResumeUnavailable`, and the payer cancels the previous attempt and starts again. A page that was already paid is confirmed instead, charging starts, and the payer continues on the status page. A Resume within 2 seconds of another Saferpay check of the same reservation is throttled: it makes no Saferpay call and reports `Status`, so the start page sends the payer to the polling status page instead of offering to cancel a payment that may be being confirmed. If Saferpay still authorizes an abandoned page later, that late authorization is cancelled automatically (see [Features](features.md#payments-and-reservations)). Without notifications this can take until the page lifetime plus 5 minutes.
- How long a Saferpay hold stays valid depends on the payment method, the acquirer contract and `Saferpay:PreAuth`. Confirm with the acquirer that pre-authorization is enabled for the merchant category.
- Saferpay notifications have no payload, and the server re-asserts every one of them. The server answers the notification before it starts charging, so a slow charger does not delay the answer past Saferpay's 10-second timeout.
- When Saferpay payments fail, the server returns message texts that the public portal's translation table does not cover, so payers may see them in English. The refund notice for a payment captured at checkout is translated.
- The management admin banner reads the management app's own `Payments:Provider` and `Stripe:WebhookSecret`, not the server's.
- `MarkTransactionStarted` (linking a `StartTransaction` to its reservation) still writes `Charging` with a plain save. If the start-timeout sweep writes `StartTimeout` and cancels the hold while a late `StartTransaction` that read the reservation as `Authorized` is being processed, the reservation can end `Charging` on a cancelled hold. Its settlement then finds the authorization cancelled and fails the reservation.
- Conditional status updates exist only on relational providers (SQL Server, SQLite). The EF in-memory provider used by most unit tests falls back to a status check plus `SaveChanges`, so only the SQLite race tests in `PaymentCoordinatorProviderTests.Races.cs` exercise the races.
- The settlement retry keeps the connector locked (`Charging` or `WaitingForDisconnect`) for up to `Maintenance:SettlementRetryWindowMinutes`. The window is measured from the disconnect, so a settlement recovered long after it (for example an offline charger recovered by the sweep) gets no transient retry and fails with `settlement_retry_exhausted`. There is no per-reservation retry counter. Explicit `recover-settlement` runs are not retried.
- When release reconciliation finds a captured Saferpay authorization (a payment captured at checkout), the release ends in `ReviewRequired`, but the reservation does not get `FailureCode=captured_at_checkout`.
- `/Payments/MockCheckout` is routed in every environment, not only when a provider mock is enabled. It is an anonymous page that only renders the given return links, and it accepts only relative or absolute `http`/`https` URLs (any host).
- Some names predate provider selection and are kept for compatibility: the `StripeCheckoutSessionId` and `StripePaymentIntentId` columns, the `StripeWebhookEvent` entity, the `IStripeBuyerMetadataReconciliationJob` Hangfire job, and the `{StripePaymentIntentId}`/`{StripeCheckoutSessionId}` invoice remark placeholders.
- These Saferpay behaviours were implemented from the JSON API specification and the Saferpay mock, and have not yet been checked against the Saferpay test environment:
  - Which error names and HTTP statuses are returned when cancelling an already cancelled or captured transaction, and for `TOKEN_INVALID` and `TRANSACTION_NOT_FOUND` (`402` or `400`). `TRANSACTION_IN_WRONG_STATE` and `TRANSACTION_ALREADY_CAPTURED` trigger a `Transaction/Inquire`. Only an inquiry that shows the transaction cancelled counts as released; a captured transaction raises the permanent error `transaction_already_captured`, and any other answer the transient error `cancel_state_unknown`. Other refusals, such as `ACTION_NOT_SUPPORTED`, are passed on as provider errors.
  - How long `Transaction/Inquire` lags behind an authorization or capture. Inquire lists only successful transactions. A `TRANSACTION_NOT_FOUND` for a known transaction id is therefore treated as the transient error `inquire_not_found`. A lookup by order id that finds nothing is taken as "no successful transaction", which is why it is used only for closed pages or after the page lifetime.
  - Whether `PaymentPage/Assert` error responses carry the `OrderId`, as the specification says. On `TOKEN_EXPIRED` the gateway uses it (or the `TransactionId`) to look for a transaction the payer authorized before the token expired.
  - How the payment page looks with `CollectPayerEmail=true`, which shows the billing-address form, and whether the e-mail is prefilled from the R1 address.
  - Capture replays after a lost response. These rely on `TRANSACTION_ALREADY_CAPTURED` plus `Transaction/Inquire`, which can lag. While Inquire still shows the authorization in the same currency, the gateway reports the requested amount, capped at the authorized amount, as captured with raw status `CAPTURED_UNVERIFIED`. The coordinator then completes the reservation with `FailureCode=capture_amount_unverified` and issues no invoice or receipt. Any other Inquire answer raises the transient error `capture_state_unknown`, which the settlement retry covers.
  - Retry identification. `RetryIndicator` is always `0`, and `RequestId` is derived from the idempotency key. Saferpay treats both as debugging data and does not deduplicate requests.
  - How long `PaymentPage/Assert` keeps working after checkout. The specification gives 24 hours.
  - That a payment page can no longer be paid after one hour, which `Saferpay:CheckoutLifetimeMinutes` (default 60) assumes.

## Known Technical Debt / Risk

- No dedicated formatter/linter configuration was found.
- No `global.json` pins the .NET SDK.
- CI has overlapping unit-test workflow files.
- Some generated/runtime artifacts appear tracked, including SQLite files and Playwright report/test-result files.
- The middleware is large and would benefit from careful extraction only with strong tests.
- Public appsettings contain development/sample values that look secret-like; do not treat them as deployable secrets.
- Charger-side reservation profile behavior is disabled in code while related option naming remains.
- SQL scripts and EF migrations may represent overlapping database history; authoritative use is Unknown / verify.

## Recommended Future Cleanup

- Add a sanitized `appsettings.example.json` pattern and move secret-like development values out of public examples.
- Decide whether checked SQLite and Playwright output files are fixtures or generated artifacts.
- Consolidate duplicate/overlapping CI workflows if they do not serve distinct purposes.
- Consider pinning the .NET SDK with `global.json` after maintainers agree on the supported SDK.
- Add docs or tests for extension packaging and runtime deployment if extensions are actively supported.
- Break down `OCPPMiddleware.cs` only when a change already requires touching related behavior and tests can protect the split.
