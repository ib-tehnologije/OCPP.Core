# Repository Guidelines

## Project Structure & Module Organization
Solution `OCPP.Core.sln` coordinates the primary services. `OCPP.Core.Server/` hosts the WebSocket endpoints, protocol controllers, and middleware. `OCPP.Core.Management/` delivers the ASP.NET Core admin UI with localized views and static assets under `wwwroot/`. Data entities and EF Core migrations live in `OCPP.Core.Database/`. Optional integrations sit in `Extensions/`, with shared contracts in `OCPP.Core.Server.Extensions/`. Reference material and scripts reside in `SQL-Server/`, `SQLite/`, `Simulators/`, and `images/`.

## Build, Test, and Development Commands
- `dotnet restore OCPP.Core.sln` — fetch NuGet dependencies for all projects.
- `dotnet build OCPP.Core.sln` — compile the entire solution.
- `dotnet run --project OCPP.Core.Server` — launch the WebSocket listener (default `http://localhost:8081`).
- `dotnet run --project OCPP.Core.Management` — start the management UI (default `http://localhost:8082`).
- `dotnet run --project OCPP.Core.Test` — execute the interactive OCPP scenario harness after updating its credentials.

## Coding Style & Naming Conventions
Follow standard .NET 8 conventions: four-space indentation, braces on new lines, PascalCase for classes and public members, camelCase for locals and parameters, and suffix async methods with `Async`. Mirror folder namespaces and keep files focused on one responsibility. Run `dotnet format` (or your editor’s equivalent) before pushing to normalize spacing, imports, and analyzer hints.

## Testing Guidelines
Automated unit tests are not yet wired up; `OCPP.Core.Test/` supplies interactive regression coverage for 1.6, 2.0, and 2.1 flows. Update the station ID, tag, and API key constants, then run `dotnet run --project OCPP.Core.Test`. Capture console output or screenshots when validating fixes. Extend the relevant protocol test or clearly document manual steps when introducing new behavior.

## Commit & Pull Request Guidelines
Commit messages stay short, capitalized, and action-oriented (e.g., `Start transaction from UI`). Group related changes per commit and avoid mixing formatting with logic. Pull requests should describe the motivation, outline configuration prerequisites (database, certs, URLs), and list the manual or automated checks performed (`dotnet build`, simulator walkthroughs, screenshots where UI changes). Link issues or reference documentation updates when applicable.

## Configuration & Security Tips
Do not commit real certificates or secrets; `localhost.pfx` and `appsettings.Development.json` are development placeholders. Store SQL Server passwords, API keys, and similar values in user secrets or environment variables as outlined in `Installation.md`. When sharing configuration examples, redact the secrets but preserve the shape of the connection strings for clarity.

## Migration Policy
- Do not generate or commit EF Core migration files manually. Schema changes must be handled through the agreed automation or by the maintainers; leave existing migrations untouched.
