# Contributing

OCPP.Core is a public .NET 8 OCPP central-system repository. Keep contributions public-safe and grounded in the code that is present in this repository.

## Local Setup

Prerequisites:

- .NET SDK capable of building `net8.0` projects. CI uses .NET `8.0.x`.
- SQL Server for migration-backed runs, or SQLite for local/test runs.
- Node.js and npm for simulator and Playwright E2E work. CI uses Node `20`.
- Docker only when building container images locally.

Restore and build:

```sh
dotnet restore OCPP.Core.sln
dotnet build OCPP.Core.sln
```

Local SQLite run:

```sh
export ConnectionStrings__SqlServer=
export ConnectionStrings__SQLite='Filename=./SQLite/OCPP.Core.test.sqlite;foreign keys=True'
export ApiKey='replace-with-local-api-key'
dotnet run --project OCPP.Core.Server
```

Run the management app in another terminal with the same database and API key:

```sh
export ConnectionStrings__SqlServer=
export ConnectionStrings__SQLite='Filename=./SQLite/OCPP.Core.test.sqlite;foreign keys=True'
export ServerApiUrl='http://localhost:8081/API'
export ApiKey='replace-with-local-api-key'
dotnet run --project OCPP.Core.Management
```

## Tests and Checks

Run the focused server test suite:

```sh
dotnet test OCPP.Core.Server.Tests/OCPP.Core.Server.Tests.csproj
```

Run the SQL Server migration metadata guard after migration changes:

```sh
bash ./scripts/check-mssql-migration-metadata.sh
```

Browser and simulator E2E checks:

```sh
npm ci --prefix Simulators/playwright
npx --prefix Simulators/playwright playwright install chromium
node Simulators/run_e2e_stack.mjs
```

The broader local regression script expects the solution to already be built:

```sh
python3 Simulators/run_local_regression.py
```

No dedicated formatter or lint command was found. Match the existing C# and Razor style in nearby files.

## Pull Request Expectations

- Keep changes scoped to the behavior or documentation being changed.
- Include tests for behavior changes where practical.
- For public charging or payment changes, validate the server API behavior and the management/public portal surface together.
- For database schema changes, update `OCPP.Core.Database`, add EF Core migrations, inspect the model snapshot, and run the migration metadata guard.
- For configuration, setup, integration, background job, deployment, or architecture changes, update the relevant documentation in the same PR.
- State which checks were run and which were intentionally skipped.

## Coding Conventions

- Projects target `net8.0`.
- Server and management apps use ASP.NET Core `Program` plus `Startup`.
- Controllers and middleware are split into partial classes by protocol or feature area.
- Protocol DTOs live under `Messages_OCPP16`, `Messages_OCPP20`, and `Messages_OCPP21`; schemas live under `Schema16`, `Schema20`, and `Schema21`.
- EF Core model changes belong in `OCPP.Core.Database`; SQL Server migrations live under `OCPP.Core.Database/Migrations`.
- Tests use xUnit in `OCPP.Core.Server.Tests`.
- Preserve existing GPL headers when editing files that already have them.

## Documentation Expectations

Documentation is part of the definition of done. Update the most specific existing document first:

- `README.md` for basic setup and usage.
- `AGENTS.md` for durable instructions to future Codex agents.
- `docs/project-overview.md` for purpose, scope, actors, and domain concepts.
- `docs/architecture.md` for module boundaries, data flow, and integration points.
- `docs/features.md` for behavior inventory.
- `docs/operations.md` for runtime configuration, local runbooks, jobs, deployment hints, and gotchas.
- `docs/maintenance.md` for validation, common failure points, recurring tasks, and technical debt.
- `docs/decisions/` for durable architectural decisions.

If something is unclear, document it as `Unknown / verify` instead of guessing.

## Public/Private Safety

Never commit:

- secrets, credentials, API keys, tokens, production passwords, or private keys
- sensitive customer/client data
- private commercial strategy or roadmap
- private deployment details, private hostnames, private IPs, or private infrastructure details
- private Drive links or personal assistant-routing notes

The checked `appsettings.json` files contain development/sample values. Override them through environment variables, user secrets, or deployment-specific secret management for any real run.

## Generated Files

Do not add generated build output, logs, temporary diagnostics, local secrets, or test reports unless a maintainer intentionally wants them as fixtures. Existing checked SQLite and simulator artifacts should be treated carefully: verify whether they are intended fixtures before deleting, replacing, or adding similar files.
