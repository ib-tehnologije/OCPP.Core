# Foreign Company Invoice Buyer Data Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Persist and issue confirmed Croatian or foreign company invoice buyer data from a charging reservation.

**Architecture:** Add a typed immutable buyer snapshot to `ChargePaymentReservation`, validate and confirm it through the existing R1 request path, build invoice drafts from that durable snapshot, and map supported fields to e-racuni. Keep Stripe metadata as backward-compatible mirroring rather than the source of truth.

**Tech Stack:** .NET 8, ASP.NET Core MVC/Razor, EF Core SQL Server migrations, Stripe SDK models, Newtonsoft.Json, xUnit.

## Global Constraints

- Croatian buyers keep strict OIB checksum validation.
- Foreign identifiers are non-authoritative and never require VIES.
- Limits: name/street 200, postal code 32, city 100, tax identifier/registration number 64, email 254.
- Reject control characters and line breaks; preserve the submitted identifier value after trimming.
- `buyerVatRegistration` is `Registered` only after explicit customer identification; otherwise `Unknown`.
- Do not map legal registration number into `buyerCode`.
- Confirmed buyer data is immutable in the customer flow.
- No deployment, live provider mutation, secret/config change, payment mutation, or client notification.

---

### Task 1: Buyer Snapshot Model and Validation

**Files:**
- Modify: `OCPP.Core.Database/ChargePaymentReservation.cs`
- Modify: `OCPP.Core.Database/OCPPCoreContext.cs`
- Create: `OCPP.Core.Server/Payments/InvoiceBuyerData.cs`
- Test: `OCPP.Core.Server.Tests/InvoiceBuyerDataValidatorTests.cs`

**Interfaces:**
- Produces: `InvoiceBuyerDataValidator.ValidateAndNormalize(PaymentR1InvoiceRequest)` returning a normalized result with field errors.
- Produces: typed `ChargePaymentReservation.InvoiceBuyer*` fields and `InvoiceBuyerConfirmedAtUtc`.

- [ ] Write tests for Croatian OIB, foreign required fields, ISO country, email, exact limits, control characters, VAT flag, and confirmation.
- [ ] Run the focused tests and verify they fail because the validator does not exist.
- [ ] Add the minimal validator and reservation properties.
- [ ] Run focused tests and verify they pass.
- [ ] Commit the model and validator slice.

### Task 2: Durable Confirmation and Idempotency

**Files:**
- Modify: `OCPP.Core.Server/Payments/IPaymentCoordinator.cs`
- Modify: `OCPP.Core.Server/Payments/StripePaymentCoordinator.cs`
- Modify: `OCPP.Core.Server/OCPPMiddleware.cs`
- Test: `OCPP.Core.Server.Tests/StripePaymentCoordinatorTests.cs`
- Test: `OCPP.Core.Server.Tests/OCPPMiddlewareTests.cs`

**Interfaces:**
- Consumes: normalized buyer data from Task 1.
- Produces: an idempotent confirmed reservation snapshot and a sanitized `PaymentR1InvoiceResult`.

- [ ] Write failing tests for persistence, identical retry, conflicting retry, foreign requests, and Croatian compatibility.
- [ ] Run focused tests and verify expected failures.
- [ ] Persist the normalized snapshot before compatibility metadata mirroring and reject conflicting confirmed updates.
- [ ] Remove endpoint-only OIB assumptions and return sanitized field/status errors.
- [ ] Run focused tests and verify they pass.
- [ ] Commit the confirmation slice.

### Task 3: Invoice Draft and e-racuni Mapping

**Files:**
- Modify: `OCPP.Core.Server/Payments/Invoices/InvoiceDraft.cs`
- Modify: `OCPP.Core.Server/Payments/Invoices/InvoiceDraftBuilder.cs`
- Modify: `OCPP.Core.Server/Payments/Invoices/ERacuni/ERacuniApiModels.cs`
- Modify: `OCPP.Core.Server/Payments/Invoices/ERacuni/ERacuniInvoiceRequestFactory.cs`
- Test: `OCPP.Core.Server.Tests/InvoiceDraftBuilderTests.cs`
- Test: `OCPP.Core.Server.Tests/ERacuniInvoiceRequestFactoryTests.cs`

**Interfaces:**
- Consumes: confirmed reservation snapshot, with legacy Stripe fallback for old rows.
- Produces: supported `SalesInvoiceCreate` buyer fields and no legal-registration-number misuse.

- [ ] Write failing tests for snapshot precedence, legacy fallback, full foreign mapping, `Registered`/`Unknown`, and omitted `buyerCode`.
- [ ] Run focused tests and verify expected failures.
- [ ] Extend draft/API models and map only provider-supported fields.
- [ ] Run focused tests and verify they pass.
- [ ] Commit the provider-mapping slice.

### Task 4: Public Review and Confirmation Flow

**Files:**
- Modify: `OCPP.Core.Management/Controllers/PaymentsController.cs`
- Modify: `OCPP.Core.Management/Views/Payments/PublicStatus.cshtml`
- Modify: relevant public-status localization resources/scripts already used by the page
- Test: `OCPP.Core.Server.Tests/ManagementControllerBehaviorTests.cs`
- Test: existing public portal Razor/contract tests, or a focused source contract test beside them

**Interfaces:**
- Consumes: full `PaymentR1InvoiceRequest` contract.
- Produces: country-aware fields, explicit review, and explicit confirmation submission.

- [ ] Write failing controller and Razor contract tests for the full payload and confirmation gate.
- [ ] Run focused tests and verify expected failures.
- [ ] Implement country-aware form fields, review summary, and confirmation checkbox.
- [ ] Forward the complete payload and use sanitized validation messages.
- [ ] Run focused tests and verify they pass.
- [ ] Commit the public-flow slice.

### Task 5: Migration, Documentation, and Full Verification

**Files:**
- Create: `OCPP.Core.Database/Migrations/<timestamp>_AddInvoiceBuyerSnapshot.cs`
- Create: matching migration designer file
- Modify: `OCPP.Core.Database/Migrations/OCPPCoreContextModelSnapshot.cs`
- Modify: `docs/features.md`
- Modify: `docs/operations.md`

**Interfaces:**
- Consumes: final EF model from Tasks 1-4.
- Produces: non-destructive nullable SQL Server columns and public-safe maintenance documentation.

- [ ] Scaffold the migration and inspect it for nullable, bounded SQL Server columns only.
- [ ] Run `bash ./scripts/check-mssql-migration-metadata.sh` and verify PASS.
- [ ] Update public-safe feature and operations documentation.
- [ ] Run focused tests, full `dotnet test`, `dotnet build`, and `git diff --check`.
- [ ] Review the diff for secrets/private context and commit the final slice.
- [ ] Push the request branch, open or update a draft PR, and record validation evidence.

