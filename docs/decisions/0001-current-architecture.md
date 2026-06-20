# 0001 - Current architecture

Status: current / accepted

## Context

The repository currently implements OCPP server behavior, operator management, public charging/payment flows, database access, integrations, and test simulators in one .NET solution.

The codebase needs durable documentation that helps future maintainers and agents understand the current architecture without relying on private deployment context.

## Decision

Document the current architecture as a two-application ASP.NET Core system with a shared EF Core database model:

- `OCPP.Core.Server` owns OCPP WebSocket connections, live in-memory charge point state, protocol-specific message processing, server `/API/...` commands, payment coordination, invoice integration hooks, and server-side maintenance jobs.
- `OCPP.Core.Management` owns the operator MVC UI and anonymous public charging/payment UI. It reads/writes shared database state and calls the server API for live status and remote operations.
- `OCPP.Core.Database` owns EF Core entities, provider selection, SQL Server migrations, and SQLite local/test support.
- Extension contracts are kept in `OCPP.Core.Server.Extensions`, with sample extension implementations under `Extensions`.
- Simulators, Playwright tests, and xUnit tests are part of the supported maintenance surface.

## Consequences

- Behavior changes often span multiple projects, especially payments and public charging.
- Server API changes can break management controllers and public payment/status pages.
- Database schema changes require EF migration discipline and SQL Server metadata validation.
- SQLite tests are useful but do not prove SQL Server migration correctness.
- Live OCPP command behavior depends on in-memory connected charge point sessions.
- Documentation must stay public-safe because repository visibility is public.

## Unknowns / Verify

- Whether this architecture is intended to support multiple server instances without sticky charge point sessions.
- Whether SQL scripts under `SQL-Server/` are still an operational source of truth.
- Whether current checked SQLite and Playwright output artifacts are intentional fixtures.
- Whether charger-side reservation profile support should remain disabled permanently.
