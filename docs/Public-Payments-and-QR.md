Public Payments & QR Flow
=========================

This outlines the public-facing flow that lets a driver scan a QR code, pay with Stripe, and remotely start a charging session.

Overview
--------
- Landing page: `OCPP.Core.Management` exposes an anonymous page at `/Public/Start?cp={chargePointId}&conn={connectorId}` (`PublicController`, `Views/Public/Start.cshtml`).
- Payment/session orchestration: the management UI calls the server API `Payments/*` endpoints (`OCPPMiddleware`) which delegate to `StripePaymentCoordinator`.
- Success/cancel: Stripe redirects to `/Payments/Success` or `/Payments/Cancel` in the management UI, which then confirms or cancels the reservation via the server API.
- Webhooks: Stripe posts to the server endpoint `/Payments/Webhook` to update reservation status and capture failures.

QR Code Deep Link
-----------------
- Encode the management UI URL for the target connector:
  - Format: `https://{management-host}/Public/Start?cp={ChargePointId}&conn={ConnectorId}`
  - `cp`: must match the `ChargePointId` in the database (table `ChargePoints`).
  - `conn`: 1-based connector id; defaults to `1` if omitted.
- Print the QR with that URL and place it near the connector label. Scanning opens the public start page with pricing and status information for that connector.

Public Start Page (anonymous)
-----------------------------
1) Driver scans QR → GET `/Public/Start`.
2) The page shows charge point name, connector label, last status, and pricing pulled from the database.
3) Driver enters an optional charge tag. If none is provided, the page generates a `WEB-{GUID}` tag.
4) POST `/Public/Start` calls the server API `Payments/Create` with:
   - `chargePointId`, `connectorId`, `chargeTagId`
   - `origin = "public"`
   - `returnBaseUrl = {current management base URL}` (used for Stripe return URLs)
5) Server response handling:
   - `status: Redirect` + `checkoutUrl` → browser is redirected to Stripe Checkout.
   - `status: Accepted` → free session or direct start; public result page shows success.
   - `status: ChargerOffline` or other → error message shown.

Server Payment Endpoints (`OCPP.Core.Server`)
---------------------------------------------
- `POST /Payments/Create` (`OCPPMiddleware.HandlePaymentCreateAsync`)
  - Validates charger online; if free charging, performs remote start immediately.
  - Otherwise calls `StripePaymentCoordinator.CreateCheckoutSession` to create a Stripe Checkout session and returns `{ status: "Redirect", checkoutUrl, reservationId, currency, maxAmountCents, maxEnergyKwh }`.
- `POST /Payments/Confirm` (`HandlePaymentConfirmAsync`)
  - Called by the management UI after Stripe redirect.
  - Confirms the Stripe session (`StripePaymentCoordinator.ConfirmReservation`), then triggers remote start against the charge point.
- `POST /Payments/Cancel` (`HandlePaymentCancelAsync`)
  - Cancels a pending reservation and Stripe PaymentIntent.
- `POST /Payments/Webhook` (`HandlePaymentWebhookAsync`)
  - Validates `Stripe-Signature` using `Stripe:WebhookSecret`.
  - Handles `checkout.session.completed` (marks reservation authorized) and `payment_intent.payment_failed`.

Stripe Checkout Redirects (management UI)
-----------------------------------------
- Success: `/Payments/Success?reservationId={id}&session_id={CHECKOUT_SESSION_ID}&origin=public`
  - `PaymentsController.Success` posts `Payments/Confirm` to the server; on acceptance it shows `Views/Payments/PublicResult.cshtml`.
- Cancel: `/Payments/Cancel?reservationId={id}&origin=public`
  - `PaymentsController.Cancel` posts `Payments/Cancel` to the server and shows the public result view.

Configuration Checklist
-----------------------
Management (`OCPP.Core.Management` `appsettings.*` or environment):
- `ServerApiUrl`: base URL of `OCPP.Core.Server` (e.g., `https://server-host:8081/`).
- `ApiKey`: API key sent as `X-API-Key` if the server enforces it.

Server (`OCPP.Core.Server` `appsettings.*` or environment, `StripeOptions`):
- `Stripe:Enabled`: `true`
- `Stripe:ApiKey`: secret key
- `Stripe:ReturnBaseUrl`: public base of management UI (e.g., `https://mgmt.example.com`)
- `Stripe:WebhookSecret`: webhook signing secret
- Optional: `Stripe:Currency`, `Stripe:ProductName`

Stripe Webhook
--------------
- Point Stripe to `https://{server-host}/Payments/Webhook`.
- Configure the signing secret as `Stripe:WebhookSecret`.
- Events used: `checkout.session.completed`, `payment_intent.payment_failed`.

End-to-End Flow Recap
---------------------
1) Driver scans QR → `/Public/Start?cp=...&conn=...`.
2) Management UI posts `Payments/Create` to the server.
3) Server creates Stripe Checkout session (unless free charging) and returns a redirect URL.
4) Driver pays on Stripe; Stripe redirects to `/Payments/Success` (or `/Payments/Cancel`).
5) Management UI calls `Payments/Confirm`; server verifies Stripe, then remote-starts the connector.
6) Server listens for OCPP response; if accepted, reservation moves to `Authorized/Charging`. On completion, server captures the Stripe payment.
