DOTNET ?= dotnet
SOLUTION := OCPP.Core.sln
STARTUP := OCPP.Core.Server
DB_PROJECT := OCPP.Core.Database
DB_CONTEXT := OCPPCoreContext
CONFIG ?= Debug
MIGRATION_NAME ?= migration_$(shell date +%Y%m%d%H%M%S)
NAME ?= NewMigration

.PHONY: restore build run-server run-management migrate migrate-sqlite auto-migrate add-migration dbupdate

restore:
	$(DOTNET) restore $(SOLUTION)

build: restore
	$(DOTNET) build $(SOLUTION) --configuration $(CONFIG)

run-server: restore
	$(DOTNET) run --project $(STARTUP) --configuration $(CONFIG)

run-management: restore
	$(DOTNET) run --project OCPP.Core.Management --configuration $(CONFIG)

# Scaffold a new migration with a generated timestamped name
migrate: restore
	$(DOTNET) ef migrations add $(MIGRATION_NAME) --project $(DB_PROJECT) --startup-project $(STARTUP) --context $(DB_CONTEXT) --verbose
	$(DOTNET) ef database update --project $(DB_PROJECT) --startup-project $(STARTUP) --context $(DB_CONTEXT) --verbose

# Update database schema to latest migration
dbupdate: restore
	$(DOTNET) ef database update --project $(DB_PROJECT) --startup-project $(STARTUP) --context $(DB_CONTEXT)

# Backward-compatible alias
auto-migrate: dbupdate

# Scaffold a new migration with a custom name: make add-migration NAME=AddSomething
add-migration: restore
	$(DOTNET) ef migrations add $(NAME) --project $(DB_PROJECT) --startup-project $(STARTUP) --context $(DB_CONTEXT)

# Use SQLite (defaults to ./SQLite/OCPP.Core.sqlite) by clearing SqlServer and setting SQLite.
migrate-sqlite: export ConnectionStrings__SqlServer=
migrate-sqlite: export ConnectionStrings__SQLite?=Filename=./SQLite/OCPP.Core.sqlite;foreign keys=True
migrate-sqlite: dbupdate
