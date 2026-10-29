# 0005 - Pluggable payment providers

Status: accepted. Provider selection for new checkouts is extended per charge point by [ADR 0006](0006-payment-provider-per-charge-point.md).

## Context

Public charging pre-authorizes a maximum amount, starts the session, and at the end captures the final amount or releases the hold. That lifecycle used to be hard-wired to Stripe. `StripePaymentCoordinator` combined three kinds of logic:

- the reservation state machine, pricing, settlement, authorization-release reconciliation, e-mails, and invoice hand-off;
- Stripe SDK calls, Stripe status strings, and Stripe webhook parsing;
- the middleware's webhook route, which also parsed the Stripe JSON itself to decide whether to start charging.

Operators need to run the same flow with another payment service provider (PSP), chosen by configuration, starting with Worldline Saferpay. Saferpay differs from Stripe in ways that matter to the lifecycle:

- The Payment Page has one return URL for every outcome and adds nothing to it.
- Notifications are plain `GET` calls with no payload, signature, or authentication.
- The PSP stores no free-form metadata.
- `PaymentPage/Assert` works only for a limited time after checkout (24 hours in the specification). `Transaction/Inquire` can return the payer details later by transaction id, but it is meant for occasional lookups (fair use, no polling) and its data can lag behind the payment.
- A capture can be done only once and must be positive. A smaller capture releases the remainder, and a zero amount must be cancelled instead.

Reservations are long-lived: authorization, charging, idle time, capture or release, and invoice retries can span hours. Some of them will still be in flight whenever an operator changes the provider.

## Decision

1. **One provider-neutral coordinator on top of a gateway contract.** `PaymentCoordinator` keeps the whole reservation lifecycle. `IPaymentGateway` (`OCPP.Core.Server/Payments/Providers/`) is the only provider-specific seam.
   - It creates a checkout, reads a checkout or authorization, captures, cancels, mirrors metadata, parses webhooks, and sanitizes provider text.
   - It returns neutral snapshots and throws `PaymentProviderException` with a stable code and a transient flag.
   - Capability flags (`SuccessUrlSessionPlaceholder`, `SupportsMetadata`, `SupportsDeferredCheckoutLookup`) let the coordinator adapt without provider-specific branches. Later additions with neutral defaults: `CheckoutLifetime` (how long a checkout can still complete), `CapturedCheckoutRequiresRefund` (an authorization already captured at checkout must be refused) and `FindAuthorizationByReservation` (look up an authorization by reservation id).
   - `StripePaymentGateway` and `SaferpayPaymentGateway` implement it. `StripePaymentCoordinator` remains as a thin single-gateway subclass for existing callers and tests.
2. **Registry and configuration.** `AddPaymentProviders` registers every gateway and a `PaymentGatewayRegistry`.
   - `Payments:Provider` (empty = Stripe, case-insensitive) selects the active gateway for new checkouts. An unknown value fails at startup instead of silently falling back.
   - Provider-neutral checkout settings (`Payments:ReturnBaseUrl`, `Payments:Currency`, `Payments:ProductName`) fall back to the existing `Stripe:*` keys, so existing deployments need no change.
3. **Per-reservation provider.** Migration `AddPaymentProvider` adds `ChargePaymentReservation.PaymentProvider`, with default `Stripe` for existing rows, and nullable `PayerEmail` and `PayerName`.
   - The reservation's own provider handles every operation on an existing reservation, never the active provider.
   - Payer details are persisted at authorization. That is simpler and more robust than reading them from the PSP again hours later, and it lets a provider without a reliable deferred lookup (`SupportsDeferredCheckoutLookup=false`, Saferpay) drive e-mails and invoices. A deferred lookup through `Transaction/Inquire` remains a possible enhancement.
4. **Reuse the Stripe-named reference columns.** `StripeCheckoutSessionId` and `StripePaymentIntentId` hold the owning provider's checkout and authorization references: for Saferpay, the payment page token and the transaction id. The `StripeWebhookEvent` deduplication table is reused too, and ids from providers other than Stripe are prefixed with the provider name.
5. **Saferpay notifications are signed signals.**
   - The server gives Saferpay notification URLs that carry the reservation id and an HMAC signature (`Saferpay:NotifySecret`, or a key derived from the API credentials).
   - A valid notification only tells the coordinator to call `PaymentPage/Assert` for the stored token. State changes come only from that server-side answer.
   - The browser return also confirms by reservation id and the stored token.
6. **Verified webhook auto-start.** Webhook routing is `/API/Payments/Webhook` (legacy Stripe) plus `/API/Payments/Webhook/{provider}`.
   - The coordinator returns the reservations it verified and saved as `Authorized`, and the middleware starts charging only for those, after it has answered the PSP.
   - The raw webhook body never drives a start.
   - A signed Stripe `POST` to an unknown provider segment or a deeper path is still handled as the legacy route, with a deprecation warning, because every path below `/API/Payments/Webhook` used to be the Stripe webhook.
7. **Fail closed.** A reservation whose provider is unknown, unregistered, or unconfigured is never sent to another provider:
   - capture marks it `Failed`;
   - release ends as `PermanentFailure`;
   - confirm and resume return `ProviderUnavailable`;
   - recovery stops.

## Alternatives Considered

- **A separate `SaferpayPaymentCoordinator` copied from the Stripe coordinator.** This was fastest to start, but it would duplicate several thousand lines of state machine, settlement, release, and invoice logic that already have extensive regression tests. Every later fix would have to land twice. Rejected.
- **Renaming the reference columns** (for example to `CheckoutReference` and `AuthorizationReference`). This is cleaner, but it is a breaking schema change. It touches every query, report, export, recovery manifest, and the model snapshot, and older application versions would fail against a renamed column during rolling deployments. Rejected for now; the column comments and documentation describe the generic meaning.
- **New generic columns plus a backfill from the Stripe columns.** This avoids the rename, but two copies of each reference would have to stay consistent, and the migration would rewrite historical financial rows. Rejected.
- **A single active provider without per-row routing.** This is simpler, but changing `Payments:Provider` would strand every in-flight reservation: holds could not be captured or released through the provider that created them. Rejected in favour of the per-row `PaymentProvider` column.
- **Trusting Saferpay notification content or the return URL.** Saferpay sends no data or signature, so nothing in the call can be trusted. Server-side `Assert` is the only authoritative source. Rejected.

## Consequences

- Adding another PSP means adding one gateway, its options, and its DI registration. The coordinator, settlement, release reconciliation, and invoice logic do not change.
- Operators can switch providers by configuration and a restart. The previous provider must stay configured until its reservations are finished. Removing it early fails closed and needs manual recovery.
- SQL Server deployments need migration `AddPaymentProvider`. Local SQLite files must be recreated, because `EnsureCreated()` does not add columns.
- Stripe-named columns, the webhook-event entity, a Hangfire job interface, and invoice remark placeholders keep their names for compatibility, even when they hold Saferpay data.
- Coordinator log prefixes change from `Stripe/...` to `Payments/...`, so log-based alerts need updating.
- Saferpay depends on the browser return or the signed notification for timely authorization. When neither arrives, the stale-pending sweep abandons the reservation. Its payment page can still be paid for up to an hour (`CheckoutLifetime`), so release reconciliation treats an open page as closed only after that lifetime plus 5 minutes, then looks the transaction up by order id and releases a hold it finds. Without notifications, such a late hold stays in place until then.
- An authorization that appears after a reservation ended without a charging session (`Cancelled`, `Abandoned`, `Failed`, `StartRejected`, `StartTimeout`) is cancelled as soon as a webhook, notification, Saferpay browser return or release lookup reveals it, for both providers. The exception is a reservation that was authorized and may own an unlinked transaction (or whose linkage cannot be evaluated): its hold is kept for review.
- The flow needs deferred capture. Saferpay payment methods that capture at checkout cannot be used: the default method filter offers only `VISA`, `MASTERCARD` and `MAESTRO`, and a payment captured at checkout fails the reservation (`captured_at_checkout`) and needs a manual refund.
- Saferpay `Transaction/Inquire` lists only successful transactions and can lag. A not-found answer for a known transaction is treated as transient, never as proof that no hold exists, and a cancel counts as a release only when Saferpay shows the transaction cancelled.
- A provider confirmed by reservation id alone (no checkout reference in the return URL) is checked only for `Pending` reservations and at most once every 2 seconds per reservation, for confirm and resume together.
- Saferpay has no metadata copy of buyer data. The reservation snapshot is the only source for R1 invoices.
- Partial capture is a single capture in both providers. Zero and below-minimum final amounts cancel the authorization instead.
- No refund flow is modelled for either provider.

## Unknowns / Verify

- Some Saferpay error names and HTTP statuses have not been checked against the Saferpay test environment, for example when cancelling an already cancelled transaction, and for `TOKEN_INVALID` and `TRANSACTION_NOT_FOUND`. The mock follows the published specification.
- How long `Transaction/Inquire` lags behind an authorization or capture. While it lags, a capture replay is reported at the requested amount (capped at the authorization) as `CAPTURED_UNVERIFIED`; the reservation completes with `FailureCode=capture_amount_unverified` and no invoice until an operator checks the amount.
- How long a hold stays valid, and whether pre-authorization is available, depends on the acquirer contract and the merchant category.
