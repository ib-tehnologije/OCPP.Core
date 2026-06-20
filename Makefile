DOTNET ?= dotnet
SOLUTION := OCPP.Core.sln
STARTUP := OCPP.Core.Server
DB_PROJECT := OCPP.Core.Database
DB_CONTEXT := OCPPCoreContext
CONFIG ?= Debug
MIGRATION_NAME ?= migration_$(shell date +%Y%m%d%H%M%S)
NAME ?= NewMigration

.PHONY: restore build run-server run-management migrate migrate-sqlite sqlite-reset auto-migrate add-migration dbupdate check-migration-metadata

SQLITE_DB ?= ./SQLite/OCPP.Core.test.sqlite

restore:
	$(DOTNET) restore $(SOLUTION)

build: restore
	$(DOTNET) build $(SOLUTION) --configuration $(CONFIG)

run-server: restore
	$(DOTNET) run --project $(STARTUP) --configuration $(CONFIG)

run-management: restore
	$(DOTNET) run --project OCPP.Core.Management --configuration $(CONFIG)

run:
	make run-server & make run-management

# Scaffold a new migration with a generated timestamped name
migrate: export ASPNETCORE_ENVIRONMENT=Production
migrate: export DOTNET_ENVIRONMENT=Production
migrate: export ConnectionStrings__SQLite=
migrate: restore
	$(DOTNET) ef migrations add $(MIGRATION_NAME) --project $(DB_PROJECT) --startup-project $(STARTUP) --context $(DB_CONTEXT) --verbose

# Update database schema to latest migration
dbupdate: export ASPNETCORE_ENVIRONMENT=Production
dbupdate: export DOTNET_ENVIRONMENT=Production
dbupdate: export ConnectionStrings__SQLite=
dbupdate: restore
	$(DOTNET) ef database update --project $(DB_PROJECT) --startup-project $(STARTUP) --context $(DB_CONTEXT)

# Backward-compatible alias
auto-migrate: dbupdate

# Scaffold a new migration with a custom name: make add-migration NAME=AddSomething
add-migration: export ASPNETCORE_ENVIRONMENT=Production
add-migration: export DOTNET_ENVIRONMENT=Production
add-migration: export ConnectionStrings__SQLite=
add-migration: restore
	$(DOTNET) ef migrations add $(NAME) --project $(DB_PROJECT) --startup-project $(STARTUP) --context $(DB_CONTEXT)

check-migration-metadata:
	bash ./scripts/check-mssql-migration-metadata.sh

# Local SQLite uses EnsureCreated() at app startup instead of EF migrations.
sqlite-reset:
	@mkdir -p $(dir $(SQLITE_DB))
	@rm -f "$(SQLITE_DB)" "$(SQLITE_DB)-shm" "$(SQLITE_DB)-wal"
	@echo "Removed $(SQLITE_DB). Start $(STARTUP) once to recreate the SQLite schema via EnsureCreated()."

# Backward-compatible alias for rebuilding the local SQLite database.
migrate-sqlite: sqlite-reset
