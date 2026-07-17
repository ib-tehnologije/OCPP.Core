# Remove Invoice Buyer Browser Persistence Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove the optional remembered-buyer browser persistence while preserving the complete pre-checkout company-buyer form, validation, reservation snapshot, bounded Stripe metadata, legacy compatibility, and post-checkout read-only behavior.

**Architecture:** Delete only the `RememberInvoiceBuyer` UI/model plumbing and the dedicated `invoiceBuyerStorage` helper. Keep ordinary POST error preservation server-rendered through `PublicStartViewModel`, and retain every buyer field, explicit confirmation control, and checkout ordering rule.

**Tech Stack:** .NET 8, ASP.NET Core MVC/Razor, vanilla browser JavaScript, Node.js `node:test`, xUnit, Playwright.

## Global Constraints

- Keep the complete country-aware buyer form and explicit confirmation before Stripe Checkout.
- Keep `ChargePaymentReservation` as the authoritative invoice-buyer source; Stripe remains payment-only.
- Preserve the approved Croatian OIB and foreign identifier rules.
- Preserve retail, below-1-kWh, capture, invoice issuance, legacy endpoint, immutability, and concurrency behavior.
- Persist no company-buyer data in browser storage beyond ordinary form submission and server-rendered validation-error preservation.
- Do not add a migration, change configuration, deploy, or touch live providers.
- Keep committed content public-safe and collaborator-neutral.

---

### Task 1: Remove Runtime Browser Persistence Test-First

**Files:**
- Modify: `OCPP.Core.Server.Tests/PublicStartInvoiceViewTests.cs`
- Modify: `OCPP.Core.Server.Tests/PublicControllerTests.cs`
- Modify: `OCPP.Core.Management/Models/PublicStartViewModel.cs`
- Modify: `OCPP.Core.Management/Controllers/PublicController.cs`
- Modify: `OCPP.Core.Management/Views/Public/Start.cshtml`
- Modify: `OCPP.Core.Management/wwwroot/js/public-portal.js`
- Delete: `OCPP.Core.Management/wwwroot/js/invoice-buyer-storage.js`
- Delete: `Simulators/tests/invoice-buyer-storage.test.mjs`

**Interfaces:**
- Consumes: the existing complete `PublicStartViewModel` buyer contract and server-rendered validation-error state.
- Produces: the same pre-checkout form without `RememberInvoiceBuyer`, `invoiceBuyerStorage`, invoice-buyer `localStorage`, or remember/shared-device copy.

- [ ] **Step 1: Invert the source-contract test to require no persistence surface**

Replace the remembered-buyer assertions in `PublicStartInvoiceViewTests.PublicStartView_CollectsCompleteConfirmedBuyerBeforeCheckout` with:

```csharp
Assert.DoesNotContain("RememberInvoiceBuyer", view);
Assert.DoesNotContain("rememberInvoiceBuyer", view);
Assert.DoesNotContain("invoiceBuyerStorage", view);
Assert.DoesNotContain("invoice-buyer-storage.js", view);
Assert.DoesNotContain("localStorage", view);

var model = ReadProjectFile("OCPP.Core.Management", "Models", "PublicStartViewModel.cs");
var controller = ReadProjectFile("OCPP.Core.Management", "Controllers", "PublicController.cs");
Assert.DoesNotContain("RememberInvoiceBuyer", model);
Assert.DoesNotContain("RememberInvoiceBuyer", controller);
```

Extend `PublicPortalTranslations_DescribePreCheckoutInvoiceConfirmation` with:

```csharp
Assert.DoesNotContain("start.rememberInvoiceBuyer", script, StringComparison.Ordinal);
Assert.DoesNotContain("start.rememberInvoiceBuyerWarning", script, StringComparison.Ordinal);
```

- [ ] **Step 2: Run the focused test and verify RED**

```bash
DOTNET_ROLL_FORWARD=Major dotnet test OCPP.Core.Server.Tests/OCPP.Core.Server.Tests.csproj -c Release --filter FullyQualifiedName~PublicStartInvoiceViewTests
```

Expected: the source-contract test fails because the current branch still renders and wires the remembered-buyer feature.

- [ ] **Step 3: Remove the minimal runtime surface**

Delete `RememberInvoiceBuyer` from `PublicStartViewModel` and its assignment in `PublicController.Start`. Remove the remember checkbox, shared-device warning, `invoice-buyer-storage.js` include, and the complete `invoiceBuyerStorage` IIFE block from `Start.cshtml`. Remove only the `start.rememberInvoiceBuyer` and `start.rememberInvoiceBuyerWarning` entries from each locale in `public-portal.js`. Delete the dedicated storage helper file.

- [ ] **Step 4: Align tests with the narrower contract**

Remove `RememberInvoiceBuyer = true` from the controller POST fixture while keeping the assertion that no remembered-buyer flag is forwarded to `Payments/Create`. Delete the dedicated Node storage test because its production module no longer exists.

- [ ] **Step 5: Run focused tests and verify GREEN**

```bash
DOTNET_ROLL_FORWARD=Major dotnet test OCPP.Core.Server.Tests/OCPP.Core.Server.Tests.csproj -c Release --filter "FullyQualifiedName~PublicStartInvoiceViewTests|FullyQualifiedName~PublicControllerTests.Start_PostForwardsConfirmedInvoiceBuyer"
```

Expected: all selected tests pass and the form still forwards the complete confirmed buyer payload.

### Task 2: Remove Persistence-Only Regression and Documentation Scope

**Files:**
- Modify: `Simulators/playwright/tests/support/session_helpers.mjs`
- Modify: `Simulators/playwright/tests/public-urgent-validation.spec.js`
- Modify: `Simulators/playwright/invoice_demo.mjs`
- Modify: `Simulators/tests/invoice_demo_runner.test.mjs`
- Modify: `docs/features.md`
- Modify: `docs/operations.md`
- Modify: `docs/local-company-invoice-demo.md`
- Modify: `docs/superpowers/specs/2026-07-17-precheckout-invoice-buyer-confirmation-design.md`
- Modify: `docs/superpowers/plans/2026-07-17-precheckout-invoice-buyer-confirmation.md`

**Interfaces:**
- Consumes: the unchanged pre-checkout company-buyer flow and read-only post-checkout status page.
- Produces: regressions and docs that describe only server-authoritative pre-checkout confirmation, with no browser persistence promise.

- [ ] **Step 1: Remove persistence-only browser behavior**

Delete the `rememberInvoiceBuyer` option branch from `startPublicSession`. Remove the localStorage-clear scenario and replace the remembered-buyer Playwright test with a no-persistence regression that completes a company session, verifies no `ocpp.invoiceBuyer.v1` key exists, revisits the start page, and verifies the buyer form starts blank after selecting company invoicing:

```javascript
await expect.poll(() => page.evaluate(() => localStorage.getItem("ocpp.invoiceBuyer.v1"))).toBeNull();
await page.goto(`/Public/Start?cp=${encodeURIComponent(publicStatusInvoiceTarget.chargePointId)}&conn=${publicStatusInvoiceTarget.connectorId}`);
await page.locator("#wantsR1").check();
await expect(page.locator("#buyerCompanyName")).toHaveValue("");
await expect(page.locator("#buyerTaxIdentifier")).toHaveValue("");
await expect(page.locator("#buyerEmail")).toHaveValue("");
```

- [ ] **Step 2: Remove remembered-buyer demo narration**

Delete the remember-checkbox click and shared-device captions from `invoice_demo.mjs`. Rename the affected screenshot/timeline checkpoint from `czech-company-remembered` to `czech-company-confirmed`, and update `invoice_demo_runner.test.mjs` plus `docs/local-company-invoice-demo.md` to describe confirmed pre-checkout data instead of browser reuse.

- [ ] **Step 3: Remove browser persistence from durable docs**

In `docs/features.md` and `docs/operations.md`, retain pre-checkout confirmation, reservation authority, bounded Stripe mirroring, and legacy compatibility while deleting the opt-in local-storage paragraphs. In the design and original implementation plan, remove the device-local goal, consent field, reuse section, storage tests/helper steps, and browser-reuse verification language without weakening the approved flow.

- [ ] **Step 4: Run focused JavaScript, source, and browser suites**

```bash
node --test Simulators/tests/*.test.mjs
node --check OCPP.Core.Management/wwwroot/js/public-portal.js
node --check Simulators/playwright/invoice_demo.mjs
OCPP_PLAYWRIGHT_ENABLE_INVOICES=1 npx --prefix Simulators/playwright playwright test tests/public-urgent-validation.spec.js --grep @invoice
npx --prefix Simulators/playwright playwright test tests/public-start-localization.spec.js
```

Expected: Node, syntax, invoice Playwright, and localization Playwright checks pass without any remembered-buyer selector or storage dependency.

- [ ] **Step 5: Run full repository validation**

```bash
DOTNET_ROLL_FORWARD=Major dotnet build OCPP.Core.sln -c Release --no-restore
DOTNET_ROLL_FORWARD=Major dotnet test OCPP.Core.Server.Tests/OCPP.Core.Server.Tests.csproj -c Release --no-build
bash ./scripts/check-mssql-migration-metadata.sh
git diff --check origin/main...HEAD
git grep -n -i -E 'ocpp\.invoiceBuyer\.v1|RememberInvoiceBuyer|rememberInvoiceBuyer|invoiceBuyerStorage|remember company details|shared device' -- OCPP.Core.Management Simulators docs ':!Simulators/playwright/tests/public-urgent-validation.spec.js' ':!docs/superpowers/plans/2026-07-17-remove-invoice-buyer-browser-persistence.md'
```

Expected: build and test suites pass, migration metadata remains valid, diff hygiene passes, and the final grep returns no runtime or durable feature-documentation references.

- [ ] **Step 6: Commit and publish the exact reviewed head**

```bash
git add -A
git commit -m "fix: remove invoice buyer browser persistence"
git push origin req/2026-07-17-precheckout-invoice-buyer-details-impl
```

Verify draft PR #18 points to the new commit and wait for current GitHub checks to complete before returning the queue item to QA.
