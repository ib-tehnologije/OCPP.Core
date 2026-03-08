# Payments & Reservations – How It Works

This repo uses Stripe Checkout (manual capture) to reserve funds before remote-starting a connector. A reservation row also marks the connector busy so concurrent starts are blocked. This doc explains the lifecycle, recovery flow, and the auto-release safeguards.

## End‑to‑end flow
1) **Create (POST /API/Payments/Create)**  
   - Validates charger online and connector not busy.  
   - Calculates max amount (energy cap + usage + session fee).  
   - Creates Stripe Checkout Session (PI requires_capture) + `ChargePaymentReservation` with `Status=Pending`.  
   - Returns `checkoutUrl`, `reservationId`, and a busy `reason` when the connector is blocked (`ActiveReservation`, `OpenTransaction`, `Offline`, live/persisted status).  
   - Public flow stores a short-lived signed recovery cookie before redirecting the same browser to Stripe.
2) **Confirm (POST /API/Payments/Confirm)** after Stripe redirect  
   - Fetches session + PaymentIntent; requires status `complete` and PI `requires_capture`/`succeeded`.  
   - Sets reservation `Status=Authorized`, records PI id/amount.  
   - Re‑checks connector busy, then sends RemoteStart; sets persisted connector status to `Occupied` and reservation to `StartRequested` if accepted.  
   - Public flow now redirects to **/Payments/Status** so the driver can see live state.
3) **Resume / Recover (POST /API/Payments/Resume)**  
   - `Pending` + open Stripe Checkout session => returns `status=Redirect` with the saved `checkoutUrl`.  
   - `Authorized`, `StartRequested`, `Charging`, `Completed` => returns `status=Status` so the UI can send the driver straight to `/Payments/Status`.  
   - Terminal / missing-session cases return a non-redirect terminal status so the UI can offer cancel-and-retry instead of silently keeping the connector locked.
4) **Charging**  
   - On `StartTransaction`, reservation moves to `Charging` and links the transaction id.  
5) **Stop / Capture**  
   - On `StopTransaction`, server computes actual cost and either captures (PI `requires_capture`) or marks completed (`succeeded`).  
   - If zero capture, cancels PI and marks reservation `Cancelled`.
6) **Webhook assists** (`/API/Payments/Webhook`)  
   - `checkout.session.completed`: backfills PI id and marks `Authorized` if still `Pending`.  
   - `checkout.session.expired`: sets `Cancelled` with `FailureCode=CheckoutExpired` so it is clearly terminal and non-locking.  
   - `payment_intent.payment_failed`: sets `Failed`.

## Busy rules (why a connector shows “occupied”)
- Live status != Available (when charger WebSocket is connected).  
- Any open transaction (`StopTime IS NULL`).  
- Any active reservation on that connector where `LocksConnector(status)=true`.  
- If no live status: persisted `ConnectorStatuses.LastStatus` is used if fresh (≤ 30 min).  
Locking statuses are `Pending`, `Authorized`, `StartRequested`, and `Charging`.

Non-locking statuses are `Completed`, `Cancelled`, `Failed`, `StartRejected`, `StartTimeout`, and `Abandoned`.

These checks run on `/Payments/Create`, `/Payments/Confirm`, `/Payments/Resume`, `/Payments/Status`, startup repair, and the public occupancy view. A unique index also prevents overlapping active reservations.

## Auto‑release safety nets (Maintenance)
- `PendingPaymentTimeoutMinutes` (default 3): background sweep + startup maintenance cancel stale `Pending` checkouts quickly so abandoned pre-payment sessions stop blocking the connector.  
- `ReservationTimeoutMinutes` (default 60): legacy fallback for older configs; `PendingPaymentTimeoutMinutes` wins when set.  
- `StatusReleaseMinutes` (default 240): on startup, if a persisted connector status is non‑Available and older than this, and there is no open transaction/reservation, it is flipped to `Available`.  
Tune via env vars `Maintenance__PendingPaymentTimeoutMinutes`, `Maintenance__ReservationTimeoutMinutes`, `Maintenance__StatusReleaseMinutes`, `Maintenance__CleanupIntervalSeconds`.

Recommended public settings: pending checkout timeout 3 minutes; status release 60–90 minutes if chargers report status reliably.

## Customer‑facing status page
- After a successful confirm in the public flow, the UI redirects to `/Payments/Status?reservationId=...` which polls `/API/Payments/Status` and shows payment state, charger status, connector, transaction id, and amounts.
- `/API/Payments/Status` now also reports `locksConnector`, `blockingReason`, and `otherActiveReservation` so the UI and diagnostics can distinguish an active checkout lock from a real occupied connector.

## Same-browser checkout recovery
- Public `/cp/{chargePointId}/{connectorId?}` reads the signed recovery cookie created during `Payments/Create`.
- If the same browser comes back to the same connector while the reservation is still `Pending`, the page shows:
  - `Resume payment`
  - `Cancel previous attempt`
  - normal connector chooser / map navigation
- If the reservation has already advanced to `Authorized`, `StartRequested`, `Charging`, or `Completed`, the driver is redirected to `/Payments/Status`.
- If the reservation is terminal or Stripe can no longer resume the session, the cookie is cleared and the driver can start cleanly again.

## Email notification (optional)
- If `Notifications__EnableCustomerEmails=true` and SMTP is configured, an email is sent on successful Stripe confirmation to the customer email returned by Checkout. Configuration keys live under `Notifications__*` (host, port, username, password, from name/address, optional reply‑to/bcc).

## Common stuck scenarios and fixes
- **Abandoned Stripe checkout** -> reservation stays `Pending` and blocks the connector until timeout/cancel. Mitigation: 3-minute `PendingPaymentTimeoutMinutes`, same-browser recovery, manual `POST /API/Payments/Cancel`.  
- **Checkout session exists but Stripe can no longer resume it** -> `/API/Payments/Resume` returns `ResumeUnavailable` or `MissingCheckoutSession`. Mitigation: cancel the old attempt and create a new checkout.  
- **Charger never reports Available after start attempt** -> persisted status stays `Occupied`. Mitigation: ensure charger status notifications; `StatusReleaseMinutes` fallback; admin reset via DB or a maintenance action.  
- **StopTransaction missing** -> open transaction blocks starts. Mitigation: send StopTransaction or close transaction manually.

## Key code touchpoints
- API flow & busy check: `OCPP.Core.Server/OCPPMiddleware.cs`  
- Stripe logic: `OCPP.Core.Server/Payments/StripePaymentCoordinator.cs`  
- Cleanup: `OCPP.Core.Server/Payments/PaymentReservationCleanupService.cs`, `StartupMaintenance.cs`  
- Status endpoint: `/API/Payments/Status` in `OCPPMiddleware`  
- Public status page: `OCPP.Core.Management/Views/Payments/PublicStatus.cshtml` and `PaymentsController.Status`
