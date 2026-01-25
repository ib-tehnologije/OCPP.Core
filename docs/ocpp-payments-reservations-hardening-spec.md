# OCPP Reservations + Stripe Payments — Production Hardening Spec (Codex-ready)

This document is the “do this next” spec for Codex to implement so reservations, remote-start, charging lifecycle, and Stripe manual-capture payments behave reliably with *real* OCPP chargers.

It is written against the current repo behavior described in:
- `payments-and-reservations-overview.md` (reservation lifecycle + busy rules + maintenance)  
- `stripe-payments.md` (Stripe endpoints + webhook behavior)

---

## 0) Why this is breaking in production

Real chargers behave like the simulators in the important ways — so the same bugs show up:

1) **“Connector busy” is too strict**  
   Today the connector is treated as busy if *live status != Available* (and the server sometimes force-writes persisted status to `Occupied`).  
   In real installations, it’s common for a connector to be **Preparing** (cable plugged, EV handshake, waiting for authorization). Preparing is *not* the same as “someone is charging”, but your current rule blocks it.

2) **Server-written connector status (“Occupied”) causes sticky states**  
   Setting persisted connector status to `Occupied` after `RemoteStartTransaction` makes your system disagree with the charger and creates “stuck busy” situations that need manual cleanup.

3) **RemoteStart accepted ≠ charging started**  
   Many chargers will accept `RemoteStartTransaction` but still require an `Authorize` step (depending on configuration). If your CSMS doesn’t accept the idTag quickly, the session never starts.

4) **Ordering is not guaranteed**  
   Chargers can send `StatusNotification(Charging)` before `StartTransaction`, or `StatusNotification(Available/Finishing)` before `StopTransaction`. If your code assumes strict order, it will wedge.

5) **Browser redirect is unreliable**  
   If the user never hits `/Payments/Confirm` (3DS oddities, network, closed tab), a paid session might not start unless webhooks can trigger it too.

---

## 1) Design principles (Codex must follow)

### 1.1 Separate “physical status” from “logical lock”
- **Physical connector status** = only what the charger reports via `StatusNotification`.
- **Logical lock** = your reservation row + timeouts (prevents concurrent sessions).

Codex MUST:
- Stop writing fake connector status values like `Occupied` on the server.
- Use reservations (and open transactions) as the lock, not status mutation.

### 1.2 Treat “startable” statuses correctly
For the purpose of allowing a paid start attempt, treat these as **startable**:
- `Available`
- `Preparing`  ✅ (important)

Treat these as **not startable**:
- `Charging`, `SuspendedEV`, `SuspendedEVSE`, `Finishing`, `Reserved`, `Unavailable`, `Faulted`

### 1.3 Idempotency everywhere
- Stripe webhooks retry.
- Chargers retry OCPP messages.
- Your UI can retry API calls.

Codex MUST make:
- `/Payments/Confirm`, webhook handler, and remote-start “TryStart” logic idempotent.
- StartTransaction/StopTransaction association idempotent (no duplicates).

---

## 2) Data model updates

### 2.1 Add fields to `ChargePaymentReservation`
Add columns (names can vary but keep meaning):

- `OcppIdTag` (string, max 20 chars)
- `AuthorizedAt` (datetime?)
- `StartDeadlineAt` (datetime?) — when authorized, set to now + StartWindowMinutes
- `RemoteStartSentAt` (datetime?)
- `RemoteStartResult` (string/enum: Accepted, Rejected, Timeout, Error)
- `RemoteStartAcceptedAt` (datetime?)
- `AwaitingPlug` (bool) OR a status value if you expand the enum
- `StartTransactionId` (int?) — link to OCPP transaction
- `StartTransactionAt` (datetime?)
- `StopTransactionAt` (datetime?)
- `LastOcppEventAt` (datetime?)
- `FailureCode` / `FailureMessage` (strings)

### 2.2 Indexes / constraints
- Keep (or add) a unique constraint that prevents >1 **active** reservation per connector.
- Add index on `(ChargePointId, ConnectorId, OcppIdTag)` for fast correlation.

---

## 3) Reservation status model

You can either:
- **Option A (minimal):** keep existing statuses but use timestamps + `FailureCode` to express richer states; OR  
- **Option B (recommended):** add explicit statuses.

Recommended statuses:

- `PendingPayment`
- `Authorized` (Stripe paid, ready to start)
- `StartRequested` (RemoteStart sent / accepted; waiting for StartTransaction)
- `Charging`
- `Stopping` (optional; seen finishing/available before StopTransaction)
- `Completed`
- `Cancelled`
- `Failed`
- `StartRejected`
- `StartTimeout`
- `Abandoned` (optional: paid but user never started)

Pick one approach and be consistent; the important part is timeouts and correlation.

---

## 4) OCPP correlation: generate a reservation-scoped idTag

### 4.1 Generate `OcppIdTag` per reservation
When a reservation enters `Authorized`, generate an idTag that:
- Is <= 20 characters
- Is unique enough to avoid collisions

Example:
- `R` + base32(random 80–100 bits), truncated to 20 chars.

Store it on the reservation.

### 4.2 Use this idTag in RemoteStart
When sending `RemoteStartTransaction`, always pass this reservation’s `OcppIdTag`.

### 4.3 Accept Authorize.req for active reservations
If the charger sends `Authorize.req` with the reservation’s `OcppIdTag`, then:
- Return `Accepted` only if reservation is active and not timed out.
- Return `Expired/Invalid/Blocked` if reservation is not active or timed out.

This single change fixes a huge class of “RemoteStart accepted but never starts” cases.

### 4.4 Match StartTransaction by (CP, connector, idTag)
When `StartTransaction.req` arrives:
- Prefer to find an active reservation matching:
  - `ChargePointId`, `ConnectorId`, `OcppIdTag == StartTransaction.idTag`
- If found, link transaction to reservation and move to `Charging`.
- If not found:
  - Treat as an unpaid/unknown start and respond accordingly (policy choice).
  - At minimum: do **not** attach it to a paid reservation.

---

## 5) Remote-start orchestration: make it a function

Create a single internal method:

`TryStartCharging(reservationId, caller)`
- caller: Confirm | StripeWebhook | Admin | RetryJob

It must:
1) Load reservation row with FOR UPDATE / transaction lock.
2) Verify reservation is `Authorized` (or `StartRequested`).
3) Verify connector is startable:
   - No open transaction
   - No other active reservation
   - Latest physical status is startable (`Available` OR `Preparing`)
   - Charger appears online (recent messages)
4) If already `StartRequested`, return success (idempotent).
5) Send RemoteStartTransaction with `OcppIdTag`.
6) Store `RemoteStartSentAt` and the result.
7) If accepted:
   - Move to `StartRequested`
8) If rejected:
   - Move to `StartRejected`, set failure fields, and trigger payment unwind (see §8).

IMPORTANT: Never set connector status to `Occupied` here.

---

## 6) Busy/startable check: the new algorithm

Implement a function:

`GetConnectorStartability(chargePointId, connectorId, reservationId?) -> { startable: bool, reasons: [] }`

Rules in order:

1) If charge point is offline (no WS + stale last message) -> not startable.
2) If open transaction exists for connector -> not startable.
3) If another active reservation exists for connector (excluding current reservationId) -> not startable.
4) If latest status is in {Unavailable, Faulted, Charging, Suspended*, Finishing, Reserved} -> not startable.
5) If latest status is in {Available, Preparing} -> startable.

Notes:
- If no live status exists, use persisted status **only if fresh** (keep the freshness window but prefer shorter, e.g. 5–10 minutes in production).
- Return reasons for observability.

---

## 7) Make “paid session start” not depend on browser redirect

### 7.1 Stripe webhook should trigger start
When Stripe confirms payment (webhook `checkout.session.completed` or equivalent), call:

- mark reservation as `Authorized`
- generate/store `OcppIdTag` if not already
- call `TryStartCharging(reservationId, StripeWebhook)`

### 7.2 `/Payments/Confirm` is still supported
But it becomes a *second path* that calls the same idempotent logic:

- validate session
- mark Authorized
- call `TryStartCharging(reservationId, Confirm)`

This eliminates “paid but nothing happens” when user doesn’t return to your site.

---

## 8) Timeouts and automatic release (this is the unstick mechanism)

### 8.1 StartWindowMinutes (NEW)
Add config:
- `Payments:StartWindowMinutes` (default 5–10 minutes)

When reservation becomes `Authorized`, set:
- `StartDeadlineAt = now + StartWindowMinutes`

### 8.2 Start timeout job (every 30–60 seconds)
Sweep reservations in:
- `Authorized` or `StartRequested` (and optionally `AwaitingPlug`)
where:
- `now > StartDeadlineAt`
and:
- no StartTransaction linked

Action:
1) Set status = `StartTimeout` (or `Cancelled` + FailureCode)
2) Release logical lock (reservation is no longer active)
3) Unwind payment:
   - If PaymentIntent is cancelable and uncaptured, cancel it.
   - If using Checkout and cancellation is restricted, mark as “Authorization will drop off” and stop.

### 8.3 RemoteStart rejected path
If RemoteStart is rejected:
- Set status `StartRejected`
- Unwind payment (same as timeout path)
- Release lock immediately

### 8.4 Late StartTransaction after timeout
If StartTransaction arrives with an idTag for a timed-out reservation:
- Return an authorization result that rejects/invalidates it.
- Log and alert.

Do not silently attach it to a paid reservation that is already cancelled/timed out.

---

## 9) StopTransaction handling: tolerate out-of-order + offline delays

### 9.1 Out-of-order stop
If you see `StatusNotification(Available/Finishing)` while you still have an open transaction:
- Move reservation to `Stopping` (optional)
- Keep waiting for `StopTransaction` to compute final cost + capture

### 9.2 Offline delays
A charge point may send StopTransaction later after reconnect.
Do not auto-cancel a charging session quickly just because you didn’t get StopTransaction yet.
Instead:
- keep transaction open
- provide admin tooling to force close if truly necessary

---

## 10) Optional but valuable: Reservation Profile (ReserveNow / CancelReservation)

If the charge point supports it:
- After payment authorization, call `ReserveNow(connectorId, expiryDate, idTag, reservationId)`
- On cancel/timeout, call `CancelReservation(reservationId)` best-effort

This moves some enforcement onto the charger and reduces “someone else plugs in” races.

Implementation detail:
- Detect support via charge point model config, or a per-charge-point capability flag.

---

## 11) Observability: make field debugging possible

### 11.1 Extend `/API/Payments/Status`
Return:
- reservation status + timestamps (AuthorizedAt, RemoteStartSentAt, StartDeadlineAt, StartTransactionAt)
- OcppIdTag
- last known connector physical status + how old it is
- busy reasons computed from §6
- payment status (session/payment intent + capturable?)  

This way, the public status page can show “Waiting for plug”, “Charger offline”, “Start timed out”, etc.

### 11.2 Store and log “why” on failure
Always populate:
- `FailureCode` (enum-ish string)
- `FailureMessage` (human-readable)

Examples:
- `ChargePointOffline`
- `ConnectorNotStartable_PreparingButNoAuthorizeSupport`
- `RemoteStartRejected`
- `StartTimeout`
- `PaymentMismatch`
- `WebhookSignatureInvalid`

---

## 12) Code touchpoints (expected files)

Based on the existing docs, Codex should expect to touch:
- `OCPP.Core.Server/OCPPMiddleware.cs` (busy check + Payments routes)
- `OCPP.Core.Server/Payments/StripePaymentCoordinator.cs` (authorize/capture + webhook hooks)
- `OCPP.Core.Server/Payments/PaymentReservationCleanupService.cs` (add start-timeout sweep)
- `StartupMaintenance.cs` (stop flipping status; only clean locks/transactions)

And add/adjust OCPP handlers for:
- `Authorize`
- `StartTransaction`
- `StopTransaction`
- `StatusNotification`

---

## 13) Acceptance tests (must pass)

### Plug-first (Preparing)
1) Plug cable (charger reports Preparing)
2) Pay
3) Webhook marks Authorized and triggers TryStart
4) RemoteStart accepted
5) StartTransaction arrives
6) Charging
7) StopTransaction -> capture -> Completed

### Pay-first
1) Pay
2) Webhook marks Authorized and triggers TryStart
3) RemoteStart accepted
4) User plugs in
5) StartTransaction arrives and attaches to reservation

### RemoteStart rejected
- Should set StartRejected and unwind payment + release lock

### Start timeout
- Authorized but no StartTransaction by deadline -> StartTimeout + release lock + unwind payment

### Out-of-order start
- StatusNotification(Charging) before StartTransaction -> still results in Charging state once StartTransaction arrives

### Out-of-order stop
- StatusNotification(Available/Finishing) before StopTransaction -> transaction still finalizes correctly once StopTransaction arrives

---

## 14) Practical default settings for production

- `Payments:StartWindowMinutes`: **7** (5–10)
- `Maintenance:ReservationTimeoutMinutes`: **10** (not 60)
- `Maintenance:StatusReleaseMinutes`: **60–90** (only if chargers report status reliably)
- Stripe Checkout Session `expires_at`: **30 minutes** (if you keep Checkout)

---

## 15) Notes for Stripe (keep in mind)
Your current docs say Cancel endpoint cancels the PaymentIntent and that webhook signature verification can be skipped. That must be revisited in Stripe implementation:
- Webhook signature verification should be mandatory in production.
- If you stick to Checkout, cancellation rules differ vs direct PaymentIntents.

(Stripe-specific hardening is covered in the separate Stripe spec.)

---

## Appendix A: Suggested “reason codes” for startability
Return these from `GetConnectorStartability`:
- `Offline`
- `OpenTransaction`
- `ActiveReservation`
- `StatusFaulted`
- `StatusUnavailable`
- `StatusCharging`
- `StatusReserved`
- `StatusFinishing`
- `StatusSuspended`
- `StatusUnknownStale`
- `Startable`

---

## Appendix B: Reference documents (URLs)
(For convenience — paste into a browser if needed.)

```text
OCPP 1.6 schemas (StatusNotification/StartTransaction/etc):
https://ocpp-spec.org/schemas/v1.6/

Open Charge Alliance - OCPP 1.6 Compliancy Test Tool Test Case Document (ordering + behaviors):
https://openchargealliance.org/

OCPP.Core schema example showing idTag length:
https://gitea.71dev.com/nutchayut/OCPP.Core/
```
