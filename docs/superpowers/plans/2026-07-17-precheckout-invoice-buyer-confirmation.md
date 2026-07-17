# Pre-checkout Invoice Buyer Confirmation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Confirm and persist Croatian or foreign company invoice buyer data before Stripe Checkout, with explicit device-local reuse, so charging completion cannot race ahead of R1 classification.

**Architecture:** Extend the existing payment-session request with the established buyer contract and reuse `InvoiceBuyerDataValidator` inside `CreateCheckoutSession`. Apply the normalized snapshot to `ChargePaymentReservation` before Stripe options are created, mirror only bounded compatibility fields to Stripe, move the complete form to the public start page, and remove late-entry controls from the status page while preserving the legacy endpoint.

**Tech Stack:** .NET 8, ASP.NET Core MVC/Razor, EF Core, Stripe SDK abstractions, vanilla browser JavaScript, Node.js `node:test`, xUnit.

## Global Constraints

- Keep `ChargePaymentReservation` as the authoritative invoice-buyer source; Stripe remains payment-only.
- Croatian buyers require a checksum-valid 11-digit OIB.
- Foreign buyers require country, legal name, street, postal code, city, billing email, and a bounded identifier; registry and VIES lookup remain out of scope.
- Persist no reservation, charger, invoice, or payment identifiers in browser storage.
- Browser reuse is explicit opt-in under the versioned key `ocpp.invoiceBuyer.v1`.
- Keep the post-checkout request endpoint for legacy clients, including its immutability and submission guards.
- Do not add a database migration or alter payment, capture, refund, charging, or e-racuni configuration behavior.
- Keep all committed content public-safe and collaborator-neutral.

---

### Task 1: Validate and Persist Buyer Data During Checkout Creation

**Files:**
- Modify: `OCPP.Core.Server/Payments/IPaymentCoordinator.cs`
- Modify: `OCPP.Core.Server/Payments/InvoiceBuyerData.cs`
- Modify: `OCPP.Core.Server/Payments/StripePaymentCoordinator.cs`
- Modify: `OCPP.Core.Server/OCPPMiddleware.cs`
- Test: `OCPP.Core.Server.Tests/InvoiceBuyerDataValidatorTests.cs`
- Test: `OCPP.Core.Server.Tests/StripePaymentCoordinatorTests.cs`

**Interfaces:**
- Consumes: `PaymentSessionRequest.RequestR1Invoice` and complete buyer fields.
- Produces: `InvoiceBuyerDataValidator.ValidateAndNormalize(PaymentSessionRequest)`, a confirmed reservation snapshot, `invoice_type=R1` compatibility metadata, and bounded `InvoiceBuyerValidationException` responses before any Stripe call.

- [ ] **Step 1: Write failing validator and coordinator tests**

Add a validator test showing that a foreign `PaymentSessionRequest` normalizes through the same rules as the legacy request. Replace `CreateCheckoutSession_DoesNotTreatUnreviewedR1IntentAsConfirmedBuyerData` with tests that prove unconfirmed requests fail before Stripe and confirmed requests persist the snapshot:

```csharp
[Fact]
public void ValidateAndNormalize_PaymentSessionRequest_UsesForeignBuyerContract()
{
    var result = InvoiceBuyerDataValidator.ValidateAndNormalize(new PaymentSessionRequest
    {
        RequestR1Invoice = true,
        BuyerCountry = "cz",
        BuyerCompanyName = " Example s.r.o. ",
        BuyerStreet = " Pražská 1 ",
        BuyerPostalCode = "110 00",
        BuyerCity = "Praha",
        BuyerEmail = "billing@example.cz",
        BuyerTaxIdentifier = "CZ 123-ABC",
        BuyerRegistrationNumber = "C 12345",
        BuyerIdentifierIsVatRegistration = true,
        BuyerDataConfirmed = true
    });

    Assert.True(result.Success);
    Assert.Equal("CZ", result.Data.Country);
    Assert.Equal("Example s.r.o.", result.Data.CompanyName);
}

[Fact]
public void CreateCheckoutSession_RejectsUnconfirmedR1BeforeCallingStripe()
{
    using var context = CreatePricedContext("CP-R1");
    var sessionService = new FakeSessionService();
    var coordinator = CreateCoordinator(context, sessionService, new FakePaymentIntentService());

    var error = Assert.Throws<InvoiceBuyerValidationException>(() => coordinator.CreateCheckoutSession(
        context,
        new PaymentSessionRequest
        {
            ChargePointId = "CP-R1",
            ConnectorId = 1,
            ChargeTagId = "TAG-R1",
            RequestR1Invoice = true,
            BuyerCountry = "HR",
            BuyerTaxIdentifier = "12345678903",
            BuyerDataConfirmed = false
        }));

    Assert.Equal("ConfirmationRequired", error.Status);
    Assert.Null(sessionService.LastCreateOptions);
    Assert.Empty(context.ChargePaymentReservations);
}

[Fact]
public void CreateCheckoutSession_PersistsConfirmedForeignBuyerAndMarksStripeR1()
{
    using var context = CreatePricedContext("CP-R1");
    var sessionService = new FakeSessionService
    {
        CreateResponse = new Session { Id = "sess_r1", Url = "https://checkout/r1", PaymentIntentId = "pi_r1" }
    };
    var coordinator = CreateCoordinator(context, sessionService, new FakePaymentIntentService());

    var result = coordinator.CreateCheckoutSession(context, CreateConfirmedForeignSessionRequest("CP-R1"));

    Assert.NotNull(result.Reservation.InvoiceBuyerConfirmedAtUtc);
    Assert.Equal("CZ", result.Reservation.InvoiceBuyerCountry);
    Assert.Equal("Example s.r.o.", result.Reservation.InvoiceBuyerCompanyName);
    Assert.Equal("CZ 123-ABC", result.Reservation.InvoiceBuyerTaxIdentifier);
    Assert.Equal("R1", sessionService.LastCreateOptions.Metadata["invoice_type"]);
    Assert.Equal("CZ", sessionService.LastCreateOptions.Metadata["buyer_country"]);
    Assert.Equal("CZ 123-ABC", sessionService.LastCreateOptions.PaymentIntentData.Metadata["buyer_tax_identifier"]);
    Assert.False(sessionService.LastCreateOptions.Metadata.ContainsKey("invoice_review_requested"));
}

private static OCPPCoreContext CreatePricedContext(string chargePointId)
{
    var context = CreateContext();
    context.ChargePoints.Add(new ChargePoint
    {
        ChargePointId = chargePointId,
        MaxSessionKwh = 1,
        PricePerKwh = 1m
    });
    context.SaveChanges();
    return context;
}

private static PaymentSessionRequest CreateConfirmedForeignSessionRequest(string chargePointId) => new PaymentSessionRequest
{
    ChargePointId = chargePointId,
    ConnectorId = 1,
    ChargeTagId = "TAG-R1",
    RequestR1Invoice = true,
    BuyerCountry = "CZ",
    BuyerCompanyName = "Example s.r.o.",
    BuyerStreet = "Pražská 1",
    BuyerPostalCode = "110 00",
    BuyerCity = "Praha",
    BuyerEmail = "billing@example.cz",
    BuyerTaxIdentifier = "CZ 123-ABC",
    BuyerRegistrationNumber = "C 12345",
    BuyerIdentifierIsVatRegistration = true,
    BuyerDataConfirmed = true
};
```

- [ ] **Step 2: Run focused tests and verify RED**

```bash
dotnet test OCPP.Core.Server.Tests/OCPP.Core.Server.Tests.csproj --filter "FullyQualifiedName~InvoiceBuyerDataValidatorTests|FullyQualifiedName~StripePaymentCoordinatorTests.CreateCheckoutSession" --no-restore
```

Expected: compilation/test failures because `PaymentSessionRequest` lacks the full contract, its validator overload and `InvoiceBuyerValidationException` do not exist, and checkout still treats R1 as post-checkout intent.

- [ ] **Step 3: Extend the request contract and validator**

Add the complete buyer properties to `PaymentSessionRequest`, including `BuyerCountry`, address, email, identifier, registration number, VAT-registration flag, and `BuyerDataConfirmed`. Add this public bounded exception and overload in `InvoiceBuyerData.cs`:

```csharp
public sealed class InvoiceBuyerValidationException : ArgumentException
{
    public InvoiceBuyerValidationException(string status, string field, string message) : base(message)
    {
        Status = status;
        Field = field;
    }

    public string Status { get; }
    public string Field { get; }
}

public static InvoiceBuyerDataValidationResult ValidateAndNormalize(PaymentSessionRequest request) =>
    ValidateAndNormalize(request == null ? null : new PaymentR1InvoiceRequest
    {
        BuyerCompanyName = request.BuyerCompanyName,
        BuyerOib = request.BuyerOib,
        BuyerCountry = request.BuyerCountry,
        BuyerStreet = request.BuyerStreet,
        BuyerPostalCode = request.BuyerPostalCode,
        BuyerCity = request.BuyerCity,
        BuyerEmail = request.BuyerEmail,
        BuyerTaxIdentifier = request.BuyerTaxIdentifier,
        BuyerRegistrationNumber = request.BuyerRegistrationNumber,
        BuyerIdentifierIsVatRegistration = request.BuyerIdentifierIsVatRegistration,
        BuyerDataConfirmed = request.BuyerDataConfirmed
    });
```

- [ ] **Step 4: Apply the confirmed snapshot before Stripe option creation**

At the beginning of `CreateCheckoutSession`, validate only when `RequestR1Invoice` is true. Throw `InvoiceBuyerValidationException` with the safe status, field, and error. After constructing the reservation, call the existing `ApplyConfirmedBuyer` helper. Replace `invoice_review_requested` with the same bounded metadata mirror used by `RequestR1Invoice`:

```csharp
if (invoiceBuyer != null)
{
    ApplyConfirmedBuyer(reservation, invoiceBuyer, now);
    metadata["invoice_type"] = "R1";
    SetOrRemoveMetadata(metadata, "buyer_oib", invoiceBuyer.Country == "HR" ? TrimMetadataValue(invoiceBuyer.TaxIdentifier, 32) : null);
    SetOrRemoveMetadata(metadata, "buyer_country", invoiceBuyer.Country);
    SetOrRemoveMetadata(metadata, "buyer_tax_identifier", TrimMetadataValue(invoiceBuyer.TaxIdentifier, 64));
    SetOrRemoveMetadata(metadata, "buyer_company", TrimMetadataValue(invoiceBuyer.CompanyName, 200));
}
```

- [ ] **Step 5: Return bounded validation JSON from `Payments/Create`**

Add a dedicated middleware catch before the generic catch:

```csharp
catch (InvoiceBuyerValidationException validation)
{
    context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
    context.Response.ContentType = "application/json";
    await context.Response.WriteAsync(JsonConvert.SerializeObject(new
    {
        status = validation.Status,
        field = validation.Field,
        message = validation.Message
    }));
}
```

- [ ] **Step 6: Run the focused tests and verify GREEN**

Run the Step 2 command. Expected: all selected validator and checkout-creation tests pass.

- [ ] **Step 7: Commit the server slice**

```bash
git add OCPP.Core.Server/Payments/IPaymentCoordinator.cs OCPP.Core.Server/Payments/InvoiceBuyerData.cs OCPP.Core.Server/Payments/StripePaymentCoordinator.cs OCPP.Core.Server/OCPPMiddleware.cs OCPP.Core.Server.Tests/InvoiceBuyerDataValidatorTests.cs OCPP.Core.Server.Tests/StripePaymentCoordinatorTests.cs
git commit -m "fix: confirm invoice buyer before Stripe checkout"
```

### Task 2: Move the Complete Buyer Form to the Start Page

**Files:**
- Modify: `OCPP.Core.Management/Models/PublicStartViewModel.cs`
- Modify: `OCPP.Core.Management/Controllers/PublicController.cs`
- Modify: `OCPP.Core.Management/Views/Public/Start.cshtml`
- Create: `OCPP.Core.Server.Tests/PublicStartInvoiceViewTests.cs`
- Test: `OCPP.Core.Server.Tests/PublicControllerTests.cs`

**Interfaces:**
- Consumes: the extended `PaymentSessionRequest` JSON contract and existing country-aware validation rules.
- Produces: a pre-checkout form that preserves errors and forwards every confirmed buyer field to `Payments/Create`.

- [ ] **Step 1: Write failing start-page contract tests**

Create `PublicStartInvoiceViewTests.cs` to assert the view contains named inputs `BuyerCountry`, `BuyerCompanyName`, `BuyerStreet`, `BuyerPostalCode`, `BuyerCity`, `BuyerEmail`, `BuyerTaxIdentifier`, `BuyerRegistrationNumber`, `BuyerIdentifierIsVatRegistration`, `BuyerDataConfirmed`, and `RememberInvoiceBuyer`, plus the explicit shared-device warning. Assert the old copy saying details are collected after checkout is absent.

Add a `PublicControllerTests` POST test using `TestHttpServer` that captures `/Payments/Create` and asserts the body contains the complete confirmed payload, including `"buyerCountry":"CZ"`, `"buyerTaxIdentifier":"CZ 123-ABC"`, and `"buyerDataConfirmed":true`.

- [ ] **Step 2: Run the new tests and verify RED**

```bash
dotnet test OCPP.Core.Server.Tests/OCPP.Core.Server.Tests.csproj --filter "FullyQualifiedName~PublicStartInvoiceViewTests|FullyQualifiedName~PublicControllerTests.Start_PostForwardsConfirmedInvoiceBuyer" --no-restore
```

Expected: failure because the view model, controller payload, and pre-checkout controls are incomplete.

- [ ] **Step 3: Extend and preserve the view model**

Add the complete buyer fields plus `RememberInvoiceBuyer` to `PublicStartViewModel`. In `PublicController.Start(PublicStartViewModel request)`, copy submitted values to the rebuilt model instead of clearing them, and add all buyer fields plus `buyerDataConfirmed` to the anonymous `Payments/Create` request.

- [ ] **Step 4: Render the country-aware form before checkout**

Move the country selector and buyer fields from `Views/Payments/PublicStatus.cshtml` into the existing `r1Fields` block in `Start.cshtml`, using names matching the view model. Keep the Croatian/foreign labels, max lengths, review summary, confirmation checkbox, VAT-registration flag, and optional registration number. Add an opt-in `Remember company details on this device` checkbox and a warning that other users of a shared device may see saved details. Disable all buyer controls when company invoicing is not selected; keep server validation authoritative.

- [ ] **Step 5: Run the focused tests and verify GREEN**

Run the Step 2 command. Expected: all selected start-page and controller tests pass.

- [ ] **Step 6: Commit the management slice**

```bash
git add OCPP.Core.Management/Models/PublicStartViewModel.cs OCPP.Core.Management/Controllers/PublicController.cs OCPP.Core.Management/Views/Public/Start.cshtml OCPP.Core.Server.Tests/PublicStartInvoiceViewTests.cs OCPP.Core.Server.Tests/PublicControllerTests.cs
git commit -m "feat: collect invoice buyer before checkout"
```

### Task 3: Add Opt-in Browser Reuse and Remove Late-entry UI

**Files:**
- Create: `OCPP.Core.Management/wwwroot/js/invoice-buyer-storage.js`
- Create: `Simulators/tests/invoice-buyer-storage.test.mjs`
- Modify: `OCPP.Core.Management/Views/Public/Start.cshtml`
- Modify: `OCPP.Core.Management/Views/Payments/PublicStatus.cshtml`
- Modify: `OCPP.Core.Server.Tests/PublicStatusInvoiceViewTests.cs`

**Interfaces:**
- Consumes: named start-page buyer controls and browser `localStorage`.
- Produces: `globalThis.invoiceBuyerStorage` with `load(storage)`, `save(storage, details)`, and `clear(storage)`; no editable post-checkout buyer form for new sessions.

- [ ] **Step 1: Write failing storage and status-view tests**

Create a Node test that imports the browser helper and supplies an in-memory storage adapter. Assert that a record containing `buyerCountry`, `buyerCompanyName`, and a forbidden `reservationId` stores only the two whitelisted buyer fields under `ocpp.invoiceBuyer.v1`; add cases for clear, malformed JSON, and unavailable storage. Update `PublicStatusInvoiceViewTests` to assert the status view no longer contains `id="r1-submit"`, `submitR1Details`, or copy promising details can be added later, while invoice result messaging remains.

- [ ] **Step 2: Run tests and verify RED**

```bash
node --test Simulators/tests/invoice-buyer-storage.test.mjs
dotnet test OCPP.Core.Server.Tests/OCPP.Core.Server.Tests.csproj --filter FullyQualifiedName~PublicStatusInvoiceViewTests --no-restore
```

Expected: the module is missing and the current status page still exposes late entry.

- [ ] **Step 3: Implement the storage helper**

Create an IIFE that assigns a frozen API to `globalThis.invoiceBuyerStorage`. Use the exact key `ocpp.invoiceBuyer.v1`, whitelist only buyer field names, trim string values, preserve the VAT boolean, catch all storage/JSON errors, and return `null` for invalid records. Do not include reservation or payment fields.

- [ ] **Step 4: Wire opt-in reuse on the start page**

Load the helper through the view's `Scripts` section. On page load, populate empty buyer controls from `invoiceBuyerStorage.load(localStorage)` and select `RememberInvoiceBuyer` when data exists. On form submission, save the current buyer fields only when both company invoicing and remember are selected; otherwise call `clear(localStorage)`. Storage failures must not block checkout.

- [ ] **Step 5: Remove late-entry controls from the status page**

Delete the editable `r1-panel`, its element bindings, review builder, and `submitR1Details` fetch flow from `PublicStatus.cshtml`. Retain invoice outcome rendering and customer-safe provider messages. Do not remove `PaymentsController.RequestR1Invoice` or the server `Payments/RequestR1` endpoint because older clients still use them.

- [ ] **Step 6: Run focused tests and verify GREEN**

```bash
node --test Simulators/tests/invoice-buyer-storage.test.mjs
dotnet test OCPP.Core.Server.Tests/OCPP.Core.Server.Tests.csproj --filter FullyQualifiedName~PublicStatusInvoiceViewTests --no-restore
node --check OCPP.Core.Management/wwwroot/js/invoice-buyer-storage.js
```

Expected: Node storage tests, status source-contract tests, and JavaScript syntax checks pass.

- [ ] **Step 7: Commit the browser/status slice**

```bash
git add OCPP.Core.Management/wwwroot/js/invoice-buyer-storage.js Simulators/tests/invoice-buyer-storage.test.mjs OCPP.Core.Management/Views/Public/Start.cshtml OCPP.Core.Management/Views/Payments/PublicStatus.cshtml OCPP.Core.Server.Tests/PublicStatusInvoiceViewTests.cs
git commit -m "feat: remember invoice buyer details on device"
```

### Task 4: End-to-end Regression, Documentation, and Full Verification

**Files:**
- Modify: `Simulators/playwright/tests/support/session_helpers.mjs`
- Modify: `Simulators/playwright/tests/public-urgent-validation.spec.js`
- Modify: `docs/features.md`
- Modify: `docs/operations.md`

**Interfaces:**
- Consumes: pre-checkout form IDs, persisted snapshot, and R1 metadata.
- Produces: browser regression proof that invoice classification is established before charging and maintained through completion.

- [ ] **Step 1: Update the invoice browser regression**

Extend `startPublicSession` to fill country, legal name, address, email, identifier, VAT flag, and confirmation before clicking Start. Replace the separate status-page R1 submission test with a localStorage reuse test that starts one company-invoice form with remember enabled, revisits another start page, and verifies the form is pre-populated before checkout.

- [ ] **Step 2: Update public documentation**

In `docs/features.md`, describe pre-checkout confirmation, reservation ownership, minimal Stripe mirroring, and opt-in browser reuse. In `docs/operations.md`, state that new sessions never depend on a post-checkout buyer update and that the legacy endpoint remains only for compatibility.

- [ ] **Step 3: Run focused verification**

```bash
dotnet test OCPP.Core.Server.Tests/OCPP.Core.Server.Tests.csproj --filter "FullyQualifiedName~InvoiceBuyerDataValidatorTests|FullyQualifiedName~StripePaymentCoordinatorTests|FullyQualifiedName~PublicStartInvoiceViewTests|FullyQualifiedName~PublicStatusInvoiceViewTests|FullyQualifiedName~PublicControllerTests" --no-restore
node --test Simulators/tests/invoice-buyer-storage.test.mjs
```

Expected: all selected tests pass.

- [ ] **Step 4: Run full verification**

```bash
dotnet build OCPP.Core.sln -c Release --no-restore
dotnet test OCPP.Core.Server.Tests/OCPP.Core.Server.Tests.csproj -c Release --no-build
bash ./scripts/check-mssql-migration-metadata.sh
node --check OCPP.Core.Management/wwwroot/js/invoice-buyer-storage.js
node --check OCPP.Core.Management/wwwroot/js/public-portal.js
git diff --check origin/main...HEAD
```

Expected: Release build passes, all xUnit tests pass, migration metadata is valid, both JavaScript files parse, and the branch diff has no whitespace errors.

- [ ] **Step 5: Run the invoice browser regression when the local stack is available**

```bash
OCPP_PLAYWRIGHT_ENABLE_INVOICES=1 npx --prefix Simulators/playwright playwright test tests/public-urgent-validation.spec.js --grep @invoice
```

Expected: the pre-checkout R1 flow and device-local reuse test pass. If the required mock invoice stack is unavailable, record that exact limitation without weakening unit/source-contract verification.

- [ ] **Step 6: Commit documentation and regression updates**

```bash
git add Simulators/playwright/tests/support/session_helpers.mjs Simulators/playwright/tests/public-urgent-validation.spec.js docs/features.md docs/operations.md
git commit -m "test: cover pre-checkout company invoices"
```

- [ ] **Step 7: Review final branch state**

```bash
git status --short --branch
git log --oneline --decorate origin/main..HEAD
git diff --stat origin/main...HEAD
```

Expected: the worktree is clean and the branch contains the design, implementation, regression, and documentation commits only.
