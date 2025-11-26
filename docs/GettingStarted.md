# OCPP.Core Quickstart

This guide walks through running the server and management UI locally, testing with the included simulators, and onboarding real chargers.

## Components
- **OCPP.Core.Server**: WebSocket/OCPP endpoint and REST API (`ws://localhost:8081/OCPP/<id>` by default).
- **OCPP.Core.Management**: Admin UI for chargers, connectors, and RFID tags (`http://localhost:8082` by default).
- **OCPP.Core.Database**: Entity Framework Core models and migrations (SQLite or SQL Server).

## Prerequisites
- .NET 8 SDK installed.
- SQLite (bundled db at `SQLite/OCPP.Core.sqlite`) or a reachable SQL Server instance.
- Optional: Stripe test keys if you want to try Stripe holds/capture.

## Configure
1) Server connection string: edit `OCPP.Core.Server/appsettings.Development.json` → `ConnectionStrings` (`SQLite` or `SqlServer`).
2) Management connection string + server API URL: `OCPP.Core.Management/appsettings.Development.json` → `ConnectionStrings` and `ServerApiUrl`. If you set `ApiKey` here, set the same key in the server file.
3) Users: adjust admin credentials in `OCPP.Core.Management/appsettings.Development.json` (`Users` array).
4) Optional message logging: in server settings set `MessageDumpDir` (filesystem path) and `DbMessageLog` (0=None, 1=Info, 2=Verbose).
5) Optional Stripe: in `OCPP.Core.Server/appsettings.Development.json` fill `Stripe` (API key, webhook secret, currency, return URL to management UI). The server will reserve an amount before sending remote starts and capture on stop.

## Run locally
```bash
dotnet restore OCPP.Core.sln
dotnet run --project OCPP.Core.Server      # starts WebSocket endpoint on 8081
dotnet run --project OCPP.Core.Management  # starts admin UI on 8082
```

## Create a test charger + tag
1) Open `http://localhost:8082`, sign in with the configured admin user.
2) Administration → Chargepoints → New; set an ID (example `station42`). Optionally add basic auth or a client-cert thumbprint.
3) Administration → Charge tags → New; add a token ID for testing.

## Simulate chargers (no hardware)
Simulators live in `Simulators/` and can be opened directly in a browser:
- `simple simulator1.6_mod.html`: OCPP 1.6 single connector.
- `simple simulator1.6_multi_connector.html`: OCPP 1.6 multi-connector.
- `cp20_mod.html`: OCPP 2.0/2.1.

Use Central System URL `ws://localhost:8081/OCPP/<chargepoint-id>` (e.g., `ws://localhost:8081/OCPP/station42`). Enter the tag you created, connect, then start/stop transactions. The management UI tiles should reflect status; connectors are auto-discovered and can be renamed later.

## Using real chargers
1) Deploy/host `OCPP.Core.Server` with TLS: configure Kestrel URLs/cert in `OCPP.Core.Server/appsettings*.json` and use `wss://<host>/OCPP/<chargepoint-id>`.
2) In the management UI, create a chargepoint with the exact ID you will configure on the device. Set credentials to match what the device will send (basic auth or client certificate thumbprint).
3) On the charger, point its Central System URL to the server endpoint and set credentials accordingly.
4) Create charge tags in the UI. After the first status/transaction messages, connectors appear automatically; rename them if desired.

## Troubleshooting
- Enable `MessageDumpDir` and `DbMessageLog` on the server to capture traffic.
- Verify firewall/port exposure for `8081`/`8091` (or your configured ports).
- Make sure the management UI `ApiKey` matches the server when set; mismatches block status calls.
- For Stripe tests, use Stripe test keys and ensure the return URL points to the running management UI.

## Helpful references
- `README.md`: features and protocol coverage.
- `Installation.md`: database setup and hosting options.
- `Server-API.md`: server REST endpoints.
