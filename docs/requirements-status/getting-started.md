# GettingStarted.md – Implementation Check

Source: `docs/GettingStarted.md`

Implemented
- Server and management projects run on defaults `http://localhost:8081` and `http://localhost:8082`; reflected in `OCPP.Core.Server/appsettings.json:58-72` and `OCPP.Core.Management/appsettings.json:54-67`.
- Connection strings for SQLite/SQL Server exist in both apps; see `OCPP.Core.Server/appsettings.json:30-34` and `OCPP.Core.Management/appsettings.json:32-35`.
- Management `ServerApiUrl` and `ApiKey` config are present; see `OCPP.Core.Management/appsettings.json:37-39`.
- Simulators referenced (`simple simulator1.6_mod.html`, `simple simulator1.6_multi_connector.html`, `cp20_mod.html`) are present under `Simulators/`.
- Optional logging/pricing/Stripe settings exist in server config (`MessageDumpDir`, `DbMessageLog`, `Stripe:*`); see `OCPP.Core.Server/appsettings.json:35-55`.

Notes / gaps
- Guide suggests running `dotnet` commands; AGENTS instructions say not to run them in this environment (manual step remains for users).
- No automated validation of config prerequisites (DB reachability, Stripe keys); users must supply values.
