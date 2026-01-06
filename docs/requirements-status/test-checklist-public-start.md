# Test Checklist – Public Start with Simulator (OCPP 1.6) – Implementation Check

Source: `docs/test-checklist.md`

Implemented elements
- Simulator endpoints and management/public URLs match current controllers and defaults (`ws://localhost:8081/OCPP/<id>`, `http://localhost:8082/Public/Start?cp=...&conn=1`); see `OCPP.Core.Management/Controllers/PublicController.cs:30-110` and `OCPP.Core.Server/appsettings.json:58-72`.
- Public flow auto-generates `WEB-{GUID}` tags when empty and reuses provided tags; see `PublicController.Start` POST at `OCPP.Core.Management/Controllers/PublicController.cs:37-110`.
- Payments create/confirm/cancel endpoints exist and are invoked by the public UI; see `OCPP.Core.Server/OCPPMiddleware.cs:812-1065` and `OCPP.Core.Management/Controllers/PaymentsController.cs:34-94`.
- Busy/offline guards return `ConnectorBusy`/`ChargerOffline` that the public page surfaces as user-friendly errors; see `PublicController.Start` handling at `OCPP.Core.Management/Controllers/PublicController.cs:69-110` and server checks at `OCPP.Core.Server/OCPPMiddleware.cs:848-950`.
- DB inspection queries in the checklist align with tables `Transactions`, `ChargePaymentReservation`, and `ConnectorStatus` as used by the occupancy logic.

Notes / gaps
- Checklist references a patched simulator file `Simulators/simple simulator1.6_mod.html` which is present, but the repo does not automate its patching or verification.
- No automated tests; checklist remains manual.
