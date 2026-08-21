using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using OCPP.Core.Database;
using OCPP.Core.Server.Payments;
using OCPP.Core.Server.Payments.Invoices;
using OCPP.Core.Server.Payments.Recovery;
using Stripe.Checkout;
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
            Assert.Equal(0L, result.IdleFeeCents);
            Assert.Equal(218L, result.TotalCents);
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

        [Fact]
        public void Assess_FailsClosed_WhenReservationIsNotTerminal()
        {
            var reservation = CreateReservation();
            reservation.Status = PaymentReservationStatus.Charging;

            var result = FinancialRecoverySettlementAssessor.Assess(reservation, CreateTransaction());

            Assert.False(result.Eligible);
            Assert.Contains("terminal", result.Reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Assess_FailsClosed_WhenDisconnectEvidenceIsMissing()
        {
            var reservation = CreateReservation();
            reservation.DisconnectedAtUtc = null;

            var result = FinancialRecoverySettlementAssessor.Assess(reservation, CreateTransaction());

            Assert.False(result.Eligible);
            Assert.Contains("disconnect", result.Reason, StringComparison.OrdinalIgnoreCase);
        }

        private static ChargePaymentReservation CreateReservation() => new()
        {
            ReservationId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            TransactionId = 42,
            Status = PaymentReservationStatus.Failed,
            PricePerKwh = 0.30m,
            UserSessionFee = 0.50m,
            UsageFeePerMinute = 0.10m,
            MaxUsageFeeMinutes = 4,
            Currency = "EUR",
            StripePaymentIntentId = "pi_synthetic",
            StopTransactionAtUtc = new DateTime(2026, 1, 1, 11, 0, 0, DateTimeKind.Utc),
            DisconnectedAtUtc = new DateTime(2026, 1, 1, 11, 10, 0, DateTimeKind.Utc)
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

    public class FinancialRecoveryInvoiceTests
    {
        [Fact]
        public void Assess_AllowsCompleteCapturedBillingBreakdown()
        {
            var result = FinancialRecoveryInvoiceAssessor.Assess(CreateReservation(), CreateTransaction());

            Assert.True(result.Eligible, result.Reason);
        }

        [Fact]
        public void Assess_RejectsCapturedAmountThatContradictsBillingBreakdown()
        {
            var reservation = CreateReservation();
            reservation.CapturedAmountCents = 299;

            var result = FinancialRecoveryInvoiceAssessor.Assess(reservation, CreateTransaction());

            Assert.False(result.Eligible);
            Assert.Contains("captured amount", result.Reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Assess_RejectsMissingPersistedCurrency()
        {
            var transaction = CreateTransaction();
            transaction.Currency = null;

            var result = FinancialRecoveryInvoiceAssessor.Assess(CreateReservation(), transaction);

            Assert.False(result.Eligible);
            Assert.Contains("currency", result.Reason, StringComparison.OrdinalIgnoreCase);
        }

        private static ChargePaymentReservation CreateReservation() => new()
        {
            ReservationId = Guid.Parse("45454545-4545-4545-4545-454545454545"),
            TransactionId = 45,
            Status = PaymentReservationStatus.Completed,
            CapturedAtUtc = new DateTime(2026, 1, 1, 11, 1, 0, DateTimeKind.Utc),
            CapturedAmountCents = 300,
            Currency = "EUR"
        };

        private static Transaction CreateTransaction() => new()
        {
            TransactionId = 45,
            StartTime = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc),
            StopTime = new DateTime(2026, 1, 1, 11, 0, 0, DateTimeKind.Utc),
            Currency = "EUR",
            EnergyKwh = 5,
            EnergyCost = 2.50m,
            UserSessionFeeAmount = 0.50m
        };
    }

    public class FinancialRecoveryServiceTests
    {
        [Fact]
        public void Run_ExecuteRecoverInvoicePreservesStoredCheckoutContext()
        {
            using var dbContext = CreateContext();
            var reservation = CreateInvoiceReservation();
            dbContext.ChargePaymentReservations.Add(reservation);
            dbContext.Transactions.Add(CreateInvoiceTransaction());
            dbContext.SaveChanges();
            var manifest = CreateInvoiceManifest(reservation.ReservationId);
            var sessions = new RecoverySessionService
            {
                GetResponse = new Session
                {
                    Id = reservation.StripeCheckoutSessionId,
                    PaymentIntentId = reservation.StripePaymentIntentId,
                    CustomerDetails = new SessionCustomerDetails
                    {
                        Email = "billing@example.test",
                        Name = "Synthetic Buyer"
                    }
                }
            };
            var invoiceIntegration = new ContextRecordingInvoiceIntegrationService();
            var service = CreateService(invoiceIntegration, sessions);

            var report = service.Run(dbContext, manifest, execute: true, manifest.Sha256);

            Assert.True(report.Succeeded);
            Assert.Equal(reservation.StripeCheckoutSessionId, sessions.LastGetId);
            Assert.Equal("Retail", invoiceIntegration.LastDraft?.InvoiceKind);
            Assert.Equal("billing@example.test", invoiceIntegration.LastDraft?.BuyerEmail);
            Assert.Equal("Synthetic Buyer", invoiceIntegration.LastDraft?.BuyerPersonalName);
        }

        [Theory]
        [InlineData("checkout-session")]
        [InlineData("payment-intent")]
        public void Run_ExecuteRecoverInvoiceBlocksMismatchedCheckoutContext(string mismatch)
        {
            using var dbContext = CreateContext();
            var reservation = CreateInvoiceReservation();
            dbContext.ChargePaymentReservations.Add(reservation);
            dbContext.Transactions.Add(CreateInvoiceTransaction());
            dbContext.SaveChanges();
            var manifest = CreateInvoiceManifest(reservation.ReservationId);
            var sessions = new RecoverySessionService
            {
                GetResponse = new Session
                {
                    Id = mismatch == "checkout-session" ? "cs_conflict" : reservation.StripeCheckoutSessionId,
                    PaymentIntentId = mismatch == "payment-intent" ? "pi_conflict" : reservation.StripePaymentIntentId
                }
            };
            var invoiceIntegration = new ContextRecordingInvoiceIntegrationService();
            var service = CreateService(invoiceIntegration, sessions);

            var report = service.Run(dbContext, manifest, execute: true, manifest.Sha256);

            var item = Assert.Single(report.Items);
            Assert.False(item.Eligible);
            Assert.Contains("checkout session", item.Outcome, StringComparison.OrdinalIgnoreCase);
            Assert.Null(invoiceIntegration.LastDraft);
        }

        [Fact]
        public void Run_ExecuteRecoverInvoiceBlocksCheckoutContextRetrievalFailure()
        {
            using var dbContext = CreateContext();
            var reservation = CreateInvoiceReservation();
            dbContext.ChargePaymentReservations.Add(reservation);
            dbContext.Transactions.Add(CreateInvoiceTransaction());
            dbContext.SaveChanges();
            var manifest = CreateInvoiceManifest(reservation.ReservationId);
            var sessions = new RecoverySessionService
            {
                GetException = new InvalidOperationException("synthetic Stripe retrieval failure")
            };
            var invoiceIntegration = new ContextRecordingInvoiceIntegrationService();
            var service = CreateService(invoiceIntegration, sessions);

            var report = service.Run(dbContext, manifest, execute: true, manifest.Sha256);

            var item = Assert.Single(report.Items);
            Assert.False(item.Eligible);
            Assert.Contains("checkout session", item.Outcome, StringComparison.OrdinalIgnoreCase);
            Assert.Null(invoiceIntegration.LastDraft);
        }

        [Fact]
        public void Run_DryRunDoesNotArmAuthorizationRelease()
        {
            using var dbContext = CreateContext();
            var reservation = CreateAuthorizationReservation(
                Guid.Parse("55555555-5555-5555-5555-555555555555"));
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

        [Theory]
        [InlineData("missing-charge-point", false)]
        [InlineData("missing-charge-point", true)]
        [InlineData("missing-connector", false)]
        [InlineData("missing-connector", true)]
        [InlineData("missing-tag", false)]
        [InlineData("missing-tag", true)]
        [InlineData("missing-window-start", false)]
        [InlineData("missing-window-start", true)]
        [InlineData("missing-window-end", false)]
        [InlineData("missing-window-end", true)]
        [InlineData("invalid-window-order", false)]
        [InlineData("invalid-window-order", true)]
        public void Run_RejectsIncompleteTransactionLinkageBeforeProviderCall(
            string missingEvidence,
            bool execute)
        {
            using var dbContext = CreateContext();
            var reservation = CreateAuthorizationReservation(
                Guid.Parse("54545454-5454-5454-5454-545454545454"));
            RemoveTransactionLinkageEvidence(reservation, missingEvidence);
            dbContext.ChargePaymentReservations.Add(reservation);
            dbContext.SaveChanges();
            var manifest = FinancialRecoveryManifest.Parse("""
                {
                  "schemaVersion": 1,
                  "entries": [
                    { "operation": "release-authorization", "reservationId": "54545454-5454-5454-5454-545454545454" }
                  ]
                }
                """);
            var coordinator = new RecordingPaymentCoordinator();
            var service = new FinancialRecoveryService(coordinator, invoiceIntegrationService: null);

            var report = service.Run(
                dbContext,
                manifest,
                execute,
                execute ? manifest.Sha256 : null);

            var item = Assert.Single(report.Items);
            Assert.False(item.Eligible);
            Assert.Contains("transaction linkage", item.Outcome, StringComparison.OrdinalIgnoreCase);
            Assert.False(report.Succeeded);
            Assert.Empty(coordinator.ReconcileCalls);
            dbContext.ChangeTracker.Clear();
            Assert.Null(dbContext.ChargePaymentReservations.Single().AuthorizationReleaseState);
            Assert.Empty(dbContext.PaymentAuthorizationReleaseAttempts);
        }

        [Fact]
        public void Run_DryRunRejectsStoppedUnlinkedTransactionWithinAuthorizationWindow()
        {
            using var dbContext = CreateContext();
            var authorizedAt = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
            var reservation = new ChargePaymentReservation
            {
                ReservationId = Guid.Parse("57575757-5757-5757-5757-575757575757"),
                ChargePointId = "CP-SYNTHETIC",
                ConnectorId = 1,
                ChargeTagId = "TAG-SYNTHETIC",
                OcppIdTag = "TAG-SYNTHETIC",
                Currency = "EUR",
                Status = PaymentReservationStatus.Abandoned,
                StripePaymentIntentId = "pi_synthetic",
                CapturedAmountCents = 0,
                ActualEnergyKwh = 0,
                CreatedAtUtc = authorizedAt.AddMinutes(-1),
                AuthorizedAtUtc = authorizedAt,
                StartDeadlineAtUtc = authorizedAt.AddMinutes(10),
                UpdatedAtUtc = authorizedAt.AddMinutes(20)
            };
            dbContext.ChargePaymentReservations.Add(reservation);
            dbContext.Transactions.Add(new Transaction
            {
                TransactionId = 5757,
                ChargePointId = reservation.ChargePointId,
                ConnectorId = reservation.ConnectorId,
                StartTagId = reservation.OcppIdTag,
                StartTime = authorizedAt.AddMinutes(5),
                StopTime = authorizedAt.AddMinutes(15),
                MeterStart = 100,
                MeterStop = 101
            });
            dbContext.SaveChanges();
            var manifest = FinancialRecoveryManifest.Parse("""
                {
                  "schemaVersion": 1,
                  "entries": [
                    { "operation": "release-authorization", "reservationId": "57575757-5757-5757-5757-575757575757" }
                  ]
                }
                """);
            var service = new FinancialRecoveryService(paymentCoordinator: null, invoiceIntegrationService: null);

            var report = service.Run(dbContext, manifest, execute: false, confirmationSha256: null);

            var item = Assert.Single(report.Items);
            Assert.False(item.Eligible);
            Assert.Contains("transaction", item.Outcome, StringComparison.OrdinalIgnoreCase);
            Assert.Null(reservation.AuthorizationReleaseState);
            Assert.Empty(dbContext.PaymentAuthorizationReleaseAttempts);
        }

        [Fact]
        public void Run_ExecuteRestoresUnarmedStateWhenCoordinatorSkipsAuthorizationRelease()
        {
            using var dbContext = CreateContext();
            var reservation = CreateAuthorizationReservation(
                Guid.Parse("56565656-5656-5656-5656-565656565656"));
            dbContext.ChargePaymentReservations.Add(reservation);
            dbContext.SaveChanges();
            var manifest = FinancialRecoveryManifest.Parse("""
                {
                  "schemaVersion": 1,
                  "entries": [
                    { "operation": "release-authorization", "reservationId": "56565656-5656-5656-5656-565656565656" }
                  ]
                }
                """);
            var coordinator = new RecordingPaymentCoordinator();
            var service = new FinancialRecoveryService(coordinator, invoiceIntegrationService: null);

            var report = service.Run(dbContext, manifest, execute: true, manifest.Sha256);

            var item = Assert.Single(report.Items);
            Assert.False(item.Eligible);
            Assert.Equal(PaymentAuthorizationReleaseOutcome.SkippedNotEligible, item.Outcome);
            dbContext.ChangeTracker.Clear();
            Assert.Null(dbContext.ChargePaymentReservations.Single().AuthorizationReleaseState);
            Assert.Single(coordinator.ReconcileCalls);
        }

        [Fact]
        public void Run_ExecuteReportsRetryScheduledAuthorizationReleaseAsFailure()
        {
            using var dbContext = CreateContext();
            var reservation = CreateAuthorizationReservation(
                Guid.Parse("58585858-5858-5858-5858-585858585858"));
            dbContext.ChargePaymentReservations.Add(reservation);
            dbContext.SaveChanges();
            var manifest = FinancialRecoveryManifest.Parse("""
                {
                  "schemaVersion": 1,
                  "entries": [
                    { "operation": "release-authorization", "reservationId": "58585858-5858-5858-5858-585858585858" }
                  ]
                }
                """);
            var coordinator = new RecordingPaymentCoordinator
            {
                ReconcileOutcome = PaymentAuthorizationReleaseOutcome.RetryScheduled
            };
            var service = new FinancialRecoveryService(coordinator, invoiceIntegrationService: null);

            var report = service.Run(dbContext, manifest, execute: true, manifest.Sha256);

            var item = Assert.Single(report.Items);
            Assert.False(item.Eligible);
            Assert.Equal(PaymentAuthorizationReleaseOutcome.RetryScheduled, item.Outcome);
            Assert.False(report.Succeeded);
            Assert.Single(coordinator.ReconcileCalls);
        }

        [Fact]
        public void Run_ExecuteRejectsReleasedOutcomeWithoutPersistedReleasedAfterState()
        {
            using var dbContext = CreateContext();
            var reservation = CreateAuthorizationReservation(
                Guid.Parse("59595959-5959-5959-5959-595959595959"));
            dbContext.ChargePaymentReservations.Add(reservation);
            dbContext.SaveChanges();
            var manifest = FinancialRecoveryManifest.Parse("""
                {
                  "schemaVersion": 1,
                  "entries": [
                    { "operation": "release-authorization", "reservationId": "59595959-5959-5959-5959-595959595959" }
                  ]
                }
                """);
            var coordinator = new RecordingPaymentCoordinator
            {
                ReconcileOutcome = PaymentAuthorizationReleaseOutcome.Released
            };
            var service = new FinancialRecoveryService(coordinator, invoiceIntegrationService: null);

            var report = service.Run(dbContext, manifest, execute: true, manifest.Sha256);

            var item = Assert.Single(report.Items);
            Assert.False(item.Eligible);
            Assert.Equal("ReleasedOutcomeMissingPersistedAfterState", item.Outcome);
            Assert.False(report.Succeeded);
            Assert.Single(coordinator.ReconcileCalls);
        }

        private static ChargePaymentReservation CreateAuthorizationReservation(Guid reservationId)
        {
            var authorizedAt = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
            return new ChargePaymentReservation
            {
                ReservationId = reservationId,
                ChargePointId = "CP-SYNTHETIC",
                ConnectorId = 1,
                ChargeTagId = "TAG-SYNTHETIC",
                OcppIdTag = "TAG-SYNTHETIC",
                Currency = "EUR",
                Status = PaymentReservationStatus.Abandoned,
                StripePaymentIntentId = "pi_synthetic",
                CapturedAmountCents = 0,
                ActualEnergyKwh = 0,
                CreatedAtUtc = authorizedAt.AddMinutes(-1),
                AuthorizedAtUtc = authorizedAt,
                StartDeadlineAtUtc = authorizedAt.AddMinutes(10),
                UpdatedAtUtc = authorizedAt.AddMinutes(20)
            };
        }

        private static ChargePaymentReservation CreateInvoiceReservation() => new()
        {
            ReservationId = Guid.Parse("60606060-6060-6060-6060-606060606060"),
            TransactionId = 6060,
            ChargePointId = "CP-RECOVERY-CONTEXT",
            ConnectorId = 1,
            ChargeTagId = "TAG-RECOVERY-CONTEXT",
            OcppIdTag = "TAG-RECOVERY-CONTEXT",
            Status = PaymentReservationStatus.Completed,
            StripeCheckoutSessionId = "cs_recovery_context",
            StripePaymentIntentId = "pi_recovery_context",
            CapturedAtUtc = new DateTime(2026, 1, 1, 11, 1, 0, DateTimeKind.Utc),
            CapturedAmountCents = 300,
            Currency = "EUR"
        };

        private static Transaction CreateInvoiceTransaction() => new()
        {
            TransactionId = 6060,
            ChargePointId = "CP-RECOVERY-CONTEXT",
            ConnectorId = 1,
            StartTagId = "TAG-RECOVERY-CONTEXT",
            StartTime = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc),
            StopTime = new DateTime(2026, 1, 1, 11, 0, 0, DateTimeKind.Utc),
            Currency = "EUR",
            EnergyKwh = 5,
            EnergyCost = 2.50m,
            UserSessionFeeAmount = 0.50m
        };

        private static FinancialRecoveryManifest CreateInvoiceManifest(Guid reservationId) =>
            FinancialRecoveryManifest.Parse($$"""
                {
                  "schemaVersion": 1,
                  "entries": [
                    { "operation": "recover-invoice", "reservationId": "{{reservationId}}" }
                  ]
                }
                """);

        private static FinancialRecoveryService CreateService(
            IInvoiceIntegrationService invoiceIntegration,
            IStripeCheckoutSessionReader sessions) =>
            new(paymentCoordinator: null!, invoiceIntegration, sessions);

        private sealed class ContextRecordingInvoiceIntegrationService : IInvoiceIntegrationService
        {
            public InvoiceDraft? LastDraft { get; private set; }

            public void HandleCompletedReservation(
                OCPPCoreContext dbContext,
                ChargePaymentReservation reservation,
                Transaction transaction,
                Session checkoutSession) =>
                LastDraft = new InvoiceDraftBuilder().Build(reservation, transaction, checkoutSession);

            public void RecoverCompletedReservation(
                OCPPCoreContext dbContext,
                ChargePaymentReservation reservation,
                Transaction transaction,
                Session checkoutSession) =>
                LastDraft = new InvoiceDraftBuilder().Build(reservation, transaction, checkoutSession);
        }

        private sealed class RecoverySessionService : IStripeCheckoutSessionReader
        {
            public string? LastGetId { get; private set; }
            public Session GetResponse { get; set; } = new();
            public Exception? GetException { get; set; }

            public Session Get(string id)
            {
                LastGetId = id;
                if (GetException != null)
                {
                    throw GetException;
                }
                return GetResponse;
            }
        }

        private static void RemoveTransactionLinkageEvidence(
            ChargePaymentReservation reservation,
            string missingEvidence)
        {
            switch (missingEvidence)
            {
                case "missing-charge-point":
                    reservation.ChargePointId = " ";
                    break;
                case "missing-connector":
                    reservation.ConnectorId = 0;
                    break;
                case "missing-tag":
                    reservation.ChargeTagId = " ";
                    reservation.OcppIdTag = " ";
                    break;
                case "missing-window-start":
                    reservation.AuthorizedAtUtc = null;
                    reservation.CreatedAtUtc = default;
                    break;
                case "missing-window-end":
                    reservation.StartDeadlineAtUtc = null;
                    break;
                case "invalid-window-order":
                    reservation.StartDeadlineAtUtc = reservation.AuthorizedAtUtc.GetValueOrDefault().AddMinutes(-1);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(missingEvidence), missingEvidence, null);
            }
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
