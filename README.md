# OCPP.Core

OCPP.Core is a .NET 8 implementation of an Open Charge Point Protocol (OCPP) central system. It includes:

- an OCPP server for charge point WebSocket sessions and server-side control APIs
- a management web application for operators
- an EF Core database model with SQL Server migrations and local SQLite support
- payment, public charging, invoice, reporting, simulator, and regression-test support

This repository is public. Committed documentation must stay public-safe. Private/internal project context is maintained outside this public repository.

## Current Status

The repository has active build, unit-test, browser regression, simulator E2E, and Docker image workflows. Release process, supported production topology, and production operations are Unknown / verify.

License: GPL-3.0-or-later as indicated by `LICENSE` and source headers.

## Repository Layout

- `OCPP.Core.Server/` - OCPP WebSocket middleware, protocol controllers, payment coordination, invoice integration, hosted maintenance services, and server Dockerfile.
- `OCPP.Core.Management/` - ASP.NET Core MVC management portal, public charging portal, reporting, email sending, and management Dockerfile.
- `OCPP.Core.Database/` - EF Core context, entities, migrations, and SQL Server/SQLite provider selection.
- `OCPP.Core.Server.Extensions/` - extension interfaces for raw-message sinks and external authorization.
- `Extensions/` - sample extension projects, including Azure Service Bus forwarding and authorization override examples.
- `OCPP.Core.Server.Tests/` - xUnit tests for server, management, payment, invoice, portal, and middleware behavior.
- `OCPP.Core.Test/` - console OCPP protocol test harness.
- `Simulators/` - HTML and Node/Python simulators plus Playwright browser tests.
- `SQL-Server/` and `SQLite/` - schema scripts and checked local database fixtures.
- `.github/workflows/` - CI, E2E, browser regression, and Docker image workflows.

## Prerequisites

- .NET SDK capable of building `net8.0` projects. CI uses .NET `8.0.x`.
- SQL Server for migration-backed runs, or SQLite for local/test runs.
- Node.js and npm for simulator and Playwright E2E work. CI uses Node `20`.
- Docker if you want to build the server or management images.
- Optional: `dotnet-ef` for creating or applying EF Core migrations.

No `global.json` is currently present, so SDK selection comes from the local machine or CI setup.

## Restore and Build

```sh
dotnet restore OCPP.Core.sln
dotnet build OCPP.Core.sln
```

The `Makefile` wraps common .NET commands:

```sh
make restore
make build
make run-server
make run-management
```

## Local Run

Run the server and management app in separate terminals. Use environment variables or user secrets for local values. Do not rely on committed `appsettings.json` values for shared or deployed environments.

Server with SQLite:

```sh
export ConnectionStrings__SqlServer=
export ConnectionStrings__SQLite='Filename=./SQLite/OCPP.Core.test.sqlite;foreign keys=True'
export ApiKey='replace-with-local-api-key'
dotnet run --project OCPP.Core.Server
```

Management app with the same database and API key:

```sh
export ConnectionStrings__SqlServer=
export ConnectionStrings__SQLite='Filename=./SQLite/OCPP.Core.test.sqlite;foreign keys=True'
export ServerApiUrl='http://localhost:8081/API'
export ApiKey='replace-with-local-api-key'
dotnet run --project OCPP.Core.Management
```

Default local endpoints from the checked configuration are:

- server HTTP: `http://localhost:8081`
- server HTTPS: `https://localhost:8091`
- management HTTP: `http://localhost:8082`
- management HTTPS: `https://localhost:8092`
- OCPP WebSocket path: `/OCPP/{chargePointId}`
- server API path: `/API/...`

Charge points must exist in the database before the middleware accepts their WebSocket sessions.

## Tests and Checks

Fast checks:

```sh
bash ./scripts/check-mssql-migration-metadata.sh
dotnet test OCPP.Core.Server.Tests/OCPP.Core.Server.Tests.csproj
```

Full solution validation:

```sh
dotnet restore OCPP.Core.sln
dotnet build OCPP.Core.sln
dotnet test OCPP.Core.Server.Tests/OCPP.Core.Server.Tests.csproj --no-build
```

Browser and simulator E2E:

```sh
npm ci --prefix Simulators/playwright
npx --prefix Simulators/playwright playwright install chromium
node Simulators/run_e2e_stack.mjs
```

Regression matrix after building the solution:

```sh
python3 Simulators/run_local_regression.py
```

## Database Notes

`OCPP.Core.Database.DbContextExtensions` selects SQL Server when `ConnectionStrings:SqlServer` is set, otherwise SQLite when `ConnectionStrings:SQLite` is set.

For SQL Server, EF Core migrations live under `OCPP.Core.Database/Migrations`. The repository includes a migration metadata guard:

```sh
make check-migration-metadata
```

For local SQLite runs, the server uses `EnsureCreated()` at startup instead of applying the SQL Server-oriented migrations. To reset the checked test SQLite database path used by the `Makefile`:

```sh
make sqlite-reset
```

## Documentation Ownership

Public technical documentation lives in this repository.

Private/internal project context, if any, is maintained outside this public repository. Do not add private client, commercial, roadmap, deployment, infrastructure, credential, or personal assistant-routing context to committed docs.

## Deeper Documentation

- [Contributing](CONTRIBUTING.md)
- [Project overview](docs/project-overview.md)
- [Architecture](docs/architecture.md)
- [Features](docs/features.md)
- [Operations](docs/operations.md)
- [Maintenance](docs/maintenance.md)
- [Current architecture ADR](docs/decisions/0001-current-architecture.md)
