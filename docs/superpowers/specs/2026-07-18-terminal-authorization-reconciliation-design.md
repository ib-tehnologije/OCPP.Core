# Terminal Authorization Reconciliation Design

## Purpose

Prevent a manual-capture payment authorization from remaining on hold when a reservation becomes terminal before Stripe reports that the PaymentIntent is capturable. The change is prevention-only: it must not backfill or automatically process terminal reservations that predate this feature.

## Selected Architecture

Use one durable reconciliation path with three triggers:

- `checkout.session.completed`, when the completed Checkout Session supplies the PaymentIntent after local cleanup has already made the reservation terminal
- `payment_intent.amount_capturable_updated`, when Stripe reports that a manual-capture PaymentIntent has funds available to capture
- the existing payment reservation cleanup sweep, which recovers missed or reordered webhooks and resumes bounded retries after process restarts

All triggers call the same coordinator method. That method always retrieves the current PaymentIntent from Stripe before deciding whether to cancel it. A cancellation is allowed only when the local reservation is explicitly armed for prevention, the reservation is `Abandoned` or `Failed`, there is no active transaction, no captured amount or capture timestamp, no submitted invoice evidence, the PaymentIntent metadata belongs to the reservation, and the provider status is exactly `requires_capture` with a positive capturable amount.

## Prevention-Only Boundary

The migration adds nullable authorization-release state to `ChargePaymentReservation` and does not backfill it. Existing terminal rows therefore remain outside the sweep.

New code arms reconciliation only when it creates a new terminal no-charge condition:

- stale pending cleanup marks a reservation `Abandoned`
- a no-charge or below-provider-minimum completion path encounters a provider failure while trying to release the authorization

Webhooks and sweeps may reconcile only an armed reservation. This preserves the separate approval boundary for historical payment remediation.

## Persistence

`ChargePaymentReservation` stores the current summary needed for efficient restart-safe scans:

- authorization release state
- attempt count
- last attempt timestamp
- next retry timestamp
- released timestamp
- last sanitized provider error

A new `PaymentAuthorizationReleaseAttempt` entity stores one row per reconciliation attempt:

- reservation and PaymentIntent identifiers
- monotonic attempt number and trigger
- start and finish timestamps
- provider status and capturable amount observed at action time
- outcome
- sanitized provider error code and message
- next retry timestamp, when applicable

The audit table has a foreign key to the reservation, a unique `(ReservationId, AttemptNumber)` index, and indexes supporting reservation history and due retry inspection.

## Reconciliation Outcomes

- `Released`: the provider accepted the cancellation.
- `AlreadyReleased`: direct provider state is already `canceled`; this is a successful no-op.
- `RetryScheduled`: a bounded transient provider or network failure occurred.
- `PermanentFailure`: retry budget is exhausted or the provider failure is non-transient.
- `ReviewRequired`: local ownership, activity, charge, invoice, identifier, or provider state is ambiguous or unsafe.
- `SkippedNotEligible`: the reservation is not armed or no longer satisfies the strict local allowlist.

`succeeded` and other captured evidence always produce `ReviewRequired`; they never trigger cancellation. Missing or mismatched ownership metadata also produces `ReviewRequired`.

## Retry and Idempotency

The maximum attempt count and exponential retry base use bounded `Maintenance` configuration with safe defaults. Each attempt retrieves provider state first. Cancellation uses an idempotency key derived from the reservation and attempt number.

If a provider call has an indeterminate result, the next attempt retrieves the PaymentIntent again. If the earlier cancellation succeeded, the provider now reports `canceled` and reconciliation finishes as `AlreadyReleased`; otherwise the new attempt uses a new key only after provider state still proves `requires_capture`. This keeps retries safe without pinning every later attempt to a cached provider error.

## Error Handling and Privacy

Provider errors are stored in the dedicated release fields and attempt audit rows, not in the generic terminal reason fields. The reconciler stores only bounded error code/message text and sanitizes email addresses and provider-style object/request identifiers. Raw provider response bodies are never persisted.

Cleanup keeps the generic terminal reason but does not overwrite a more specific existing failure. Provider failures remain available in the dedicated authorization-release audit even when reservation status is terminal.

## Tests

Focused tests cover:

- cleanup before webhook and webhook before cleanup
- amount-capturable, checkout-completed, duplicate, reordered, and missing webhook paths
- successful release and already-canceled no-op
- transient retry, retry exhaustion, permanent failure, and process-restart sweep recovery
- repeated sweeps and provider idempotency
- missing identifier and mismatched ownership
- active transaction, captured payment, submitted invoice, unexpected provider status, and succeeded-payment exclusion
- preservation of the existing below-energy-threshold, minimum-charge, invoice/email suppression, and reservation-lock behavior
- migration discovery and SQL Server-shaped metadata

## Documentation and Deployment Impact

Public-safe documentation describes the background reconciliation, configuration, audit table, migration, and prevention-only rollout boundary. Deployment requires applying the EF Core migration. No new service, secret, provider setting, client message, or production action is introduced by this implementation.
