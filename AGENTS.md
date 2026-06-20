# Repository Instructions

## Repository Purpose

This repository is a public .NET 8 OCPP central-system codebase. It contains an OCPP WebSocket server, an operator management portal, a public charging/payment portal, EF Core database model and migrations, extension interfaces, simulator tooling, and regression tests.

## Repository Visibility

Classification: public/open-source.

Determination: `gh repo view --json nameWithOwner,url,isPrivate,visibility,viewerPermission` reported `nameWithOwner=ib-tehnologije/OCPP.Core`, `isPrivate=false`, and `visibility=PUBLIC`.

Because this repository is public, committed docs and code comments must remain public-safe. Private client, business, roadmap, deployment, infrastructure, credential, and personal assistant-routing context belongs outside this repository.

## Documentation Audience

Classification: public/open-source.

Write committed documentation for maintainers, contributors, future operators, and Codex agents who can inspect this repository but may not have access to any private context outside it.

## Important Folders and Modules

- `OCPP.Core.Server/` - ASP.NET Core OCPP server. `OCPPMiddleware.cs` routes `/OCPP/{chargePointId}` WebSocket sessions and `/API/...` commands. Protocol-specific receive/control logic is split across `OCPPMiddleware.OCPP16.cs`, `OCPPMiddleware.OCPP20.cs`, and `OCPPMiddleware.OCPP21.cs`. `ControllerOCPP*.cs` files process protocol actions.
- `OCPP.Core.Server/Payments/` - Stripe payment flow, reservation lifecycle, public charging start/stop coordination, hosted cleanup, idle-fee warning email logic, and invoice integration hooks.
- `OCPP.Core.Management/` - ASP.NET Core MVC operator portal, public charging portal, payment status pages, owner/charge reports, account/login flow, localization resources, and static assets.
- `OCPP.Core.Database/` - EF Core `OCPPCoreContext`, entity classes, provider selection, migrations, and model snapshot.
- `OCPP.Core.Server.Extensions/` - public interfaces for runtime-loaded extensions.
- `Extensions/` - sample extensions for Azure Service Bus raw-message forwarding and external authorization.
- `OCPP.Core.Server.Tests/` - xUnit test suite for core behavior.
- `OCPP.Core.Test/` - console OCPP protocol harness.
- `Simulators/` - local simulators, E2E scripts, Playwright tests, and regression helpers.
- `SQL-Server/`, `SQLite/` - database scripts and checked local fixtures.
- `.github/workflows/` - CI, E2E, browser regression, and Docker image workflows.
- `docs/` - durable project documentation. Update these files when behavior or maintenance procedures change.

## Build, Run, Test, and Check Commands

Common commands:

```sh
dotnet restore OCPP.Core.sln
dotnet build OCPP.Core.sln
dotnet test OCPP.Core.Server.Tests/OCPP.Core.Server.Tests.csproj
bash ./scripts/check-mssql-migration-metadata.sh
```

Make targets:

```sh
make restore
make build
make run-server
make run-management
make check-migration-metadata
make sqlite-reset
```

Local SQLite run pattern:

```sh
export ConnectionStrings__SqlServer=
export ConnectionStrings__SQLite='Filename=./SQLite/OCPP.Core.test.sqlite;foreign keys=True'
export ApiKey='replace-with-local-api-key'
dotnet run --project OCPP.Core.Server
```

Management app must use the same database and API key:

```sh
export ConnectionStrings__SqlServer=
export ConnectionStrings__SQLite='Filename=./SQLite/OCPP.Core.test.sqlite;foreign keys=True'
export ServerApiUrl='http://localhost:8081/API'
export ApiKey='replace-with-local-api-key'
dotnet run --project OCPP.Core.Management
```

E2E commands:

```sh
npm ci --prefix Simulators/playwright
npx --prefix Simulators/playwright playwright install chromium
node Simulators/run_e2e_stack.mjs
python3 Simulators/run_local_regression.py
```

The Python regression script expects the solution to already be built.

No dedicated formatter or lint command was found. Use the existing C# and Razor style in nearby files.

## Coding Conventions Observed

- C# projects target `net8.0`.
- Server and management apps use classic ASP.NET Core `Program` plus `Startup`, not minimal APIs.
- Controllers and middleware are split into partial classes by protocol or feature area.
- Protocol message DTOs are grouped under `Messages_OCPP16`, `Messages_OCPP20`, and `Messages_OCPP21`; JSON schemas live under matching `Schema16`, `Schema20`, and `Schema21` folders.
- Configuration is read from `appsettings.json`, optional environment-specific appsettings, user secrets for app projects, and environment variables.
- EF Core model changes belong in `OCPP.Core.Database`; SQL Server migrations live under `OCPP.Core.Database/Migrations`.
- Tests use xUnit in `OCPP.Core.Server.Tests`; prefer focused tests near the behavior being changed.
- Existing code has a mix of nullable and non-nullable contexts. Match the local file style.
- Many files retain original GPL headers. Preserve existing headers when editing those files.

## Architecture Constraints

- The OCPP server middleware owns live WebSocket state in memory and exposes `/API/...` operations for management/payment flows.
- The management app calls the server API through `ServerApiUrl` and `X-API-Key`; keep API key configuration synchronized between apps.
- Charge points must exist in the database before WebSocket connections are accepted.
- SQL Server is the migration-backed provider. SQLite is used for local/test runs and uses `EnsureCreated()` at server startup.
- Keep migration metadata SQL Server-shaped. Run `scripts/check-mssql-migration-metadata.sh` after migration changes.
- Hangfire starts only when `ConnectionStrings:SqlServer` is configured. Do not assume Hangfire jobs run under SQLite.
- Runtime extensions are loaded from an output `Extensions` directory and must implement `IRawMessageSink` or `IExternalAuthorization`.
- Public charging and payment behavior spans both apps and shared database state; validate both server API behavior and management/public portal UI when changing it.

## Documentation Maintenance Rule

Documentation is part of the definition of done.

When a task changes behavior, APIs, database schema, config, setup commands, tests, background jobs, integrations, deployment, architecture, or maintenance procedures, update the relevant docs in the same task.

If repository visibility is unclear, default to public-safe documentation.

## Collaborator-Neutral Documentation Rule

Committed repository documentation must be useful to maintainers and collaborators who do not have access to any personal Google Drive, ChatGPT memory, assistant setup, or private project registry.

Technical documentation lives in this repository.

Private business, client, deployment, roadmap, and operational context may be maintained outside this repository. Do not add raw private Drive links, personal assistant-routing notes, secrets, credentials, private infrastructure details, or sensitive client context to committed docs.

For private/internal repositories, treat docs as team-private and collaborator-neutral. For public/open-source repositories, committed docs must remain public-safe.

## Optional External Private Context

This repository should be understandable from its committed documentation alone.

The current operator may have access to external private context for this project, such as Google Drive, email, tickets, client notes, deployment notes, or team knowledge bases. Use that context as supplemental information when available, especially for business intent, client constraints, deployment expectations, roadmap, or historical decisions.

Do not make repository work depend on external private context. Do not copy private external links, client-sensitive notes, secrets, credentials, private infrastructure details, or personal assistant-routing instructions into committed files.

If external private context conflicts with committed technical docs, prefer the repository for code-level facts and mention the conflict in the final response.

Use the most specific existing document first:

- `README.md` for basic project usage/setup
- `AGENTS.md` for durable instructions to future coding agents
- `CONTRIBUTING.md` for public contribution workflow
- `docs/project-overview.md` for purpose/status/domain context
- `docs/architecture.md` for architecture/module boundaries
- `docs/features.md` for behavior and feature inventory
- `docs/operations.md` for runtime/deployment/config
- `docs/maintenance.md` for maintenance procedures and known risks
- `docs/decisions/*.md` for durable architectural decisions

If a change creates or reveals a durable architectural decision, add or update an ADR under `docs/decisions/`.

## Public/Private Safety Rules

Never commit:

- secrets, credentials, API keys, tokens, production passwords, or private keys
- sensitive customer/client data
- private commercial strategy or roadmap
- private deployment details, private hostnames, private IPs, or private infrastructure details
- private Drive links or personal assistant-routing notes

If unsure whether something is safe for a public repo, do not commit it. Put it in external private documentation or ask a maintainer.

## Done Means

For future Codex tasks, done means:

- the requested behavior or documentation change is implemented
- public/private safety was considered before writing public files
- relevant docs were updated in the same task
- tests/checks appropriate to the change were run, or skipped with a concrete reason
- database changes include migrations and `check-mssql-migration-metadata` validation
- public charging/payment changes are validated across server API and management/public portal surfaces
- no temporary diagnostics, local secrets, generated build output, or private context was committed
- committed docs remain collaborator-neutral and public-safe
- final response states what changed, what was checked, and what remains unknown
