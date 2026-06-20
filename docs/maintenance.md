# Maintenance

## How to Make Changes Safely

1. Identify the affected surface:
   - OCPP protocol handling
   - server API
   - management UI
   - public charging/payment UI
   - database schema
   - payments/invoices/emails
   - background jobs
   - extensions
   - simulators/tests
2. Read the closest existing code and tests before editing.
3. Keep public repository changes free of private/client/deployment context.
4. Update documentation in the same task when behavior, config, schema, commands, integrations, jobs, deployment, or architecture changes.
5. Add or update an ADR in `docs/decisions/` for durable architecture decisions.

## Validation Matrix

Default documentation-only validation:

```sh
git diff --check
```

Default code validation:

```sh
bash ./scripts/check-mssql-migration-metadata.sh
dotnet restore OCPP.Core.sln
dotnet build OCPP.Core.sln
dotnet test OCPP.Core.Server.Tests/OCPP.Core.Server.Tests.csproj --no-build
```

For payment/public portal changes:

```sh
npm ci --prefix Simulators/playwright
npx --prefix Simulators/playwright playwright install chromium
node Simulators/run_e2e_stack.mjs
```

For broad regression changes after building:

```sh
python3 Simulators/run_local_regression.py
```

For database schema changes:

```sh
make add-migration NAME=DescriptiveName
make check-migration-metadata
dotnet test OCPP.Core.Server.Tests/OCPP.Core.Server.Tests.csproj
```

Also inspect the generated migration and model snapshot manually for provider-specific type drift.

## Common Failure Points

- `OCPPMiddleware.cs` has several responsibilities in one class: route parsing, WebSocket state, server API, payment handlers, and helper logic. Small changes can have wide effects.
- Live charge point state is in memory, so multi-instance behavior is not implied by the code.
- Server API authentication depends on matching `ApiKey` values between server and management app.
- SQL Server and SQLite paths differ: migrations for SQL Server, `EnsureCreated()` for SQLite.
- Hangfire jobs only start with SQL Server configured.
- Payment reservation state touches connector locks, remote start/stop, Stripe, cleanup services, status UI, invoice logs, and emails.
- Timeouts and idle-fee behavior depend on UTC timestamps, configured local time zones, and sweep intervals.
- OCPP 1.6, 2.0.1, and 2.1 can need protocol-specific behavior even when a feature name is shared.
- Public portal changes may require Razor, CSS, JavaScript, server API, and Playwright updates.

## Recurring Maintenance Tasks

- Keep migrations and `OCPPCoreContextModelSnapshot.cs` aligned with entity/model changes.
- Run `scripts/check-mssql-migration-metadata.sh` after migration scaffolding.
- Keep public docs current with config keys and validation commands.
- Review checked runtime artifacts under `SQLite/` and `Simulators/playwright/` before adding new generated files.
- Keep simulator scenarios in sync with payment and protocol behavior.
- Review package versions periodically for .NET, Hangfire, EF Core, Stripe, Sentry, Azure Identity, ClosedXML, and Playwright.

## Known Technical Debt / Risk

- No dedicated formatter/linter configuration was found.
- No `global.json` pins the .NET SDK.
- CI has overlapping unit-test workflow files.
- Some generated/runtime artifacts appear tracked, including SQLite files and Playwright report/test-result files.
- The middleware is large and would benefit from careful extraction only with strong tests.
- Public appsettings contain development/sample values that look secret-like; do not treat them as deployable secrets.
- Charger-side reservation profile behavior is disabled in code while related option naming remains.
- SQL scripts and EF migrations may represent overlapping database history; authoritative use is Unknown / verify.

## Recommended Future Cleanup

- Add a sanitized `appsettings.example.json` pattern and move secret-like development values out of public examples.
- Decide whether checked SQLite and Playwright output files are fixtures or generated artifacts.
- Consolidate duplicate/overlapping CI workflows if they do not serve distinct purposes.
- Consider pinning the .NET SDK with `global.json` after maintainers agree on the supported SDK.
- Add docs or tests for extension packaging and runtime deployment if extensions are actively supported.
- Break down `OCPPMiddleware.cs` only when a change already requires touching related behavior and tests can protect the split.
