# Local Company Invoice Demo Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build and record a reusable local-only walkthrough of the current Czech and Croatian company-invoice UI, including post-issuance locking.

**Architecture:** Add a deterministic SQLite fixture module and a Playwright runner that starts the real .NET applications on loopback, uses mock Stripe with providers and email disabled, records the real local UI, and writes private artifacts outside source control. Keep all product behavior unchanged.

**Tech Stack:** Node.js ESM, Node test runner, Playwright/Chromium, .NET 8 ASP.NET Core, EF Core SQLite, xUnit.

## Global Constraints

- Use current `origin/main` including merged PR #15.
- Use only loopback HTTP, disposable SQLite, mock Stripe, and fake example data.
- Set `Invoices__Enabled=false` and `Notifications__EnableCustomerEmails=false`.
- Do not read or call production, live Stripe, e-racuni, fiscalization, SMTP, or customer data.
- Do not change invoice, tax, VAT, payment, or product behavior.
- Keep generated video and screenshots outside the public repository.
- Keep committed docs collaborator-neutral and public-safe.

---

### Task 1: Deterministic invoice demo fixtures

**Files:**
- Create: `Simulators/lib/invoice_demo_fixtures.mjs`
- Create: `Simulators/tests/invoice_demo_fixtures.test.mjs`
- Modify: `Simulators/lib/sqlite_helpers.mjs`

**Interfaces:**
- Produces: `invoiceDemoFixtures` with `foreignEditable`, `croatianEditable`, and `foreignLocked` reservation definitions.
- Produces: `buildInvoiceDemoSql(nowIso)` returning the bounded SQLite fixture SQL.
- Produces: `seedInvoiceDemoFixtures(dbPath)` executing only that SQL against the disposable database.

- [ ] **Step 1: Write failing fixture contract tests**

Assert that the three fixed reservations exist, all emails use `.test`, no value contains a production hostname, the locked fixture has confirmed buyer data plus an invoice-log marker, and the SQL includes the dedicated charge point, connector, three reservations, and one invoice log.

- [ ] **Step 2: Run the fixture tests and verify RED**

Run: `node --test Simulators/tests/invoice_demo_fixtures.test.mjs`

Expected: FAIL because `Simulators/lib/invoice_demo_fixtures.mjs` does not exist.

- [ ] **Step 3: Implement the minimal fixture module and SQLite helper**

Use fixed GUIDs and synthetic Czech/Croatian values. Build one transaction that removes only the fixed fixture IDs and inserts the dedicated local charge point, three distinct connectors, three completed reservations with unique inactive `ActiveConnectorKey` values, and a synthetic completed invoice log for the locked reservation.

- [ ] **Step 4: Run the fixture tests and verify GREEN**

Run: `node --test Simulators/tests/invoice_demo_fixtures.test.mjs`

Expected: PASS.

- [ ] **Step 5: Commit the fixture slice**

```sh
git add Simulators/lib/invoice_demo_fixtures.mjs Simulators/lib/sqlite_helpers.mjs Simulators/tests/invoice_demo_fixtures.test.mjs
git commit -m "test: add deterministic invoice demo fixtures"
```

### Task 2: Local stack and browser recording runner

**Files:**
- Create: `Simulators/playwright/invoice_demo.mjs`
- Create: `Simulators/tests/invoice_demo_runner.test.mjs`
- Modify: `Simulators/playwright/package.json`

**Interfaces:**
- Consumes: `seedTestStack`, `seedInvoiceDemoFixtures`, and `invoiceDemoFixtures`.
- Produces: `npm run demo:invoice --prefix Simulators/playwright`.
- Produces: `walkthrough.webm`, numbered PNG screenshots, `manifest.json`, and local logs in `INVOICE_DEMO_ARTIFACT_DIR`.

- [ ] **Step 1: Write failing runner safety tests**

Assert an exported `buildDemoEnvironment(runtime)` binds loopback endpoints, enables mock Stripe, disables invoices and customer emails, clears SQL Server configuration, and contains no live provider keys. Assert `assertPrivateArtifactDirectory(repoRoot, artifactDir)` rejects the worktree root and accepts an external directory.

- [ ] **Step 2: Run the runner tests and verify RED**

Run: `node --test Simulators/tests/invoice_demo_runner.test.mjs`

Expected: FAIL because the runner exports do not exist.

- [ ] **Step 3: Implement the orchestration and safety helpers**

Create disposable runtime paths, select unused loopback ports, spawn server and management with captured logs, wait for health, seed fixtures, and shut down children on success, signal, or error. Add the `demo:invoice` package script.

- [ ] **Step 4: Implement the recorded browser sequence**

Use Playwright Chromium with `recordVideo`. Add readable in-page captions; exercise the start-page company-invoice choice, Czech review and save, Croatian invalid/valid OIB path, and locked state. Capture numbered screenshots and rename the completed video to `walkthrough.webm`.

- [ ] **Step 5: Verify runner unit tests GREEN**

Run: `node --test Simulators/tests/invoice_demo_runner.test.mjs Simulators/tests/invoice_demo_fixtures.test.mjs`

Expected: PASS.

- [ ] **Step 6: Commit the runner slice**

```sh
git add Simulators/playwright/invoice_demo.mjs Simulators/playwright/package.json Simulators/tests/invoice_demo_runner.test.mjs
git commit -m "feat: record local company invoice walkthrough"
```

### Task 3: Operator runbook and end-to-end artifact verification

**Files:**
- Create: `docs/local-company-invoice-demo.md`
- Modify: `README.md`

**Interfaces:**
- Consumes: the `demo:invoice` command and artifact manifest.
- Produces: a public-safe repeatable operator procedure and verified private artifacts.

- [ ] **Step 1: Document prerequisites, invocation, and safety proof**

Document `dotnet`, `node`, `npm`, `sqlite3`, and Playwright Chromium prerequisites; the external artifact directory invocation; optional `INVOICE_DEMO_HEADLESS=0`; output files; and the exact disabled integrations.

- [ ] **Step 2: Run the real demo command**

Run with `INVOICE_DEMO_ARTIFACT_DIR` set to a private path outside the worktree.

Expected: exit 0 with non-empty `walkthrough.webm`, numbered screenshots, logs, and `manifest.json`; Czech and Croatian saves succeed, invalid OIB is observed, and locked controls are disabled.

- [ ] **Step 3: Inspect artifact safety and readability**

Verify the manifest contains only loopback URLs and fake fixture values, screenshots are readable at their natural resolution, the WebM has video duration greater than zero, and no artifact is tracked by Git.

- [ ] **Step 4: Run repository verification**

Run:

```sh
node --test Simulators/tests/invoice_demo_fixtures.test.mjs Simulators/tests/invoice_demo_runner.test.mjs
DOTNET_ROLL_FORWARD=Major dotnet test OCPP.Core.Server.Tests/OCPP.Core.Server.Tests.csproj
DOTNET_ROLL_FORWARD=Major dotnet build OCPP.Core.sln --no-restore
bash ./scripts/check-mssql-migration-metadata.sh
git diff --check
git status --short
```

Expected: all tests/checks pass and only intended source/docs changes remain before commit.

- [ ] **Step 5: Commit documentation and final validation evidence**

```sh
git add README.md docs/local-company-invoice-demo.md
git commit -m "docs: add local invoice demo runbook"
```

- [ ] **Step 6: Push and open a draft PR**

Push `req/2026-07-15-002-tehnoline-local-invoice-demo`, open a draft PR, and report the exact artifact path separately from the public PR body.
