using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OCPP.Core.Database;
using OCPP.Core.Server.Payments;
using OCPP.Core.Server.Payments.Providers;
using OCPP.Core.Server.Payments.Recovery;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    /// <summary>
    /// Existing reservations keep being processed while any registered provider is configured
    /// (<see cref="IPaymentCoordinator.IsOperational"/>), even when the provider selected for new checkouts
    /// (<see cref="IPaymentCoordinator.IsEnabled"/>) is not, for example while a previous provider drains after a switch.
    /// </summary>
    public class PaymentOperationalGateTests
    {
        private const string ReturnBaseUrl = "https://return.example.com";
        private const string ActiveProvider = "NewPay";
        private const string PreviousProvider = "OldPay";
        private static readonly DateTime Now = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void FinancialRecovery_ExecuteRunsWhileOnlyThePreviousProviderIsConfigured()
        {
            using var dbContext = CreateContext();
            var reservationId = Guid.Parse("61616161-6161-6161-6161-616161616161");
            dbContext.ChargePaymentReservations.Add(CreateAbandonedAuthorizedReservation(reservationId));
            dbContext.SaveChanges();
            var manifest = ReleaseManifest(reservationId);
            var coordinator = new GateRecordingCoordinator(isEnabled: false, isOperational: true);
            var service = new FinancialRecoveryService(coordinator, invoiceIntegrationService: null);

            var report = service.Run(dbContext, manifest, execute: true, manifest.Sha256);

            var item = Assert.Single(report.Items);
            Assert.Equal(PaymentAuthorizationReleaseOutcome.SkippedNotEligible, item.Outcome);
            Assert.Equal(new[] { reservationId }, coordinator.ReconcileCalls);
        }

        [Fact]
        public void FinancialRecovery_ExecuteIsBlockedWhenNoProviderIsConfigured()
        {
            using var dbContext = CreateContext();
            var reservationId = Guid.Parse("62626262-6262-6262-6262-626262626262");
            dbContext.ChargePaymentReservations.Add(CreateAbandonedAuthorizedReservation(reservationId));
            dbContext.SaveChanges();
            var manifest = ReleaseManifest(reservationId);
            var coordinator = new GateRecordingCoordinator(isEnabled: false, isOperational: false);
            var service = new FinancialRecoveryService(coordinator, invoiceIntegrationService: null);

            var report = service.Run(dbContext, manifest, execute: true, manifest.Sha256);

            var item = Assert.Single(report.Items);
            Assert.False(item.Eligible);
            Assert.Equal("Payment provider integration is unavailable or disabled.", item.Outcome);
            Assert.Empty(coordinator.ReconcileCalls);
        }

        [Fact]
        public async Task IdleWarning_IsSentForPreviousProviderSessionsWhileTheActiveProviderIsNotConfigured()
        {
            var coordinator = CreateCoordinator(activeConfigured: false, previousConfigured: true);
            Assert.False(coordinator.IsEnabled);
            Assert.True(coordinator.IsOperational);

            var email = await RunIdleWarningSweepAsync(coordinator);

            var warning = Assert.Single(email.IdleFeeWarnings);
            Assert.Equal("idle-payer@example.com", warning.ToEmail);
            Assert.StartsWith($"{ReturnBaseUrl}/Payments/Status?reservationId=", warning.StatusUrl);
        }

        [Fact]
        public async Task IdleWarning_IsNotSentWhenNoProviderIsConfigured()
        {
            var coordinator = CreateCoordinator(activeConfigured: false, previousConfigured: false);
            Assert.False(coordinator.IsOperational);

            var email = await RunIdleWarningSweepAsync(coordinator);

            Assert.Empty(email.IdleFeeWarnings);
        }

        private static async Task<RecordingEmailNotificationService> RunIdleWarningSweepAsync(IPaymentCoordinator coordinator)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Maintenance:IdleWarningSweepSeconds"] = "30"
                })
                .Build();
            services.AddSingleton<IConfiguration>(configuration);
            var databaseName = Guid.NewGuid().ToString();
            services.AddDbContext<OCPPCoreContext>(options => options.UseInMemoryDatabase(databaseName));
            var email = new RecordingEmailNotificationService();
            services.AddSingleton<IEmailNotificationService>(email);
            services.AddSingleton(coordinator);
            using var provider = services.BuildServiceProvider();

            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OCPPCoreContext>();
                db.ChargePaymentReservations.Add(new ChargePaymentReservation
                {
                    ReservationId = Guid.NewGuid(),
                    ChargePointId = "CP1",
                    ConnectorId = 1,
                    ChargeTagId = "TAG1",
                    PaymentProvider = PreviousProvider,
                    StripeCheckoutSessionId = "old_token_idle",
                    StripePaymentIntentId = "old_tx_idle",
                    PayerEmail = "idle-payer@example.com",
                    Status = PaymentReservationStatus.Charging,
                    UsageFeeAnchorMinutes = 1,
                    UsageFeePerMinute = 0.20m,
                    StartUsageFeeAfterMinutes = 20,
                    MaxUsageFeeMinutes = 120,
                    Currency = "eur",
                    TransactionId = 995,
                    CreatedAtUtc = Now.AddMinutes(-30),
                    UpdatedAtUtc = Now.AddMinutes(-5)
                });
                db.Transactions.Add(new Transaction
                {
                    TransactionId = 995,
                    ChargePointId = "CP1",
                    ConnectorId = 1,
                    StartTagId = "TAG1",
                    StartTime = Now.AddHours(-1),
                    ChargingEndedAtUtc = Now.AddMinutes(-10)
                });
                db.SaveChanges();
            }

            var service = new IdleWarningHarness(
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<ILogger<IdleFeeWarningEmailService>>(),
                configuration,
                Options.Create(new NotificationOptions { EnableCustomerEmails = true, IdleWarningLeadMinutes = 15 }),
                Settings(),
                () => Now);

            await service.RunOnce();
            return email;
        }

        private static PaymentCoordinator CreateCoordinator(bool activeConfigured, bool previousConfigured)
        {
            var registry = new PaymentGatewayRegistry(
                new IPaymentGateway[]
                {
                    new StaticPaymentGateway(ActiveProvider, activeConfigured),
                    new StaticPaymentGateway(PreviousProvider, previousConfigured)
                },
                ActiveProvider);
            return new PaymentCoordinator(
                registry,
                Settings(),
                Options.Create(new PaymentFlowOptions()),
                NullLogger<PaymentCoordinator>.Instance,
                () => Now);
        }

        private static PaymentCheckoutSettings Settings() => new()
        {
            ActiveProvider = ActiveProvider,
            ReturnBaseUrl = ReturnBaseUrl,
            Currency = "eur",
            ProductName = "Charging session"
        };

        private static FinancialRecoveryManifest ReleaseManifest(Guid reservationId) =>
            FinancialRecoveryManifest.Parse($$"""
                {
                  "schemaVersion": 1,
                  "entries": [
                    { "operation": "release-authorization", "reservationId": "{{reservationId}}" }
                  ]
                }
                """);

        private static ChargePaymentReservation CreateAbandonedAuthorizedReservation(Guid reservationId)
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
                PaymentProvider = PreviousProvider,
                Status = PaymentReservationStatus.Abandoned,
                StripePaymentIntentId = "old_tx_synthetic",
                CapturedAmountCents = 0,
                ActualEnergyKwh = 0,
                CreatedAtUtc = authorizedAt.AddMinutes(-1),
                AuthorizedAtUtc = authorizedAt,
                StartDeadlineAtUtc = authorizedAt.AddMinutes(10),
                UpdatedAtUtc = authorizedAt.AddMinutes(20)
            };
        }

        private static OCPPCoreContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<OCPPCoreContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            return new OCPPCoreContext(options);
        }

        private sealed class IdleWarningHarness : IdleFeeWarningEmailService
        {
            public IdleWarningHarness(
                IServiceScopeFactory scopeFactory,
                ILogger<IdleFeeWarningEmailService> logger,
                IConfiguration configuration,
                IOptions<NotificationOptions> notificationOptions,
                PaymentCheckoutSettings checkoutSettings,
                Func<DateTime> utcNow)
                : base(scopeFactory, logger, configuration, notificationOptions, checkoutSettings, utcNow)
            {
            }

            public Task RunOnce(CancellationToken token = default) => SweepAsync(token);
        }

        /// <summary>Coordinator with independent gates that records the release reconciliation calls it receives.</summary>
        private sealed class GateRecordingCoordinator : IPaymentCoordinator
        {
            public GateRecordingCoordinator(bool isEnabled, bool isOperational)
            {
                IsEnabled = isEnabled;
                IsOperational = isOperational;
            }

            public bool IsEnabled { get; }
            public bool IsOperational { get; }
            public List<Guid> ReconcileCalls { get; } = new();

            public PaymentAuthorizationReleaseResult ReconcileTerminalPaymentAuthorization(
                OCPPCoreContext dbContext,
                ChargePaymentReservation reservation,
                string trigger)
            {
                ReconcileCalls.Add(reservation?.ReservationId ?? Guid.Empty);
                return new PaymentAuthorizationReleaseResult
                {
                    Outcome = PaymentAuthorizationReleaseOutcome.SkippedNotEligible
                };
            }

            public PaymentSessionResult CreateCheckoutSession(OCPPCoreContext dbContext, PaymentSessionRequest request) => throw new NotSupportedException();
            public PaymentConfirmationResult ConfirmReservation(OCPPCoreContext dbContext, Guid reservationId, string checkoutSessionId) => throw new NotSupportedException();
            public PaymentResumeResult ResumeReservation(OCPPCoreContext dbContext, Guid reservationId) => throw new NotSupportedException();
            public PaymentR1InvoiceResult RequestR1Invoice(OCPPCoreContext dbContext, PaymentR1InvoiceRequest request) => throw new NotSupportedException();
            public void CancelReservation(OCPPCoreContext dbContext, Guid reservationId, string reason) => throw new NotSupportedException();
            public void CancelPaymentIntentIfCancelable(OCPPCoreContext dbContext, ChargePaymentReservation reservation, string reason) => throw new NotSupportedException();
            public void MarkTransactionStarted(OCPPCoreContext dbContext, string chargePointId, int connectorId, string chargeTagId, int transactionId) => throw new NotSupportedException();
            public void CompleteReservation(OCPPCoreContext dbContext, Transaction transaction) => throw new NotSupportedException();
            public void HandleConnectorAvailable(OCPPCoreContext dbContext, string chargePointId, int connectorId, DateTime disconnectedAtUtc) => throw new NotSupportedException();
            public void HandleWebhookEvent(OCPPCoreContext dbContext, string payload, string signatureHeader) => throw new NotSupportedException();
        }

        /// <summary>Gateway without PSP calls: only its name and configuration state matter to these tests.</summary>
        private sealed class StaticPaymentGateway : IPaymentGateway
        {
            public StaticPaymentGateway(string providerName, bool isConfigured)
            {
                ProviderName = providerName;
                IsConfigured = isConfigured;
            }

            public string ProviderName { get; }
            public bool IsConfigured { get; }
            public string SuccessUrlSessionPlaceholder => null!;
            public bool SupportsMetadata => false;
            public bool SupportsDeferredCheckoutLookup => false;

            public PaymentCheckoutSession CreateCheckout(PaymentCheckoutRequest request) => throw new NotSupportedException();
            public PaymentCheckoutStatus GetCheckout(string sessionReference) => throw new NotSupportedException();
            public PaymentAuthorizationSnapshot GetAuthorization(string authorizationReference) => throw new NotSupportedException();
            public PaymentAuthorizationSnapshot Capture(string authorizationReference, long amountMinor, string currency, string idempotencyKey) => throw new NotSupportedException();
            public PaymentAuthorizationSnapshot Cancel(string authorizationReference, string idempotencyKey) => throw new NotSupportedException();

            public IReadOnlyDictionary<string, string> MirrorMetadata(
                string sessionReference,
                string fallbackAuthorizationReference,
                Action<IDictionary<string, string>> mutate) => throw new NotSupportedException();

            public PaymentWebhookParseResult ParseWebhook(PaymentWebhookRequest request) => throw new NotSupportedException();
            public string SanitizeProviderText(string text) => text;
        }

        private sealed class RecordingEmailNotificationService : IEmailNotificationService
        {
            public List<(string ToEmail, string StatusUrl)> IdleFeeWarnings { get; } = new();

            public void SendIdleFeeWarning(string toEmail, ChargePaymentReservation reservation, Transaction transaction, ChargePoint chargePoint, DateTime idleFeeStartsAtUtc, TimeSpan remainingUntilIdleFee, string statusUrl) =>
                IdleFeeWarnings.Add((toEmail, statusUrl));

            public void SendPaymentAuthorized(string toEmail, ChargePaymentReservation reservation, PaymentCheckoutDetails checkoutDetails, string statusUrl) { }
            public void SendChargingCompleted(string toEmail, ChargePaymentReservation reservation, Transaction transaction, ChargePoint chargePoint, string statusUrl) { }
            public void SendSessionReceipt(string toEmail, ChargePaymentReservation reservation, Transaction transaction, ChargePoint chargePoint, string statusUrl, string invoiceNumber, string invoiceUrl) { }
            public void SendR1InvoiceRequested(string toEmail, ChargePaymentReservation reservation, ChargePoint chargePoint, string statusUrl, string buyerCompanyName, string buyerOib) { }
            public void SendR1InvoiceReady(string toEmail, ChargePaymentReservation reservation, Transaction transaction, ChargePoint chargePoint, string statusUrl, string invoiceNumber, string invoiceUrl) { }
        }
    }
}
