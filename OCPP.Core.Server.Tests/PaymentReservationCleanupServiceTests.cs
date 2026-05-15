using System;
using System.Collections.Generic;
using System.Linq;
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
using Xunit;

namespace OCPP.Core.Server.Tests
{
    public class PaymentReservationCleanupServiceTests
    {
        [Fact]
        public async Task CleanupAsync_AbandonsStalePendingReservation_AndRequestsCancel()
        {
            var coordinator = new RecordingPaymentCoordinator();
            using var provider = BuildProvider(
                coordinator,
                new Dictionary<string, string?>
                {
                    ["Maintenance:PendingPaymentTimeoutMinutes"] = "1",
                    ["Maintenance:CleanupIntervalSeconds"] = "30"
                });

            var reservationId = Guid.NewGuid();
            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OCPPCoreContext>();
                db.ChargePaymentReservations.Add(new ChargePaymentReservation
                {
                    ReservationId = reservationId,
                    ChargePointId = "CP1",
                    ConnectorId = 1,
                    ChargeTagId = "TAG1",
                    StripePaymentIntentId = "pi_stale",
                    Status = PaymentReservationStatus.Pending,
                    Currency = "eur",
                    CreatedAtUtc = DateTime.UtcNow.AddHours(-2),
                    UpdatedAtUtc = DateTime.UtcNow.AddHours(-2)
                });
                db.SaveChanges();
            }

            var service = new CleanupServiceHarness(
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<IConfiguration>());

            await service.RunOnce();

            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OCPPCoreContext>();
                var reservation = db.ChargePaymentReservations.Single(r => r.ReservationId == reservationId);

                Assert.Equal(PaymentReservationStatus.Abandoned, reservation.Status);
                Assert.Equal("CleanupTimeout", reservation.FailureCode);
                Assert.Contains("Auto-cancelled", reservation.LastError);
            }

            Assert.Single(coordinator.CancelCalls);
            Assert.Equal(reservationId, coordinator.CancelCalls[0].ReservationId);
            Assert.Equal("Reservation stale", coordinator.CancelCalls[0].Reason);
        }

        [Fact]
        public async Task CleanupAsync_MarksStartTimeout_WhenStartWindowExpired()
        {
            var coordinator = new RecordingPaymentCoordinator();
            using var provider = BuildProvider(
                coordinator,
                new Dictionary<string, string?>
                {
                    ["Maintenance:PendingPaymentTimeoutMinutes"] = "15",
                    ["Maintenance:CleanupIntervalSeconds"] = "30"
                });

            var reservationId = Guid.NewGuid();
            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OCPPCoreContext>();
                db.ChargePaymentReservations.Add(new ChargePaymentReservation
                {
                    ReservationId = reservationId,
                    ChargePointId = "CP1",
                    ConnectorId = 1,
                    ChargeTagId = "TAG2",
                    StripePaymentIntentId = "pi_start_timeout",
                    Status = PaymentReservationStatus.Authorized,
                    StartDeadlineAtUtc = DateTime.UtcNow.AddMinutes(-1),
                    Currency = "eur",
                    CreatedAtUtc = DateTime.UtcNow.AddMinutes(-20),
                    UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-20)
                });
                db.SaveChanges();
            }

            var service = new CleanupServiceHarness(
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<IConfiguration>());

            await service.RunOnce();

            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OCPPCoreContext>();
                var reservation = db.ChargePaymentReservations.Single(r => r.ReservationId == reservationId);

                Assert.Equal(PaymentReservationStatus.StartTimeout, reservation.Status);
                Assert.Equal("StartTimeout", reservation.FailureCode);
                Assert.Equal("Start window expired without transaction.", reservation.LastError);
                Assert.Equal("Start window expired without transaction.", reservation.FailureMessage);
            }

            Assert.Single(coordinator.CancelCalls);
            Assert.Equal(reservationId, coordinator.CancelCalls[0].ReservationId);
            Assert.Equal("Start window expired", coordinator.CancelCalls[0].Reason);
        }

        [Fact]
        public async Task CleanupAsync_ClosesChargingReservation_WhenConnectorPersistedAvailable()
        {
            var coordinator = new RecordingPaymentCoordinator();
            using var provider = BuildProvider(
                coordinator,
                new Dictionary<string, string?>
                {
                    ["Maintenance:PendingPaymentTimeoutMinutes"] = "15",
                    ["Maintenance:CleanupIntervalSeconds"] = "30",
                    ["Maintenance:AvailableStatusOpenTransactionGraceMinutes"] = "1"
                });

            var reservationId = Guid.NewGuid();
            var availableAt = DateTime.UtcNow.AddMinutes(-5);

            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OCPPCoreContext>();
                db.Transactions.Add(new Transaction
                {
                    TransactionId = 9001,
                    ChargePointId = "CP1",
                    ConnectorId = 2,
                    StartTagId = "TAG3",
                    StartTime = availableAt.AddHours(-1),
                    MeterStart = 10.0
                });
                db.ConnectorStatuses.Add(new ConnectorStatus
                {
                    ChargePointId = "CP1",
                    ConnectorId = 2,
                    LastStatus = "Available",
                    LastStatusTime = availableAt,
                    LastMeter = 12.5,
                    LastMeterTime = availableAt.AddMinutes(-1)
                });
                db.ChargePaymentReservations.Add(new ChargePaymentReservation
                {
                    ReservationId = reservationId,
                    ChargePointId = "CP1",
                    ConnectorId = 2,
                    ChargeTagId = "TAG3",
                    OcppIdTag = "TAG3",
                    StripePaymentIntentId = "pi_available",
                    Status = PaymentReservationStatus.Charging,
                    TransactionId = 9001,
                    Currency = "eur",
                    CreatedAtUtc = availableAt.AddHours(-1),
                    UpdatedAtUtc = availableAt.AddHours(-1)
                });
                db.SaveChanges();
            }

            var service = new CleanupServiceHarness(
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<IConfiguration>());

            await service.RunOnce();

            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OCPPCoreContext>();
                var transaction = db.Transactions.Single(t => t.TransactionId == 9001);
                var reservation = db.ChargePaymentReservations.Single(r => r.ReservationId == reservationId);

                Assert.Equal(availableAt, transaction.StopTime);
                Assert.Equal(12.5, transaction.MeterStop);
                Assert.Equal("ConnectorAvailableWithoutStopTransaction", transaction.StopReason);
                Assert.Equal(availableAt, transaction.ChargingEndedAtUtc);
                Assert.Equal(PaymentReservationStatus.Completed, reservation.Status);
                Assert.Equal(availableAt, reservation.StopTransactionAtUtc);
                Assert.Equal(availableAt, reservation.DisconnectedAtUtc);
            }

            Assert.Equal(new[] { 9001 }, coordinator.CompleteCalls);
        }

        [Fact]
        public async Task CleanupAsync_LogsRecoveryDiagnostic_WhenConnectorPersistedAvailable()
        {
            var coordinator = new RecordingPaymentCoordinator();
            var logger = new RecordingLogger<PaymentReservationCleanupService>();
            using var provider = BuildProvider(
                coordinator,
                new Dictionary<string, string?>
                {
                    ["Maintenance:PendingPaymentTimeoutMinutes"] = "15",
                    ["Maintenance:CleanupIntervalSeconds"] = "30",
                    ["Maintenance:AvailableStatusOpenTransactionGraceMinutes"] = "1"
                });

            var reservationId = Guid.NewGuid();
            var availableAt = DateTime.UtcNow.AddMinutes(-5);

            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OCPPCoreContext>();
                db.Transactions.Add(new Transaction
                {
                    TransactionId = 9002,
                    ChargePointId = "CP-DIAG",
                    ConnectorId = 1,
                    StartTagId = "TAG-DIAG",
                    StartTime = availableAt.AddHours(-2),
                    MeterStart = 30.0
                });
                db.ConnectorStatuses.Add(new ConnectorStatus
                {
                    ChargePointId = "CP-DIAG",
                    ConnectorId = 1,
                    LastStatus = "Available",
                    LastStatusTime = availableAt,
                    LastMeter = 34.75,
                    LastMeterTime = availableAt.AddMinutes(-1)
                });
                db.ChargePaymentReservations.Add(new ChargePaymentReservation
                {
                    ReservationId = reservationId,
                    ChargePointId = "CP-DIAG",
                    ConnectorId = 1,
                    ChargeTagId = "TAG-DIAG",
                    OcppIdTag = "TAG-DIAG",
                    StripePaymentIntentId = "pi_diag",
                    Status = PaymentReservationStatus.Charging,
                    TransactionId = 9002,
                    Currency = "eur",
                    CreatedAtUtc = availableAt.AddHours(-2),
                    UpdatedAtUtc = availableAt.AddHours(-2)
                });
                db.SaveChanges();
            }

            var service = new CleanupServiceHarness(
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<IConfiguration>(),
                logger);

            await service.RunOnce();

            Assert.Contains(logger.Entries, entry =>
                entry.Level == LogLevel.Warning &&
                entry.Message.Contains("Open transaction recovery candidate", StringComparison.Ordinal) &&
                entry.Message.Contains("cp=CP-DIAG", StringComparison.Ordinal) &&
                entry.Message.Contains("connector=1", StringComparison.Ordinal) &&
                entry.Message.Contains("tx=9002", StringComparison.Ordinal) &&
                entry.Message.Contains("reservation=", StringComparison.Ordinal) &&
                entry.Message.Contains("status=Available", StringComparison.Ordinal) &&
                entry.Message.Contains("reservationStatus=Charging", StringComparison.Ordinal));
        }

        [Fact]
        public async Task CleanupAsync_DoesNotChangeReservations_ThatAreStillValid()
        {
            var coordinator = new RecordingPaymentCoordinator();
            using var provider = BuildProvider(
                coordinator,
                new Dictionary<string, string?>
                {
                    ["Maintenance:PendingPaymentTimeoutMinutes"] = "5",
                    ["Maintenance:CleanupIntervalSeconds"] = "30"
                });

            Guid pendingId;
            Guid authorizedId;
            Guid startedId;

            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OCPPCoreContext>();

                var pending = new ChargePaymentReservation
                {
                    ReservationId = Guid.NewGuid(),
                    ChargePointId = "CP1",
                    ConnectorId = 1,
                    ChargeTagId = "TAG1",
                    Status = PaymentReservationStatus.Pending,
                    Currency = "eur",
                    CreatedAtUtc = DateTime.UtcNow.AddMinutes(-1),
                    UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-1)
                };

                var authorized = new ChargePaymentReservation
                {
                    ReservationId = Guid.NewGuid(),
                    ChargePointId = "CP1",
                    ConnectorId = 2,
                    ChargeTagId = "TAG2",
                    Status = PaymentReservationStatus.Authorized,
                    StartDeadlineAtUtc = DateTime.UtcNow.AddMinutes(5),
                    Currency = "eur",
                    CreatedAtUtc = DateTime.UtcNow.AddMinutes(-1),
                    UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-1)
                };

                var started = new ChargePaymentReservation
                {
                    ReservationId = Guid.NewGuid(),
                    ChargePointId = "CP1",
                    ConnectorId = 3,
                    ChargeTagId = "TAG3",
                    Status = PaymentReservationStatus.StartRequested,
                    StartDeadlineAtUtc = DateTime.UtcNow.AddMinutes(-5),
                    TransactionId = 1001,
                    Currency = "eur",
                    CreatedAtUtc = DateTime.UtcNow.AddMinutes(-10),
                    UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-10)
                };

                db.ChargePaymentReservations.AddRange(pending, authorized, started);
                db.SaveChanges();

                pendingId = pending.ReservationId;
                authorizedId = authorized.ReservationId;
                startedId = started.ReservationId;
            }

            var service = new CleanupServiceHarness(
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<IConfiguration>());

            await service.RunOnce();

            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OCPPCoreContext>();
                var pending = db.ChargePaymentReservations.Single(r => r.ReservationId == pendingId);
                var authorized = db.ChargePaymentReservations.Single(r => r.ReservationId == authorizedId);
                var started = db.ChargePaymentReservations.Single(r => r.ReservationId == startedId);

                Assert.Equal(PaymentReservationStatus.Pending, pending.Status);
                Assert.Equal(PaymentReservationStatus.Authorized, authorized.Status);
                Assert.Equal(PaymentReservationStatus.StartRequested, started.Status);
            }

            Assert.Empty(coordinator.CancelCalls);
        }

        private static ServiceProvider BuildProvider(
            RecordingPaymentCoordinator coordinator,
            IDictionary<string, string?> configurationData)
        {
            var dbName = Guid.NewGuid().ToString();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(
                new ConfigurationBuilder()
                    .AddInMemoryCollection(configurationData)
                    .Build());
            services.AddDbContext<OCPPCoreContext>(options =>
                options.UseInMemoryDatabase(dbName));
            services.AddSingleton<IPaymentCoordinator>(coordinator);
            return services.BuildServiceProvider();
        }
    }

    internal sealed class CleanupServiceHarness : PaymentReservationCleanupService
    {
        public CleanupServiceHarness(
            IServiceScopeFactory scopeFactory,
            IConfiguration configuration,
            ILogger<PaymentReservationCleanupService>? logger = null)
            : base(
                scopeFactory,
                logger ?? NullLogger<PaymentReservationCleanupService>.Instance,
                configuration,
                Options.Create(new PaymentFlowOptions { StartWindowMinutes = 7 }))
        {
        }

        public Task RunOnce(CancellationToken token = default) => CleanupAsync(token);
    }

    internal sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception), exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose()
            {
            }
        }
    }

    internal sealed class RecordingPaymentCoordinator : IPaymentCoordinator
    {
        public bool IsEnabled => true;
        public List<(Guid ReservationId, string Reason)> CancelCalls { get; } = new();
        public List<int> CompleteCalls { get; } = new();

        public PaymentSessionResult CreateCheckoutSession(OCPPCoreContext dbContext, PaymentSessionRequest request) =>
            throw new NotImplementedException();

        public PaymentConfirmationResult ConfirmReservation(OCPPCoreContext dbContext, Guid reservationId, string checkoutSessionId) =>
            throw new NotImplementedException();

        public PaymentResumeResult ResumeReservation(OCPPCoreContext dbContext, Guid reservationId) =>
            throw new NotImplementedException();

        public PaymentR1InvoiceResult RequestR1Invoice(OCPPCoreContext dbContext, PaymentR1InvoiceRequest request) =>
            throw new NotImplementedException();

        public void CancelReservation(OCPPCoreContext dbContext, Guid reservationId, string reason) =>
            throw new NotImplementedException();

        public void CancelPaymentIntentIfCancelable(OCPPCoreContext dbContext, ChargePaymentReservation reservation, string reason)
        {
            CancelCalls.Add((reservation?.ReservationId ?? Guid.Empty, reason));
        }

        public void MarkTransactionStarted(OCPPCoreContext dbContext, string chargePointId, int connectorId, string chargeTagId, int transactionId) =>
            throw new NotImplementedException();

        public void CompleteReservation(OCPPCoreContext dbContext, Transaction transaction)
        {
            CompleteCalls.Add(transaction.TransactionId);

            var reservation = dbContext.ChargePaymentReservations.SingleOrDefault(r => r.TransactionId == transaction.TransactionId);
            if (reservation == null)
            {
                return;
            }

            reservation.Status = PaymentReservationStatus.Completed;
            reservation.StopTransactionAtUtc = transaction.StopTime;
            reservation.DisconnectedAtUtc = transaction.StopTime;
            reservation.UpdatedAtUtc = transaction.StopTime ?? DateTime.UtcNow;
            dbContext.SaveChanges();
        }

        public void HandleConnectorAvailable(OCPPCoreContext dbContext, string chargePointId, int connectorId, DateTime disconnectedAtUtc) =>
            throw new NotImplementedException();

        public void HandleWebhookEvent(OCPPCoreContext dbContext, string payload, string signatureHeader) =>
            throw new NotImplementedException();
    }
}
