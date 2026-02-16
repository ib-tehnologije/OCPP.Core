using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
            IConfiguration configuration)
            : base(
                scopeFactory,
                NullLogger<PaymentReservationCleanupService>.Instance,
                configuration,
                Options.Create(new PaymentFlowOptions { StartWindowMinutes = 7 }))
        {
        }

        public Task RunOnce(CancellationToken token = default) => CleanupAsync(token);
    }

    internal sealed class RecordingPaymentCoordinator : IPaymentCoordinator
    {
        public bool IsEnabled => true;
        public List<(Guid ReservationId, string Reason)> CancelCalls { get; } = new();

        public PaymentSessionResult CreateCheckoutSession(OCPPCoreContext dbContext, PaymentSessionRequest request) =>
            throw new NotImplementedException();

        public PaymentConfirmationResult ConfirmReservation(OCPPCoreContext dbContext, Guid reservationId, string checkoutSessionId) =>
            throw new NotImplementedException();

        public void CancelReservation(OCPPCoreContext dbContext, Guid reservationId, string reason) =>
            throw new NotImplementedException();

        public void CancelPaymentIntentIfCancelable(OCPPCoreContext dbContext, ChargePaymentReservation reservation, string reason)
        {
            CancelCalls.Add((reservation?.ReservationId ?? Guid.Empty, reason));
        }

        public void MarkTransactionStarted(OCPPCoreContext dbContext, string chargePointId, int connectorId, string chargeTagId, int transactionId) =>
            throw new NotImplementedException();

        public void CompleteReservation(OCPPCoreContext dbContext, Transaction transaction) =>
            throw new NotImplementedException();

        public void HandleWebhookEvent(OCPPCoreContext dbContext, string payload, string signatureHeader) =>
            throw new NotImplementedException();
    }
}
