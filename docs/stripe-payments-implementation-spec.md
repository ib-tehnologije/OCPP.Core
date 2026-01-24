# Stripe payments for OCPP.Core — implementation spec (Codex-ready)

This spec replaces/updates the current “Stripe Payments in OCPP.Core” write‑up. It is written for Codex to implement end‑to‑end, safely, and with correct Stripe semantics.

It assumes an EV‑charging flow where you:
1) **Authorize (hold) up to a maximum amount** before sending OCPP RemoteStart, then
2) **Capture only the actual final amount** when OCPP StopTransaction arrives.

> **Important reality check (Checkout limitation):** If you use **Stripe Checkout**, Stripe documents that you **can’t cancel the PaymentIntent created by Checkout** via the PaymentIntents cancel API. That means you cannot guarantee “instant release of the hold” after a completed Checkout Session. You *can* expire a session while it’s still open.

This spec provides:
- **Option A (keep Checkout)**: minimal surface change; robust + production‑safe.
- **Option B (recommended for truly perfect hold release)**: migrate from Checkout to direct PaymentIntents + Stripe.js Payment Element.

---

## 1) Stripe concepts used

### Separate authorization + capture
- Create a PaymentIntent with `capture_method=manual` to authorize funds.
- Later call `payment_intents.capture` to capture an amount ≤ authorized amount.
- Stripe cancels uncaptured authorizations after a window (commonly 7 days; see Stripe docs for your account).

### Checkout vs PaymentIntents
- **Checkout** is a hosted checkout flow that creates the PaymentIntent *for you*.
- Stripe docs note restrictions around canceling PaymentIntents tied to Checkout. Plan accordingly.

---

## 2) Non‑negotiable requirements (must implement)

### 2.1 Webhook signature verification must be enforced
- In **production**, webhook signature verification is mandatory.
- If `Stripe:WebhookSecret` is missing in production:
  - Fail application startup **or** reject all webhook requests with HTTP 500/400 and clear logs.
- In local dev only, allow an explicit opt‑out flag if needed (`Stripe:AllowInsecureWebhooks=true`), and log a loud warning.

> Signature verification requires the **raw request body** (frameworks often parse JSON and lose the original bytes). Use Stripe’s recommended verification method.

### 2.2 Idempotency everywhere
Implement idempotency in three layers:
1) **Stripe API calls**: pass deterministic idempotency keys for Create/Expire/Capture.
2) **Webhook processing**: store Stripe `event.id` and ignore duplicates.
3) **DB state transitions**: updates must be safe to retry.

### 2.3 Connector concurrency lock
A connector must have at most one active reservation in these statuses:
- `PendingPayment`, `Paid`, `StartRequested`, `Charging`

Use:
- A DB unique index/constraint (preferred), or
- Serializable transaction + row locks.

---

## 3) Recommended status model

Use these statuses (rename if you already have equivalents):

- `PendingPayment` — Checkout session created, awaiting payment
- `Paid` — Stripe confirms payment succeeded and PI is capturable
- `StartRequested` — RemoteStart sent
- `Charging` — OCPP StartTransaction confirmed
- `Completed` — Capture succeeded (or capture skipped per policy)
- `Expired` — Checkout session expired before payment
- `Cancelled` — Session expired by server before payment
- `FailedPayment` — Stripe payment failed
- `FailedStart` — Paid but OCPP start failed / timed out
- `CaptureFailed` — capture attempted but Stripe rejected
- `Abandoned` — Paid but never started; capture not attempted

**Allowed transitions**
- PendingPayment → Paid (only when Stripe says paid)
- Paid → StartRequested (when you send RemoteStart)
- StartRequested → Charging (when StartTransaction received)
- Charging → Completed (when capture succeeds / skipped policy)
- PendingPayment → Expired/Cancelled/FailedPayment
- Paid/StartRequested → FailedStart/Abandoned
- Charging → CaptureFailed

---

## 4) Configuration

Keep existing keys, add the missing ones.

### Required
- `Stripe:Enabled` (bool)
- `Stripe:ApiKey` (secret key)
- `Stripe:WebhookSecret` (endpoint secret for the environment)
- `Stripe:ReturnBaseUrl` (for success/cancel redirects)

### Recommended
- `Stripe:Currency` (default `eur`)
- `Stripe:ProductName` (shown on Checkout)
- `Stripe:CheckoutSessionTtlMinutes` (default 30; min 30, max 1440)
- `Stripe:PaymentMethodTypes` (default `["card"]`)
- `Stripe:AllowInsecureWebhooks` (default false; only for local dev)

---

## 5) Data model changes

### 5.1 Table: PaymentReservations (or equivalent)
Add/ensure fields:

- `Id` (GUID)
- `ChargePointId`
- `ConnectorId`
- `ChargeTagId` / `UserId`
- `Currency` (string)
- `MaxHoldAmount` (int, minor units)
- `FinalAmount` (int?, minor units)
- `StripeCheckoutSessionId` (string)
- `StripePaymentIntentId` (string?, nullable until payment completes)
- `Status` (string/enum)
- `CreatedAt`, `UpdatedAt`
- `StartRequestedAt` (nullable)
- `TransactionId` (nullable)
- `LastErrorCode`, `LastErrorMessage` (nullable)
- `CaptureSkipped` (bool, default false) — useful for finalAmount=0 policy

Indexes/constraints:
- Unique constraint to prevent concurrent active reservations per connector:
  - Unique (`ChargePointId`, `ConnectorId`) where `Status in (PendingPayment, Paid, StartRequested, Charging)`
- Index on `StripeCheckoutSessionId`
- Index on `StripePaymentIntentId`

### 5.2 Table: StripeWebhookEvents (new)
Purpose: idempotency + audit.

- `EventId` (Stripe `evt_…`), PRIMARY KEY/UNIQUE
- `Type`
- `Created` (Stripe timestamp optional)
- `ProcessedAt`
- `ReservationId` (nullable)
- `PayloadJson` (optional; be mindful of storage)

---

## 6) API surface (Option A: keep Checkout)

Endpoints below use your existing routes as much as possible.

### 6.1 POST `/API/Payments/Create`

**Request**
- `chargePointId`
- `connectorId`
- `chargeTagId`

**Server steps**
1) Validate connector exists, is available, and no active reservation exists (enforced by constraint).
2) Compute `MaxHoldAmount` in minor units.
   - Must be ≥ 0
   - Must be within Stripe minimum amount (varies by currency; enforce per your currency rules).
3) Create reservation:
   - Status = `PendingPayment`
4) Create Stripe Checkout Session:
   - `mode="payment"`
   - `line_items[0].price_data.currency = Currency`
   - `line_items[0].price_data.unit_amount = MaxHoldAmount`
   - `line_items[0].price_data.product_data.name = ProductName` (or include CP/connector for debugging)
   - `payment_intent_data.capture_method = "manual"`
   - `client_reference_id = reservationId`
   - `metadata.reservation_id = reservationId` (also set on `payment_intent_data.metadata`)
   - `expires_at = now + CheckoutSessionTtlMinutes`
   - `success_url = {ReturnBaseUrl}/Payments/Success?reservationId={reservationId}&session_id={CHECKOUT_SESSION_ID}`
   - `cancel_url = {ReturnBaseUrl}/Payments/Cancel?reservationId={reservationId}`
   - `payment_method_types = cfg.PaymentMethodTypes` (default card)
5) Persist:
   - `StripeCheckoutSessionId = session.id`
   - **Do not assume** `session.payment_intent` is present at creation time.
6) Return:
   - `reservationId`
   - `checkoutUrl = session.url`

**Idempotency**
- Stripe idempotency key: `checkout_create:{reservationId}`

---

### 6.2 POST `/API/Payments/Confirm`

Called by the UI after redirect, but the system must also work if the UI never calls it (webhook-driven start).

**Request**
- `reservationId`
- `session_id`

**Server steps**
1) Load reservation.
2) Fetch Checkout Session from Stripe by `session_id`.
3) Validate the session belongs to this reservation:
   - `session.id == reservation.StripeCheckoutSessionId`
   - and (`session.client_reference_id == reservationId` OR `session.metadata.reservation_id == reservationId`)
4) Gate on payment:
   - Require `session.status == "complete"`
   - Require `session.payment_status == "paid"` (or `no_payment_required` if you ever support it)
5) Store `StripePaymentIntentId = session.payment_intent` if present.
6) Transition to `Paid` if still `PendingPayment`.
7) Attempt to start charging:
   - If connector still eligible, transition to `StartRequested` and enqueue/sent RemoteStart.
   - If already `StartRequested`/`Charging`, return idempotent success.

**Return**
- Reservation status and any UI instructions.

---

### 6.3 POST `/API/Payments/Cancel`

**Goal**
- Cancel before payment: expire the Checkout Session.
- After payment: cannot reliably “cancel/void the PI” with Checkout; apply policy.

**Request**
- `reservationId`

**Server logic**
1) Load reservation.
2) If Status == `PendingPayment`:
   - Call `checkout.sessions.expire(reservation.StripeCheckoutSessionId)`
   - Set Status = `Cancelled`
   - Release connector lock
3) If Status in `Paid` / `StartRequested` and transaction not started:
   - Set Status = `Abandoned` or `FailedStart` (depending on reason)
   - Do **not** call `payment_intents.cancel` (not supported for Checkout-created PI per Stripe docs).
4) If Status == `Charging`:
   - Treat as “stop charging” domain action; then capture/complete.

**Note**
- If you truly need to void holds after payment, implement Option B.

---

### 6.4 POST `/API/Payments/Webhook`

**Route**
- `https://api…/API/Payments/Webhook`

**Must implement**
- Signature verification (raw body)
- Event idempotency (store `evt_…`)
- Robust matching to reservation

**Subscribe to these events**
Minimum:
- `checkout.session.completed`
- `checkout.session.expired`
- `payment_intent.payment_failed` (optional but useful)

If you ever enable non-card methods:
- `checkout.session.async_payment_succeeded`
- `checkout.session.async_payment_failed`

**Handler: checkout.session.completed**
1) Identify reservation:
   - by `client_reference_id`, or `metadata.reservation_id`, or `StripeCheckoutSessionId`
2) Only proceed if `session.payment_status == "paid"`
3) Set Status = `Paid` (if PendingPayment)
4) Backfill `StripePaymentIntentId`
5) Attempt start (same as Confirm), idempotently.

**Handler: checkout.session.expired**
- If reservation still PendingPayment → Status = `Expired`, release connector.

**Handler: payment_intent.payment_failed**
- Find reservation by PI id (if stored) or by metadata (reservation id).
- Set Status = `FailedPayment`, store Stripe error, release connector.

**Webhook response**
- Always return 200 after successful processing (even if reservation not found—log and store event).

---

## 7) OCPP integration points

### 7.1 When to allow RemoteStart
RemoteStart is allowed only when:
- Reservation Status == `Paid` (or you’re already StartRequested/Charging)
- Connector is not busy / not locked by another active reservation.

### 7.2 Marking transaction started
On OCPP StartTransaction:
- Link transaction → reservation (`TransactionId`)
- Status: `StartRequested` → `Charging`

### 7.3 Completing & capturing on StopTransaction

Implement method: `CompleteReservation(reservationId, ocppStopData…)`

**Compute**
- `FinalAmount` = energy fee + session fee + idle/usage fees (all caps applied)
- Must be ≤ `MaxHoldAmount` (hard invariant). If not, clamp and log error; but ideally prevent it by design.

**Capture**
- Require `StripePaymentIntentId` present.
- Call `payment_intents.capture(paymentIntentId, amount_to_capture=FinalAmount)`
- Stripe idempotency key: `capture:{reservationId}:{FinalAmount}`

**FinalAmount == 0 policy (Checkout)**
Because you can’t reliably cancel the Checkout PI:
- Do **not** attempt to cancel.
- Mark:
  - Status = `Completed`
  - `FinalAmount = 0`
  - `CaptureSkipped = true`
- UI text should explain: “Authorization hold may take time to disappear.”

**Error handling**
- If capture fails:
  - Status = `CaptureFailed`
  - Persist Stripe error details
  - Alert/monitor (this is money-impacting)

---

## 8) Background cleanup jobs (recommended)

Run every 1–5 minutes:

### 8.1 Expire stale pending sessions
For reservations:
- Status == `PendingPayment`
- CreatedAt older than TTL + grace (e.g., 35 minutes)

Actions:
- Fetch session; if still open → expire session.
- Mark Expired.

### 8.2 Paid but never started
For reservations:
- Status == `Paid` or `StartRequested`
- Start timeout exceeded (e.g., 2–5 minutes)

Actions:
- If no StartTransaction:
  - mark `FailedStart` (and release connector)
  - (Checkout limitation) cannot reliably void authorization; log and let hold drop off.

### 8.3 Charging timeout (domain-specific)
If Charging too long, alert/stop; then capture or fail safely.

---

## 9) Observability

Log structured fields:
- reservationId, chargePointId, connectorId, chargeTagId
- stripeCheckoutSessionId, stripePaymentIntentId
- stripeEventId (for webhooks)
- state transitions old→new
- errors with Stripe error codes/messages

Metrics:
- count of reservations created/completed/failed
- capture success/failure
- average time PendingPayment→Paid, Paid→Charging, Charging→Completed

---

## 10) Test plan (must pass)

### 10.1 Happy path
1) Create → PendingPayment, session open
2) Pay in Checkout → webhook completed
3) Reservation becomes Paid and RemoteStart sent
4) StartTransaction → Charging
5) StopTransaction → capture with partial amount
6) Completed and transaction has cost breakdown

### 10.2 Cancel before payment
- Create → Cancel endpoint expires session → Cancelled

### 10.3 Session expiration
- Create → don’t pay → Stripe expires → webhook expired → reservation Expired

### 10.4 Webhook replay/idempotency
- Deliver same `evt_…` twice → second is no-op

### 10.5 Capture failure simulation
- Use a Stripe test scenario that causes capture to fail (or mock) → CaptureFailed state and alert logged

### 10.6 Security
- Invalid webhook signature → request rejected, no DB changes.

---

# Option B (recommended): migrate to PaymentIntents + Payment Element (for “perfect hold release”)

If product requirements include:
- immediate void of authorization after payment, before capture, and
- reliable control over cancel/refund flows,

then migrate away from Checkout.

## B.1 Server creates PaymentIntent
`POST /API/Payments/CreateIntent`
- Create reservation
- Create PaymentIntent:
  - `amount = MaxHoldAmount`
  - `currency`
  - `capture_method=manual`
  - `metadata.reservation_id = …`
- Return `client_secret`

## B.2 Client confirms with Stripe.js Payment Element
- Use Payment Element to confirm the PI client-side
- Redirect to success page
- Call Confirm endpoint (or rely on webhooks)

## B.3 Cancel/void is now possible
- `POST /API/Payments/Cancel` can call `payment_intents.cancel` for uncaptured PI and release hold immediately.

## B.4 Webhooks
Use PaymentIntent webhooks as primary:
- `payment_intent.amount_capturable_updated`
- `payment_intent.payment_failed`
- `payment_intent.succeeded` (after capture)

---

## Changes from the current write-up (summary checklist)

Codex must:
- Remove “process webhooks without verification if secret absent” (prod must verify).
- Do not cancel Checkout PaymentIntents; use session expiration only while open.
- Gate “Authorized/Paid” on Checkout `payment_status == paid`, not only `status == complete`.
- Handle `checkout.session.expired`.
- Treat PaymentIntent id as potentially missing until payment completes; backfill via webhook/confirm.
- Add webhook event idempotency table.
- Implement deterministic Stripe idempotency keys.
- Decide and implement a clear policy for `FinalAmount == 0` under Checkout (recommended: skip capture, mark completed, inform user).

