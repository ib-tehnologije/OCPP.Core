# Payments & Reservations – How It Works

This repo uses Stripe Checkout (manual capture) to reserve funds before remote‐starting a connector. A reservation row also marks the connector “busy” so concurrent starts are blocked. This doc explains the lifecycle and the auto‑release safeguards.

## End‑to‑end flow
1) **Create (POST /API/Payments/Create)**  
   - Validates charger online and connector not busy.  
   - Calculates max amount (energy cap + usage + session fee).  
   - Creates Stripe Checkout Session (PI requires_capture) + `ChargePaymentReservation` with `Status=Pending`.  
   - Redirects driver to Stripe.
2) **Confirm (POST /API/Payments/Confirm)** after Stripe redirect  
   - Fetches session + PaymentIntent; requires status `complete` and PI `requires_capture`/`succeeded`.  
   - Sets reservation `Status=Authorized`, records PI id/amount.  
   - Re‑checks connector busy, then sends RemoteStart; sets persisted connector status to `Occupied` and reservation to `StartRequested` if accepted.  
   - Public flow now redirects to **/Payments/Status** so the driver can see live state.
3) **Charging**  
   - On `StartTransaction`, reservation moves to `Charging` and links the transaction id.  
4) **Stop / Capture**  
   - On `StopTransaction`, server computes actual cost and either captures (PI `requires_capture`) or marks completed (`succeeded`).  
   - If zero capture, cancels PI and marks reservation `Cancelled`.
5) **Webhook assists** (`/Payments/Webhook`)  
   - `checkout.session.completed`: backfills PI id and marks `Authorized` if still `Pending`.  
   - `checkout.session.expired`: sets `Cancelled`.  
   - `payment_intent.payment_failed`: sets `Failed`.

## Busy rules (why a connector shows “occupied”)
- Live status != Available (when charger WebSocket is connected).  
- Any open transaction (`StopTime IS NULL`).  
- Any active reservation on that connector (statuses except Completed/Cancelled/Failed).  
- If no live status: persisted `ConnectorStatuses.LastStatus` is used if fresh (≤ 30 min).  
These checks run on `/Payments/Create` and `/Payments/Confirm`; a unique index also prevents overlapping active reservations.

## Auto‑release safety nets (Maintenance)
- `ReservationTimeoutMinutes` (default 60): background sweep + startup maintenance cancel stale reservations in `Pending/Authorized/StartRequested` older than this. Cancelling frees the busy flag.  
- `StatusReleaseMinutes` (default 240): on startup, if a persisted connector status is non‑Available and older than this, and there is no open transaction/reservation, it is flipped to `Available`.  
Tune via env vars `Maintenance__ReservationTimeoutMinutes`, `Maintenance__StatusReleaseMinutes`, `Maintenance__CleanupIntervalSeconds`.

Recommended public settings: reservation timeout 5–10 minutes; status release 60–90 minutes if chargers report status reliably.

## Customer‑facing status page
- After a successful confirm in the public flow, the UI redirects to `/Payments/Status?reservationId=...` which polls `/API/Payments/Status` and shows payment state, charger status, connector, transaction id, and amounts.

## Email notification (optional)
- If `Notifications__EnableCustomerEmails=true` and SMTP is configured, an email is sent on successful Stripe confirmation to the customer email returned by Checkout. Configuration keys live under `Notifications__*` (host, port, username, password, from name/address, optional reply‑to/bcc).

## Common stuck scenarios and fixes
- **Abandoned Stripe checkout** → reservation stays `Pending` and blocks the connector until timeout/cancel. Mitigation: short `ReservationTimeoutMinutes`; manual `POST /API/Payments/Cancel`.  
+- **Charger never reports Available after start attempt** → persisted status stays `Occupied`. Mitigation: ensure charger status notifications; `StatusReleaseMinutes` fallback; admin reset via DB or a maintenance action.  
+- **StopTransaction missing** → open transaction blocks starts. Mitigation: send StopTransaction or close transaction manually.

## Key code touchpoints
- API flow & busy check: `OCPP.Core.Server/OCPPMiddleware.cs`  
- Stripe logic: `OCPP.Core.Server/Payments/StripePaymentCoordinator.cs`  
- Cleanup: `OCPP.Core.Server/Payments/PaymentReservationCleanupService.cs`, `StartupMaintenance.cs`  
- Status endpoint: `/API/Payments/Status` in `OCPPMiddleware`  
- Public status page: `OCPP.Core.Management/Views/Payments/PublicStatus.cshtml` and `PaymentsController.Status`
