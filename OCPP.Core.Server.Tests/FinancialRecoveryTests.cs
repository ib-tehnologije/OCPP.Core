using System;
using Microsoft.EntityFrameworkCore;
using OCPP.Core.Database;
using OCPP.Core.Server.Payments;
using OCPP.Core.Server.Payments.Recovery;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    public class FinancialRecoveryManifestTests
    {
        [Fact]
        public void Parse_RejectsDuplicateReservationOperation()
        {
            const string json = """
                {
                  "schemaVersion": 1,
                  "entries": [
                    { "operation": "recover-settlement", "reservationId": "11111111-1111-1111-1111-111111111111" },
                    { "operation": "recover-settlement", "reservationId": "11111111-1111-1111-1111-111111111111" }
                  ]
                }
                """;

            var error = Assert.Throws<InvalidOperationException>(() => FinancialRecoveryManifest.Parse(json));

            Assert.Contains("duplicate", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void RequireExecutionConfirmation_RequiresExactDigest()
        {
            const string json = """
                {
                  "schemaVersion": 1,
                  "entries": [
                    { "operation": "recover-invoice", "reservationId": "22222222-2222-2222-2222-222222222222" }
                  ]
                }
                """;
            var manifest = FinancialRecoveryManifest.Parse(json);

            Assert.Throws<InvalidOperationException>(() => manifest.RequireExecutionConfirmation(execute: true, "wrong"));
            manifest.RequireExecutionConfirmation(execute: true, manifest.Sha256);
        }

        [Fact]
        public void RequireExecutionConfirmation_DryRunNeedsNoDigest()
        {
            const string json = """
                { "schemaVersion": 1, "entries": [] }
                """;

            FinancialRecoveryManifest.Parse(json).RequireExecutionConfirmation(execute: false, confirmationSha256: null);
        }
    }

    public class FinancialRecoverySettlementTests
    {
        [Fact]
        public void Assess_DerivesBillableValuesFromPersistedEvidence()
        {
            var reservation = CreateReservation();
            var transaction = CreateTransaction();

            var result = FinancialRecoverySettlementAssessor.Assess(reservation, transaction);

            Assert.True(result.Eligible, result.Reason);
            Assert.Equal(4.25d, result.EnergyKwh);
            Assert.Equal(128L, result.EnergyCostCents);
            Assert.Equal(50L, result.SessionFeeCents);
            Assert.Equal(40L, result.UsageFeeCents);
            Assert.Equal(20L, result.IdleFeeCents);
            Assert.Equal(238L, result.TotalCents);
        }

        [Theory]
        [InlineData(null, 14250d, "meter stop")]
        [InlineData(8999d, 9000d, "below meter start")]
        public void Assess_FailsClosed_WhenMeterEvidenceIsMissingOrContradictory(
            double? meterStop,
            double meterStart,
            string reason)
        {
            var reservation = CreateReservation();
            var transaction = CreateTransaction();
            transaction.MeterStart = meterStart;
            transaction.MeterStop = meterStop;

            var result = FinancialRecoverySettlementAssessor.Assess(reservation, transaction);

            Assert.False(result.Eligible);
            Assert.Contains(reason, result.Reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Assess_FailsClosed_WhenReservationLinksAnotherTransaction()
        {
            var reservation = CreateReservation();
            reservation.TransactionId = 999;

            var result = FinancialRecoverySettlementAssessor.Assess(reservation, CreateTransaction());

            Assert.False(result.Eligible);
            Assert.Contains("transaction link", result.Reason, StringComparison.OrdinalIgnoreCase);
        }

        private static ChargePaymentReservation CreateReservation() => new()
        {
            ReservationId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            TransactionId = 42,
            Status = PaymentReservationStatus.Failed,
            PricePerKwh = 0.30m,
            UserSessionFee = 0.50m,
            UsageFeePerMinute = 0.10m,
            Currency = "EUR",
            StripePaymentIntentId = "pi_synthetic"
        };

        private static Transaction CreateTransaction() => new()
        {
            TransactionId = 42,
            StartTime = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc),
            StopTime = new DateTime(2026, 1, 1, 11, 0, 0, DateTimeKind.Utc),
            MeterStart = 14250,
            MeterStop = 14254.25,
            UsageFeeMinutes = 4,
            IdleUsageFeeAmount = 0.20m
        };
    }

    public class FinancialRecoveryAuthorizationTests
    {
        [Fact]
        public void Assess_AllowsOnlyUnusedTerminalReservation()
        {
            var reservation = CreateReservation();

            var result = FinancialRecoveryAuthorizationAssessor.Assess(
                reservation,
                hasTransaction: false,
                hasInvoiceEvidence: false);

            Assert.True(result.Eligible, result.Reason);
        }

        [Theory]
        [InlineData(true, false, "transaction")]
        [InlineData(false, true, "invoice")]
        public void Assess_RejectsLinkedOrInvoicedReservation(bool hasTransaction, bool hasInvoice, string reason)
        {
            var result = FinancialRecoveryAuthorizationAssessor.Assess(
                CreateReservation(),
                hasTransaction,
                hasInvoice);

            Assert.False(result.Eligible);
            Assert.Contains(reason, result.Reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Assess_RejectsAnyPersistedConsumption()
        {
            var reservation = CreateReservation();
            reservation.ActualEnergyKwh = 0.01;

            var result = FinancialRecoveryAuthorizationAssessor.Assess(
                reservation,
                hasTransaction: false,
                hasInvoiceEvidence: false);

            Assert.False(result.Eligible);
            Assert.Contains("energy", result.Reason, StringComparison.OrdinalIgnoreCase);
        }

        private static ChargePaymentReservation CreateReservation() => new()
        {
            ReservationId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
            Status = PaymentReservationStatus.Abandoned,
            StripePaymentIntentId = "pi_synthetic",
            CapturedAmountCents = 0,
            ActualEnergyKwh = 0
        };
    }

    public class FinancialRecoveryServiceTests
    {
        [Fact]
        public void Run_DryRunDoesNotArmAuthorizationRelease()
        {
            using var dbContext = CreateContext();
            var reservation = new ChargePaymentReservation
            {
                ReservationId = Guid.Parse("55555555-5555-5555-5555-555555555555"),
                ChargePointId = "CP-SYNTHETIC",
                ChargeTagId = "TAG-SYNTHETIC",
                Currency = "EUR",
                Status = PaymentReservationStatus.Abandoned,
                StripePaymentIntentId = "pi_synthetic",
                CapturedAmountCents = 0,
                ActualEnergyKwh = 0
            };
            dbContext.ChargePaymentReservations.Add(reservation);
            dbContext.SaveChanges();
            var manifest = FinancialRecoveryManifest.Parse("""
                {
                  "schemaVersion": 1,
                  "entries": [
                    { "operation": "release-authorization", "reservationId": "55555555-5555-5555-5555-555555555555" }
                  ]
                }
                """);
            var service = new FinancialRecoveryService(paymentCoordinator: null, invoiceIntegrationService: null);

            var report = service.Run(dbContext, manifest, execute: false, confirmationSha256: null);

            Assert.True(Assert.Single(report.Items).Eligible);
            Assert.Null(reservation.AuthorizationReleaseState);
            Assert.Empty(dbContext.PaymentAuthorizationReleaseAttempts);
        }

        private static OCPPCoreContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<OCPPCoreContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            return new OCPPCoreContext(options);
        }
    }
}
