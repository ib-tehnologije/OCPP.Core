# Stripe Payments Fit–Gap (vs stripe-payments-implementation-spec.md)

Scope: Compare the proposed spec to the current implementation in this repo. Source of truth is `OCPP.Core.Server/Payments/StripePaymentCoordinator.cs` and `OCPP.Core.Server/OCPPMiddleware.cs`.

## Summary
- Core flow (Checkout + manual capture on StopTransaction) matches the spec’s Option A.
- Only two webhooks are handled today (`checkout.session.completed`, `payment_intent.payment_failed`); no event idempotency store; webhook signature is optional.
- Status model is leaner than the spec and already enforced via a DB unique index to prevent concurrent active reservations.
- Cancelling PaymentIntents created by Checkout is attempted in code; Stripe allows cancelling an uncaptured PI, so this is acceptable, but the spec’s warning is noted.

## Detailed fit/gap
- Webhook signature enforcement: Optional. If `Stripe:WebhookSecret` is empty, code logs a warning and still processes (HandleWebhookEvent). Spec requires rejecting or failing startup. Gap.
- Webhook signature enforcement: Now enforced unless `Stripe:AllowInsecureWebhooks=true` (dev only). ✅
- Webhook events: Handles `checkout.session.completed`, `checkout.session.expired`, and `payment_intent.payment_failed`. Still skips async payment events. Partial gap.
- Event idempotency: Implemented best-effort store (`StripeWebhookEvents`); requires DB migration to take effect. ✅ (pending migration)
- Idempotency keys on Stripe API calls: Added deterministic keys for Checkout create and capture (and cancel). ✅
- Status model: Uses `PendingPayment`, `Authorized`, `StartRequested`, `Charging`, `Completed`, `Cancelled`, `Failed`. No distinct `Expired`, `FailedPayment`, `FailedStart`, `CaptureFailed`, `Abandoned`. Partial gap.
- Connector concurrency: Enforced via unique index `UX_PaymentReservations_ActiveConnector`; code throws `ConnectorBusy`. Fits.
- Cancel flow: `CancelReservation` calls `_paymentIntentService.Cancel` if a PaymentIntent exists. Acceptable for uncaptured PIs created by Checkout; spec says avoid for Checkout but Stripe permits cancel of uncaptured PaymentIntents.
- Confirm gating: Confirms both Checkout session status (`complete`) and PaymentIntent status (`requires_capture`/`succeeded`). Fits spec intent.
- Capture on StopTransaction: Captures the computed amount, capped by authorized amount. If amount is zero, it cancels the PI (spec suggests skip-capture instead). Minor divergence.
- Pricing and hold calculation: Already computes max hold from CP pricing and uses a single line item with manual capture. Fits.
- Background cleanup: Added periodic service cancelling stale pending/authorized/start-requested reservations using `Maintenance:ReservationTimeoutMinutes`. ✅
- Observability/metrics: Basic logging only; no metrics. Gap.
- Config surface: Missing `CheckoutSessionTtlMinutes`, `PaymentMethodTypes`. `AllowInsecureWebhooks` added. Partial gap.

## Recommendation
Minimal, low-risk alignment steps:
1) Enforce webhook signature in non-Development environments; optionally allow `Stripe:AllowInsecureWebhooks` for local only.
2) Add `checkout.session.expired` handling to mark PendingPayment reservations as Cancelled/Expired.
3) Add idempotency for webhooks by storing processed `event.id` (small table) and short-circuit duplicates.
4) Use deterministic idempotency keys on Checkout create and PaymentIntent capture.

Optional follow-ups:
- Add TTL/background cleanup for stale PendingPayment.
- Add zero-amount policy (either cancel PI as today or mark CaptureSkipped).
- Extend status model only if product needs the finer states.
