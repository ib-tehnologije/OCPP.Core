# Recovery CLI Bootstrap Design

## Problem

`OCPP.Core.Recovery` builds an `IConfiguration` instance and passes it to `Startup.ConfigureServices`, but the standalone `ServiceCollection` does not contain that instance. The registered `IPaymentCoordinator` factory later requests `IConfiguration`, so the executable stops while resolving services before it can print a dry-run digest or assess a manifest.

Existing service-level recovery tests do not invoke `OCPP.Core.Recovery.Program.Main`, so they cannot detect this executable bootstrap regression.

## Considered Approaches

1. Register the existing configuration instance in the standalone service collection. This is the smallest change, preserves the established configuration sources, and makes the recovery executable match the dependency assumptions already encoded in `Startup.ConfigureServices`.
2. Refactor recovery startup into a new host-builder abstraction. This could centralize application startup, but it broadens a one-line bootstrap defect into an architectural change and risks starting hosted services that the operator-only command must not run.
3. Remove `IConfiguration` dependencies from payment and invoice services. This would alter shared runtime architecture and is unrelated to the executable defect.

Approach 1 is selected.

## Design

Immediately after constructing the recovery command's `ServiceCollection`, register the exact `IConfiguration` instance produced by its existing `ConfigurationBuilder`. Then call the existing server `Startup.ConfigureServices` unchanged and resolve the existing coordinator, invoice service, and database context as before.

No new configuration source, key, service lifetime, provider operation, recovery rule, or execution path is introduced. Dry-run remains the default. Mutation still requires both `--execute` and the exact manifest SHA-256 confirmation.

## Regression Coverage

Add Program-level integration tests that call the real `OCPP.Core.Recovery.Program.Main` entry point with isolated temporary manifests and SQLite databases:

- A valid empty manifest must return success and print the dry-run manifest digest.
- A valid synthetic `recover-invoice` manifest must reach the real assessment path, print `DryRunEligibleProviderLookupRequired`, and leave the database unchanged.

The tests use the real startup registrations and real EF Core SQLite context. They disable real payment and invoice integrations through local environment configuration and never provide production credentials or endpoints. Console streams, environment variables, database files, and manifest files are restored or removed in `finally` cleanup.

The production change that both tests protect against is omission of the built `IConfiguration` from the executable service collection.

## Operational and Deployment Impact

There is no database schema, migration, secret, or production configuration change. Building a release containing this fix is required before the corrected executable exists in an environment. Deploying or running any real recovery manifest remains a separate operator-approved action.
