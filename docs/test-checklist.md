# Test Checklist – Public Start with Simulator (OCPP 1.6)

Use this to reproduce and diagnose “ChargerOffline/ConnectorBusy” loops when starting a session via the public page.

## Prerequisites
- OCPP server running on `http://localhost:8081` (same instance for API and WebSocket).
- Management/public UI running on `http://localhost:8082`.
- Target connector clean: no open transactions, no active reservations, status = Available.
- Patched simulator `Simulators/simple simulator1.6_mod.html` (connectorId = 1, configurable Status input).

## Known-good flow
1) **Open simulator (keep tab open)**  
   - File: `Simulators/simple simulator1.6_mod.html`  
   - Central Station: `ws://localhost:8081/OCPP/Test1234`  
   - Tag: a tag in `ChargeTags` (e.g., `B4A63CDF`) or the tag you will use in the public flow.  
   - Click **Connect**. Verify server logs show the connection/heartbeat.

2) **Public start (new tab)**  
   - URL: `http://localhost:8082/Public/Start?cp=Test1234&conn=1`  
   - Leave tag blank (auto-generates `WEB-…`), **or** prefill with the same tag you set in the simulator (e.g., `B4A63CDF`) to avoid tag mismatch during Start/Stop.  
   - Click **Start and pay**. Expected: redirect to Stripe (no “offline”).  
   - Complete Stripe test payment.

3) **After payment (back to simulator)**  
   - Set the Tag field to the same tag the reservation used (the `WEB-…` shown on the public page) if it differs.  
   - Click **Start Transaction** (should return a TransactionId).  
   - Optional: **Send Meter Values**.  
   - Click **Stop Transaction**.  
   - Click **Status Notification** with Status = Available.

4) **Verify DB**  
   ```sql
   -- No open transaction
   select TransactionId, StopTime
   from Transactions
   where ChargePointId='Test1234' and ConnectorId=1
   order by TransactionId desc;

   -- No active reservation
   select ReservationId, Status
   from ChargePaymentReservation
   where ChargePointId='Test1234' and ConnectorId=1
     and Status not in ('Completed','Cancelled','Failed');

   -- Connector available
   select LastStatus, LastStatusTime
   from ConnectorStatus
   where ChargePointId='Test1234' and ConnectorId=1;
   ```

## If “ChargerOffline” on Start and pay
- Ensure the simulator tab is still connected and hitting the same server instance.
- Check server log for `Payments/Create` → `ChargerOffline`. If it fires, `_chargePointStatusDict` lacks a socket; add a temporary log of its keys before that branch.
- No load balancer/second instance: WebSocket state is in-memory only.
- Confirm `ServerApiUrl` in the management app points to the same OCPP server instance/port you connected the simulator to, and that you see a BootNotification from the simulator immediately before clicking “Start and pay.”

## If StartTransaction shows “JSON is not accepted”
- With `ValidateMessages=true`, small schema mismatches can fail. Temporarily set `"ValidateMessages": false` in `appsettings.Development.json` and retry.
- Make sure the tag in the simulator exists in `ChargeTags` (public flow auto-creates the `WEB-…` tag; use that same tag for Start/Stop).

## Tag rules
- Public flow auto-creates `WEB-…` and stores it in `ChargeTags`; charger must echo it in Start/Stop.
- Simulator must use a tag present in `ChargeTags`; ideally set it to the session’s `WEB-…` tag.

## Quick reset (MSSQL)
```sql
UPDATE Transactions
SET StopTime = ISNULL(StopTime, SYSUTCDATETIME())
WHERE ChargePointId='Test1234' AND ConnectorId=1 AND StopTime IS NULL;

UPDATE ConnectorStatus
SET LastStatus='Available', LastStatusTime=SYSUTCDATETIME()
WHERE ChargePointId='Test1234' AND ConnectorId=1;

UPDATE ChargePaymentReservation
SET Status='Cancelled', LastError='Manual reset', UpdatedAtUtc=SYSUTCDATETIME()
WHERE ChargePointId='Test1234' AND ConnectorId=1
  AND Status NOT IN ('Completed','Cancelled','Failed');
```

## Logs to watch
- WebSocket accept / BootNotification for `Test1234`.
- `Payments/Create`: whether it logs `ChargerOffline` or `ConnectorBusy` (and reason).
- StartTransaction CALLERROR details if “JSON is not accepted.”
