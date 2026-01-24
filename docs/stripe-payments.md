# Stripe Payments in OCPP.Core

This project uses Stripe Checkout with manual capture to reserve a maximum charging amount before sending remote start. The server holds funds at session start and captures only the actual cost when the transaction ends.

## Configuration
- `Stripe:Enabled` / `STRIPE_ENABLED`: turn integration on.
- `Stripe:ApiKey` / `STRIPE_API_KEY`: Stripe secret key (test or live).
- `Stripe:ReturnBaseUrl` / `STRIPE_RETURN_BASE_URL`: public base of the management UI; used for success/cancel redirects (e.g., `https://app.tehnocharge.ib-tehnologije.hr`).
- `Stripe:WebhookSecret` / `STRIPE_WEBHOOK_SECRET`: signing secret for the webhook endpoint in the same mode (test/live).
- Optional: `Stripe:Currency` (default `eur`), `Stripe:ProductName`.
- Security: webhook signature verification is required unless `Stripe:AllowInsecureWebhooks` is explicitly set `true` (intended for local dev only).
- Idempotency: Checkout creation and captures use deterministic idempotency keys; webhook events are deduped if the `StripeWebhookEvents` table exists (add via EF migration).
- Cleanup: a background sweep cancels stale pending/authorized reservations after `Maintenance:ReservationTimeoutMinutes` (defaults to 60). Interval can be tuned with `Maintenance:CleanupIntervalSeconds` (defaults to 60, min 30).
- Deployment: Traefik terminates TLS on `app.tehnocharge.ib-tehnologije.hr` (management) and `api.tehnocharge.ib-tehnologije.hr` (server). Webhook target is on the API host.

## API surface (server)
- `POST /API/Payments/Create` — calculates max amount, creates Stripe Checkout Session (manual capture), stores reservation, returns `checkoutUrl`.
- `POST /API/Payments/Confirm` — after Stripe redirect, validates session status, ensures PaymentIntent is `requires_capture`/`succeeded`, marks reservation `Authorized`.
- `POST /API/Payments/Cancel` — cancels the PaymentIntent (if any) and marks reservation `Cancelled`.
- `POST /API/Payments/Webhook` — receives Stripe events (see below).

## Reservation lifecycle (statuses)
PendingPayment → Authorized → StartRequested → Charging → Completed  
Branching: PendingPayment/Authorized may go to Cancelled or Failed on errors/cancel; Charging can end in Completed, Cancelled, or Failed.

## Price and hold calculation
- Uses charge point pricing (price per kWh, session fee, usage/idling fee caps) to compute a maximum in cents; creates a single line item for that max.
- Checkout Session uses `payment_intents` in `manual` capture mode. PaymentIntent metadata carries reservation/CP/connector/tag IDs.

## Webhooks (required)
Subscribe only to:
- `checkout.session.completed` — sets reservation to Authorized (if it was still PendingPayment) and backfills PaymentIntentId if missing.
- `checkout.session.expired` — marks a still-pending reservation Cancelled when the Checkout session times out.
- `payment_intent.payment_failed` — marks reservation Failed and stores Stripe’s last error.

Webhook endpoint (test and live, one per mode):
`https://api.tehnocharge.ib-tehnologije.hr/API/Payments/Webhook`

Keep the signing secret in `Stripe:WebhookSecret`. If absent, webhooks are rejected unless `Stripe:AllowInsecureWebhooks=true` (dev only).

## Happy-path flow
1) Management UI calls `POST /API/Payments/Create` with `chargePointId`, `connectorId`, `chargeTagId`. Response includes `checkoutUrl` and `reservationId`.
2) Driver pays via Stripe Checkout. Stripe redirects to `/Payments/Success` on the management UI (built from `ReturnBaseUrl`); UI then calls `POST /API/Payments/Confirm` with `reservationId` + `session_id`.
3) Confirm sets reservation `Authorized`; middleware flags it `StartRequested` and sends RemoteStart to the charger.
4) When the charger starts, `MarkTransactionStarted` sets status `Charging`.
5) On transaction stop, `CompleteReservation`:
   - Computes actual energy, usage/idling minutes, session fee.
   - Captures PaymentIntent for that amount (capped at the original hold). If amount is zero, it cancels instead of capturing.
   - Persists a cost breakdown on the transaction (energy, usage, session fee, operator/owner splits).
6) Reservation finishes as `Completed` (capture) or `Failed` on Stripe errors.

## Failure and edge cases
- Stripe webhook signature missing/invalid → event discarded (warning logged).
- Checkout session mismatch or not complete on Confirm → `SessionMismatch` / status returned; reservation stays PendingPayment.
- PaymentIntent unexpected status during Confirm or capture → reservation marked Failed with last error.
- Cancel endpoint or zero capture amount → PaymentIntent cancelled; reservation `Cancelled`.
- Concurrency: unique index prevents two active reservations per connector; Create throws `ConnectorBusy` on conflict.

## Test vs live
- Create separate webhooks in Stripe test and live; copy each signing secret to the matching environment.
- Test keys (`sk_test_*`, `pk_test_*`) stay in non-production envs; swap to live keys and live webhook secret for go-live.

## References
- Code: `OCPP.Core.Server/Payments/StripePaymentCoordinator.cs`, `OCPP.Core.Server/OCPPMiddleware.cs` (Payments routes), `OCPP.Core.Server/Payments/IPaymentCoordinator.cs`.
- User flow doc: `docs/Public-Payments-and-QR.md`.
