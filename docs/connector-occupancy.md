# Connector occupancy checks

This note maps every place the backend decides a connector is “busy” and what data it relies on. Use it when the public start flow shows “This connector is currently in use…” unexpectedly or fails to block an already-occupied connector.

## Signals that drive the check
- **Live OCPP status:** `_chargePointStatusDict[cpId].OnlineConnectors[connectorId].Status` (`Available`, `Occupied`, `Unavailable`, `Faulted`). Set by `StatusNotification` handlers when the charger is online.
- **Persisted status fallback:** `ConnectorStatuses.LastStatus` row for the connector (same set of status strings). Used only when no live connector entry exists and the row is fresh (default freshness window: 30 minutes).
- **Open transaction:** `Transactions` row with `StopTime IS NULL` for the connector.
- **Active reservation:** `ChargePaymentReservations` row for the connector whose `Status` is _not_ `Completed`, `Cancelled`, or `Failed` (includes `Pending`, `Authorized`, `StartRequested`, etc.). A unique index (`UX_PaymentReservations_ActiveConnector`) also prevents inserting overlapping active reservations.
- **WebSocket presence:** The charge point must have an open WebSocket in `_chargePointStatusDict`; otherwise the API returns `ChargerOffline` before any busy evaluation.

## Where busy is enforced
- **Payments/Create** (`OCPPMiddleware.HandlePaymentCreateAsync`):
  - Blocks with `ChargerOffline` if the WebSocket is absent/closed.
  - Calls `IsConnectorBusy`, which returns busy when:
    - Live status exists and is not `Available`, **or**
    - An open transaction exists (`StopTime IS NULL`), **or**
    - An active reservation exists (excluding the current one when provided), **or**
    - No live status is available and the persisted `LastStatus` is non-`Available` and fresh (<= 30 minutes old).
  - Stripe reservation insert can also throw `InvalidOperationException("ConnectorBusy")` on the unique index; that is caught and returned as `ConnectorBusy`.
- **Payments/Confirm** (after payment succeeds):
  - Repeats WebSocket check and `IsConnectorBusy` (ignoring the current reservation id).
- **Remote start side effects:**
  - When a free session is accepted or a paid reservation is confirmed, `SetConnectorStatus` persists `ConnectorStatuses.LastStatus = "Occupied"`.
  - If the charger never sends a later `StatusNotification` with `Available`, the persisted status stays occupied.

## Quick decision matrix
- WebSocket closed or missing → `ChargerOffline`.
- WebSocket open + live status `Available` + no open transactions + no active reservations → allowed to proceed.
- Live status `Occupied`/`Unavailable`/`Faulted` → `ConnectorBusy`.
- No live status, but persisted `LastStatus` not `Available` and updated within the freshness window → `ConnectorBusy`.
- Any open transaction (`StopTime IS NULL`) → `ConnectorBusy`.
- Any active reservation (pending/authorized/start requested/charging) on the same connector → `ConnectorBusy`.
- Unique-index conflict when inserting a reservation → `ConnectorBusy`.

## Common false positives and negatives
- **Stale persisted status:** Previously could block; now ignored if older than 30 minutes when no live status is available. Still investigate why no fresh status arrived.
- **Unclosed transaction:** `StopTransaction` never received (offline charger, watchdog missing), leaving `StopTime = NULL`. Every start attempt is blocked until the transaction is closed manually.
- **Lingering reservation:** Payment canceled or failed on the client, but reservation row is still `Pending`/`Authorized`; unique index and `IsConnectorBusy` treat it as busy.
- **Missing live status:** Charger is online but never sent status for that connector after boot, so live dictionary is empty; decision falls back to persisted status (if fresh) and open tx/reservations.
- **Race after remote start:** `SetConnectorStatus(..., "Occupied")` is written when remote start is accepted; if the charger rejects shortly after and does not push `Available`, the persisted row stays occupied.

## What to inspect when debugging a connector
Perform these against the target `ChargePointId`/`ConnectorId` to explain a `ConnectorBusy` response:
- `ConnectorStatuses` table: current `LastStatus` and timestamp for the connector.
- `Transactions`: any rows with `StopTime IS NULL`.
- `ChargePaymentReservations`: rows whose `Status` is not `Completed`/`Cancelled`/`Failed`.
- Server logs around the attempt (`Payments/Create` and `Payments/Confirm` entries); look for `ConnectorBusy` warnings or Stripe reservation conflicts.
- Live status in the Management UI Connector list (shows `_chargePointStatusDict` content) versus DB status; differences imply stale persisted data.

## Suggested tightening ideas
- Clear or down-rank persisted status if the charge point is online but has no live connector entry yet.
- Add logging around each predicate inside `IsConnectorBusy` to show which condition triggered busy (now logged in middleware).
- Periodically expire reservations that are stuck in `Pending`/`Authorized` beyond a timeout.
- Provide an admin action to close orphaned transactions or reset connector status when a charger is confirmed idle.

## Startup maintenance (implemented)
- On server startup, stale reservations in `PendingPayment`/`Authorized`/`StartRequested` older than `Maintenance:ReservationTimeoutMinutes` (default 60) are auto-cancelled.
- Persisted connector statuses that are non-`Available` and older than `Maintenance:StatusReleaseMinutes` (default 240) are flipped to `Available` **only** if there is no open transaction and no active reservation for that connector.
- Both thresholds are configurable via configuration (appsettings / env). Set either to 0 or negative to disable the respective cleanup.
