DOTNET ?= dotnet
SOLUTION := OCPP.Core.sln
STARTUP := OCPP.Core.Server
DB_PROJECT := OCPP.Core.Database
DB_CONTEXT := OCPPCoreContext
CONFIG ?= Debug
MIGRATION ?=
NAME ?= NewMigration

.PHONY: restore build run-server run-management migrate migrate-sqlite auto-migrate add-migration

restore:
	$(DOTNET) restore $(SOLUTION)

build: restore
	$(DOTNET) build $(SOLUTION) --configuration $(CONFIG)

run-server: restore
	$(DOTNET) run --project $(STARTUP) --configuration $(CONFIG)

run-management: restore
	$(DOTNET) run --project OCPP.Core.Management --configuration $(CONFIG)

migrate: restore
	$(DOTNET) ef database update $(MIGRATION) --project $(DB_PROJECT) --startup-project $(STARTUP) --context $(DB_CONTEXT)

auto-migrate: MIGRATION=
auto-migrate: migrate

# Scaffold a new migration: make add-migration NAME=AddSomething
add-migration: restore
	$(DOTNET) ef migrations add $(NAME) --project $(DB_PROJECT) --startup-project $(STARTUP) --context $(DB_CONTEXT)

# Use SQLite (defaults to ./SQLite/OCPP.Core.sqlite) by clearing SqlServer and setting SQLite.
migrate-sqlite: export ConnectionStrings__SqlServer=
migrate-sqlite: export ConnectionStrings__SQLite?=Filename=./SQLite/OCPP.Core.sqlite;foreign keys=True
migrate-sqlite: migrate
