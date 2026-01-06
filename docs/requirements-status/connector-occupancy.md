# Connector Occupancy – Implementation Check

Source: `docs/connector-occupancy.md`

Implemented
- Busy evaluation (`IsConnectorBusy`) checks live connector status, open transactions, active reservations, and persisted status fallback; see `OCPP.Core.Server/OCPPMiddleware.cs:1067-1139`.
- Payments create/confirm endpoints call `IsConnectorBusy` and return `ConnectorBusy` when any predicate hits; see `OCPP.Core.Server/OCPPMiddleware.cs:848-950` and `OCPP.Core.Server/OCPPMiddleware.cs:952-1033`.
- Persisted status is considered stale after 30 minutes when no live status exists; see `OCPP.Core.Server/OCPPMiddleware.cs:1054-1136` (uses `PersistedStatusStaleAfter = 30 minutes`).
- Active reservation uniqueness enforced by DB unique index `UX_PaymentReservations_ActiveConnector`; see `OCPP.Core.Database/OCPPCoreContext.cs:270-294`.
- Startup maintenance cancels stale reservations and releases old non-available statuses unless an open tx/reservation exists; see `OCPP.Core.Server/StartupMaintenance.cs:18-118` and run from `OCPP.Core.Server/Startup.cs:90-104`.
- Remote-start side effect persists `Occupied` and availability reset on transaction completion; see `OCPP.Core.Server/OCPPMiddleware.cs:913`, `OCPP.Core.Server/OCPPMiddleware.cs:1017`, and `OCPP.Core.Server/OCPPMiddleware.cs:1271-1294`.

Notes / gaps
- No structured log of which predicate triggered busy beyond the warning text; enhancing per-predicate logging could aid triage.
- Persisted status freshness window (30 minutes) is hard-coded; configuration toggle not exposed.
