# Foreign VAT Validation and VIES Verification Implementation Plan

> **For maintainers:** Execute this plan in order with the `superpowers:executing-plans` workflow. Keep every external VIES call behind a fake in automated tests.

**Goal:** Normalize structurally valid foreign EU VAT identifiers before checkout, optionally verify them against the European Commission VIES service without blocking payment, and retain auditable validation evidence with the immutable invoice-buyer snapshot.

**Architecture:** Keep Croatian OIB checksum validation and generic non-VAT identifiers unchanged. For foreign identifiers explicitly marked as VAT registrations, a project-owned validator removes presentation separators, enforces the selected-country prefix and the public EC format, and produces the canonical VAT identifier used by Stripe metadata and invoice drafting. An async VIES boundary calls the official REST endpoint after local validation; `Valid`, `Invalid`, and `Unavailable` are persisted on the reservation, while all three outcomes remain non-blocking. The public status page explains `Invalid` and `Unavailable` outcomes after checkout.

**Tech Stack:** .NET 8, ASP.NET Core, Entity Framework Core, `HttpClient`, xUnit, Stripe.net, Razor/JavaScript.

---

## Task 1: Lock down local validation behavior

**Files:**

- Modify: `OCPP.Core.Server.Tests/InvoiceBuyerDataValidatorTests.cs`
- Create: `OCPP.Core.Server/Payments/ForeignVatNumberValidator.cs`
- Modify: `OCPP.Core.Server/Payments/InvoiceBuyerData.cs`

1. Add table-driven tests for canonical normalization:

```csharp
[Theory]
[InlineData("DE", " de 123.456.789 ", "DE123456789")]
[InlineData("CZ", "CZ-12345678", "CZ12345678")]
[InlineData("SI", "si 12345678", "SI12345678")]
[InlineData("SK", "SK 1234567890", "SK1234567890")]
[InlineData("GR", "el 123456789", "EL123456789")]
[InlineData("GB", "xi 123456789", "XI123456789")]
public void ValidateAndNormalize_NormalizesForeignVatNumbers(
    string country,
    string input,
    string expected)
{
    var result = InvoiceBuyerDataValidator.ValidateAndNormalize(
        CompleteForeignBuyer(country, input, isVat: true));

    Assert.True(result.Success);
    Assert.Equal(input.Trim(), result.Data.OriginalTaxIdentifier);
    Assert.Equal(expected, result.Data.NormalizedVatIdentifier);
    Assert.Equal(expected, result.Data.TaxIdentifier);
}
```

2. Add failures for country-prefix mismatch, unsupported prefix, bad characters, and country-specific length/shape.
3. Add regression tests proving Croatian OIB stays an 11-digit OIB and an unmarked foreign tax identifier remains byte-for-byte equivalent after surrounding trim.
4. Run the tests and confirm the new assertions fail before production changes:

```bash
LANG=en_US.UTF-8 LC_ALL=en_US.UTF-8 DOTNET_ROLL_FORWARD=Major \
  dotnet test OCPP.Core.Server.Tests/OCPP.Core.Server.Tests.csproj \
  --filter InvoiceBuyerDataValidatorTests --no-restore --nologo
```

5. Implement `ForeignVatNumberValidator` with an explicit selected-country-to-VIES-prefix table and EC public format regular expressions.
6. Extend `InvoiceBuyerData` with `OriginalTaxIdentifier`, `NormalizedVatIdentifier`, and the VIES country code.
7. Run the focused tests and confirm they pass.

## Task 2: Build the bounded VIES adapter

**Files:**

- Create: `OCPP.Core.Server.Tests/ViesVerificationServiceTests.cs`
- Create: `OCPP.Core.Server/Payments/ViesVerificationService.cs`
- Modify: `OCPP.Core.Server/Startup.cs`

1. Add fake-`HttpMessageHandler` tests for:

```csharp
[Fact]
public async Task VerifyAsync_ReturnsValid_WithBoundedReference()
{
    var service = CreateService(HttpStatusCode.OK,
        """{"countryCode":"DE","vatNumber":"123456789","valid":true,"requestIdentifier":"abc"}""");

    var result = await service.VerifyAsync("DE", "123456789", CancellationToken.None);

    Assert.Equal(ViesVerificationStatus.Valid, result.Status);
    Assert.Equal("abc", result.Reference);
    Assert.NotNull(result.CheckedAtUtc);
}
```

2. Add cases for `valid=false`, HTTP 500 member-state unavailability, request timeout, malformed success JSON, and caller cancellation.
3. Run the focused test class and confirm it fails before implementation.
4. Implement:

```csharp
public interface IViesVerificationService
{
    Task<ViesVerificationResult> VerifyAsync(
        string countryCode,
        string vatNumber,
        CancellationToken cancellationToken);
}
```

5. Use a named `HttpClient` with the official EC VIES REST base address and a short configured timeout. Never send buyer names, addresses, or requester identifiers.
6. Treat network, timeout, malformed-response, and non-success responses as `Unavailable`; preserve caller cancellation.
7. Register the adapter in `Startup` and rerun the focused tests.

## Task 3: Persist evidence and integrate before Stripe

**Files:**

- Modify: `OCPP.Core.Database/ChargePaymentReservation.cs`
- Modify: `OCPP.Core.Database/OCPPCoreContext.cs`
- Create: `OCPP.Core.Database/Migrations/<timestamp>_AddInvoiceBuyerVatValidation.cs`
- Create: `OCPP.Core.Database/Migrations/<timestamp>_AddInvoiceBuyerVatValidation.Designer.cs`
- Modify: `OCPP.Core.Database/Migrations/OCPPCoreContextModelSnapshot.cs`
- Modify: `OCPP.Core.Server/Payments/IPaymentCoordinator.cs`
- Modify: `OCPP.Core.Server/Payments/StripePaymentCoordinator.cs`
- Modify: `OCPP.Core.Server/OCPPMiddleware.cs`
- Modify: `OCPP.Core.Server/Startup.cs`
- Modify: `OCPP.Core.Server.Tests/StripePaymentCoordinatorTests.cs`
- Modify: `OCPP.Core.Server.Tests/InvoiceDraftBuilderTests.cs`

1. Add reservation columns for original identifier, normalized VAT identifier, local VAT validation status, VIES status, VIES checked time, and bounded VIES reference.
2. Add focused coordinator tests proving:

```csharp
Assert.False(viesService.Called); // locally invalid or not marked as VAT
Assert.False(sessionService.CreateCalled); // locally invalid
Assert.Equal("DE123456789", result.Reservation.InvoiceBuyerTaxIdentifier);
Assert.Equal("DE 123.456.789", result.Reservation.InvoiceBuyerOriginalTaxIdentifier);
Assert.Equal(ViesVerificationStatus.Invalid, result.Reservation.InvoiceBuyerVatVerificationStatus);
Assert.True(sessionService.CreateCalled); // remote Invalid is non-blocking
```

3. Add an invoice-draft test proving the normalized identifier is passed through to eRacuni-facing draft data.
4. Add a default async method to `IPaymentCoordinator` so existing fake coordinators retain compatibility:

```csharp
Task<PaymentSessionResult> CreateCheckoutSessionAsync(
    OCPPCoreContext dbContext,
    PaymentSessionRequest request,
    CancellationToken cancellationToken) =>
    Task.FromResult(CreateCheckoutSession(dbContext, request));
```

5. Move checkout work into the async coordinator method. Run local validation first, call VIES only for locally valid foreign VAT identifiers, apply the evidence to the reservation, then create the Stripe session.
6. Keep the synchronous entry point as a compatibility wrapper. Change middleware to await the async method with `HttpContext.RequestAborted`.
7. Generate and inspect the EF migration:

```bash
DOTNET_ROLL_FORWARD=Major dotnet ef migrations add AddInvoiceBuyerVatValidation \
  --project OCPP.Core.Database/OCPP.Core.Database.csproj \
  --startup-project OCPP.Core.Server/OCPP.Core.Server.csproj
```

8. Run validator, VIES, coordinator, invoice-draft, and eRacuni request-factory tests.

## Task 4: Explain outcomes in the public flow

**Files:**

- Modify: `OCPP.Core.Server/OCPPMiddleware.cs`
- Modify: `OCPP.Core.Management/Views/Public/Start.cshtml`
- Modify: `OCPP.Core.Management/Views/Payments/PublicStatus.cshtml`
- Modify: `OCPP.Core.Management/wwwroot/js/public-portal.js`
- Modify: `OCPP.Core.Server.Tests/PublicStatusInvoiceViewTests.cs`
- Modify: `Simulators/playwright/tests/public-status.spec.js`

1. Add Northern Ireland as `GB` in the country selector while explaining that its VAT prefix is `XI`.
2. Replace the old “foreign identifiers are unverified” help with copy that distinguishes local format validation, optional VIES verification, and non-VAT identifiers.
3. Return the persisted VAT validation and VIES fields in `Payments/Status`.
4. Add a hidden accessible alert to the status page and render:

```javascript
vatAlert.hidden = status !== "Invalid" && status !== "Unavailable";
vatAlert.classList.toggle("warning", status === "Invalid");
vatAlertText.textContent = status === "Invalid"
  ? t("status.vat.invalid")
  : t("status.vat.unavailable");
```

5. Add equivalent copy for Croatian, English, Slovenian, Italian, German, and French.
6. Add view-contract and browser tests for both messages, then run them without any live VIES call.

## Task 5: Record the decision and verify the full change

**Files:**

- Create: `docs/decisions/0002-foreign-vat-validation-and-vies.md`
- Modify: `docs/features.md`
- Modify: `docs/operations.md`
- Modify: `docs/local-company-invoice-demo.md`

1. Document the local-validation/VIES boundary, the non-blocking tri-state behavior, evidence retention, and the no-PII request contract.
2. Record why `vies-dotnet-api` 3.1.0 was evaluated but not adopted: its checksum logic exceeds the EC-published structural contract and it adds a transitive polyfill dependency for a small REST integration.
3. Link the official EC VAT formats, VIES FAQ, REST Swagger, and NuGet package page.
4. Run:

```bash
LANG=en_US.UTF-8 LC_ALL=en_US.UTF-8 DOTNET_ROLL_FORWARD=Major \
  dotnet test OCPP.Core.Server.Tests/OCPP.Core.Server.Tests.csproj \
  --no-restore --nologo
```

5. Run the relevant Playwright spec against the local management/server setup if the repository harness supports it; otherwise report the exact gap without claiming browser evidence.
6. Review `git diff --check`, the migration, public-safe documentation, and `git status`.
7. Commit, push the isolated branch, open a draft pull request, and add `codex` plus `codex-automation` labels when those labels exist.
