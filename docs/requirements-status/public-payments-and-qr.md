# Public Payments & QR – Implementation Check

Source: `docs/Public-Payments-and-QR.md`

Implemented
- Public start page (`/Public/Start?cp={id}&conn={connectorId}`) is anonymous, generates a `WEB-…` tag when none is provided, and posts to `Payments/Create`; see `OCPP.Core.Management/Controllers/PublicController.cs:30`.
- Success/cancel redirects post back to `Payments/Confirm` and `Payments/Cancel`, selecting the public result view via the `origin` parameter; see `OCPP.Core.Management/Controllers/PaymentsController.cs:34` and `OCPP.Core.Management/Controllers/PaymentsController.cs:79`.
- Server endpoints `Payments/Create|Confirm|Cancel|Webhook` perform online checks, call `IsConnectorBusy`, handle free sessions, and execute remote starts; see `OCPP.Core.Server/OCPPMiddleware.cs:812`, `OCPP.Core.Server/OCPPMiddleware.cs:848`, `OCPP.Core.Server/OCPPMiddleware.cs:952`, `OCPP.Core.Server/OCPPMiddleware.cs:1034`, and `OCPP.Core.Server/OCPPMiddleware.cs:1167`.
- Stripe checkout session URLs are built from `Stripe:ReturnBaseUrl` (plus optional `origin`) and embed reservation metadata; see `OCPP.Core.Server/Payments/StripePaymentCoordinator.cs:58` and `OCPP.Core.Server/Payments/StripePaymentCoordinator.cs:132`.
- Stripe webhooks validate the signature and process `checkout.session.completed` and `payment_intent.payment_failed`; see `OCPP.Core.Server/Payments/StripePaymentCoordinator.cs:488`.
- Connector status is persisted as `Occupied` when remote start is accepted after payment or free-session shortcut; see `OCPP.Core.Server/OCPPMiddleware.cs:913` and `OCPP.Core.Server/OCPPMiddleware.cs:1017`.
- Documented config keys exist (management `ServerApiUrl`/`ApiKey`, server `Stripe:*`/`ApiKey`/`ReturnBaseUrl`); see `OCPP.Core.Management/appsettings.json:37` and `OCPP.Core.Server/appsettings.json:45`.

Notes / gaps
- QR code generation is still manual; the repo does not ship a helper to encode the URL.
- No automated tests cover this flow; manual steps live in `docs/test-checklist.md`.
- If a path-style QR such as `cp/ACE0748001?connectorId=1` is expected (per `docs/razlike.md`), it is not currently routed—controllers expect `cp` and `conn` query parameters.
