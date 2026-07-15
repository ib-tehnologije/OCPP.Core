# Local Company Invoice Demo Design

## Goal

Provide a deterministic, one-command local walkthrough of the public company-invoice journey on the current application UI. The walkthrough must use disposable fake data and local services only, record the browser as WebM, capture screenshots of each major state, and leave provider, payment, email, and production integrations untouched.

## Chosen Approach

Extend the existing simulator and Playwright tooling with a purpose-built invoice demo runner. The runner starts the real server and management applications against a new temporary SQLite database, configures mock Stripe, disables invoices and customer emails, seeds fixed demo reservations, launches Chromium, exercises the real local controller/API path, and shuts everything down when recording finishes.

This is preferred to a complete OCPP simulator/payment lifecycle because the invoice-review behavior does not depend on charger telemetry and fixed reservations make the walkthrough repeatable. It is also preferred to browser route mocking because successful saves and lock behavior must be demonstrated through the real local server and database boundary.

## Components

### Fixture module

A focused simulator module owns public-safe fake identifiers and the SQL used to seed three completed reservations:

- an editable Czech reservation for the full foreign-company form and successful confirmation;
- an editable Croatian reservation for checksum rejection followed by a valid OIB save;
- a confirmed Czech reservation with a local-only invoice-log marker so the page renders the post-issuance locked state.

The fixture also adds a dedicated fake charge point and connector. All identifiers, company names, addresses, emails, and tax values use reserved or clearly synthetic examples.

### Demo runner

The runner creates a temporary runtime directory, starts both .NET applications on loopback-only ports, waits for them to become healthy, seeds the normal local stack plus the invoice fixtures, and opens Chromium with Playwright video recording enabled. It overlays short captions in the page so the resulting recording remains understandable without private narration.

The browser sequence is:

1. Open the public start page and select Company invoice, showing the post-checkout explanation.
2. Navigate to the Czech reservation status page, fill every required and optional field, show the live review summary, confirm, and save through the real local endpoint.
3. Navigate to the Croatian reservation, submit an invalid OIB to show checksum validation, then enter a valid OIB and save.
4. Navigate to the locked reservation and show that buyer controls cannot be edited after confirmation/provider issuance.

Each major state is captured as a PNG. The Playwright WebM and a JSON manifest are written only to an operator-selected artifact directory outside source control. The manifest records fixture IDs, local URLs, screenshots, the video path, and safety configuration without secrets.

### Operator runbook

A public-safe document explains prerequisites, the one-command invocation, optional headed mode, artifact location, the exact local safety controls, and cleanup behavior. It contains no client, deployment, private Drive, or production context.

## Safety and Error Handling

- Bind HTTP endpoints to `127.0.0.1` only.
- Use a new temporary SQLite database on every run.
- Enable only the repository's mock Stripe services with a fake key.
- Set `Invoices__Enabled=false` and `Notifications__EnableCustomerEmails=false`.
- Do not read production configuration, user secrets, or environment-specific provider credentials.
- Fail if the chosen artifact directory is inside the Git worktree unless the operator explicitly selects a git-ignored destination.
- Stop child applications on normal completion, interruption, or error.
- Preserve runtime logs in the private artifact directory when a run fails.

## Testing

Node tests validate fixed fixture IDs, fake-only values, required reservations, provider-disabled configuration, and SQL structure before the runner is implemented. The demo command itself is an integration acceptance test: it must save Czech and Croatian buyer snapshots in SQLite, observe the invalid Croatian OIB error, observe disabled locked controls, produce non-empty PNG/WebM artifacts, and write a safety manifest. Existing focused invoice tests, the full .NET test suite, build, migration metadata guard, and `git diff --check` remain required.

## Scope Boundaries

The change adds only reusable local fixture/recording tooling, tests, and documentation. It does not change the merged company-invoice product behavior, database model, migrations, provider mapping, tax rules, payment behavior, deployed configuration, or customer communications.
