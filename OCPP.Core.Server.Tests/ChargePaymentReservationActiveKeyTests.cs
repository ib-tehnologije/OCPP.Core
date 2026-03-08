using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OCPP.Core.Database;
using OCPP.Core.Server.Payments;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    public class ChargePaymentReservationActiveKeyTests
    {
        [Fact]
        public void SaveChanges_SynchronizesActiveConnectorKey_ForSqlite()
        {
            using var connection = CreateConnection();
            using var context = CreateContext(connection);

            var reservation = CreateReservation(PaymentReservationStatus.Pending);
            context.ChargePaymentReservations.Add(reservation);
            context.SaveChanges();

            Assert.Equal(
                ChargePaymentReservationActiveKey.ActiveValue,
                context.Entry(reservation).Property<string>("ActiveConnectorKey").CurrentValue);

            reservation.Status = PaymentReservationStatus.Cancelled;
            context.SaveChanges();

            Assert.Equal(
                reservation.ReservationId.ToString().ToUpperInvariant(),
                context.Entry(reservation).Property<string>("ActiveConnectorKey").CurrentValue);
        }

        [Fact]
        public void StartupMaintenance_Run_RepairsStaleActiveConnectorKey_ForSqlite()
        {
            using var connection = CreateConnection();
            Guid reservationId;

            using (var setupContext = CreateContext(connection))
            {
                var reservation = CreateReservation(PaymentReservationStatus.Pending);
                reservationId = reservation.ReservationId;
                setupContext.ChargePaymentReservations.Add(reservation);
                setupContext.SaveChanges();
            }

            using (var corruptContext = CreateContext(connection))
            {
                corruptContext.Database.ExecuteSqlInterpolated($@"
                    UPDATE ChargePaymentReservation
                    SET Status = {PaymentReservationStatus.Cancelled},
                        ActiveConnectorKey = {ChargePaymentReservationActiveKey.ActiveValue}
                    WHERE ReservationId = {reservationId}");
            }

            using (var repairContext = CreateContext(connection))
            {
                StartupMaintenance.Run(
                    repairContext,
                    NullLogger.Instance,
                    new ConfigurationBuilder().Build(),
                    () => DateTime.UtcNow);

                var repaired = repairContext.ChargePaymentReservations.Single(r => r.ReservationId == reservationId);
                Assert.Equal(
                    reservationId.ToString().ToUpperInvariant(),
                    repairContext.Entry(repaired).Property<string>("ActiveConnectorKey").CurrentValue);
            }
        }

        [Theory]
        [InlineData(PaymentReservationStatus.Cancelled)]
        [InlineData(PaymentReservationStatus.Failed)]
        [InlineData(PaymentReservationStatus.Completed)]
        [InlineData(PaymentReservationStatus.Abandoned)]
        [InlineData(PaymentReservationStatus.StartRejected)]
        [InlineData(PaymentReservationStatus.StartTimeout)]
        public void IsConnectorBusy_IgnoresNonLockingReservation_WithStaleActiveConnectorKey(string terminalStatus)
        {
            using var connection = CreateConnection();
            Guid reservationId;

            using (var setupContext = CreateContext(connection))
            {
                var reservation = CreateReservation(PaymentReservationStatus.Pending);
                reservationId = reservation.ReservationId;
                setupContext.ChargePaymentReservations.Add(reservation);
                setupContext.SaveChanges();
            }

            using (var corruptContext = CreateContext(connection))
            {
                corruptContext.Database.ExecuteSqlInterpolated($@"
                    UPDATE ChargePaymentReservation
                    SET Status = {terminalStatus},
                        ActiveConnectorKey = {ChargePaymentReservationActiveKey.ActiveValue}
                    WHERE ReservationId = {reservationId}");
            }

            using var verificationContext = CreateContext(connection);
            var middleware = CreateMiddleware();
            var isBusy = InvokeIsConnectorBusy(middleware, verificationContext, "CP-ACTIVE", 1, null, out var reason);

            Assert.False(isBusy);
            Assert.NotEqual("ActiveReservation", reason);
        }

        private static SqliteConnection CreateConnection()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            return connection;
        }

        private static OCPPCoreContext CreateContext(SqliteConnection connection)
        {
            var options = new DbContextOptionsBuilder<OCPPCoreContext>()
                .UseSqlite(connection)
                .Options;

            var context = new OCPPCoreContext(options);
            context.Database.EnsureCreated();
            return context;
        }

        private static ChargePaymentReservation CreateReservation(string status)
        {
            var now = DateTime.UtcNow;
            return new ChargePaymentReservation
            {
                ReservationId = Guid.NewGuid(),
                ChargePointId = "CP-ACTIVE",
                ConnectorId = 1,
                ChargeTagId = "TAG-ACTIVE",
                Currency = "eur",
                Status = status,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
        }

        private static OCPPMiddleware CreateMiddleware()
        {
            var services = new ServiceCollection().BuildServiceProvider();
            var mediator = new StartChargingMediator();

            return new OCPPMiddleware(
                _ => Task.CompletedTask,
                NullLoggerFactory.Instance,
                new ConfigurationBuilder().Build(),
                services.GetRequiredService<IServiceScopeFactory>(),
                new NoopPaymentCoordinator(),
                mediator,
                new ReservationLinkService(mediator));
        }

        private static bool InvokeIsConnectorBusy(
            OCPPMiddleware middleware,
            OCPPCoreContext context,
            string chargePointId,
            int connectorId,
            Guid? reservationToIgnore,
            out string? busyReason)
        {
            var method = typeof(OCPPMiddleware)
                .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(m => m.Name == "IsConnectorBusy" && m.GetParameters().Length == 6);

            object?[] args =
            {
                context,
                chargePointId,
                connectorId,
                null,
                reservationToIgnore,
                null
            };

            var result = method.Invoke(middleware, args);
            busyReason = args[5] as string;
            return Assert.IsType<bool>(result);
        }

        private sealed class NoopPaymentCoordinator : IPaymentCoordinator
        {
            public bool IsEnabled => false;

            public PaymentSessionResult CreateCheckoutSession(OCPPCoreContext dbContext, PaymentSessionRequest request) => throw new NotSupportedException();
            public PaymentConfirmationResult ConfirmReservation(OCPPCoreContext dbContext, Guid reservationId, string checkoutSessionId) => throw new NotSupportedException();
            public PaymentResumeResult ResumeReservation(OCPPCoreContext dbContext, Guid reservationId) => throw new NotSupportedException();
            public PaymentR1InvoiceResult RequestR1Invoice(OCPPCoreContext dbContext, PaymentR1InvoiceRequest request) => throw new NotSupportedException();
            public void CancelReservation(OCPPCoreContext dbContext, Guid reservationId, string reason) { }
            public void CancelPaymentIntentIfCancelable(OCPPCoreContext dbContext, ChargePaymentReservation reservation, string reason) { }
            public void MarkTransactionStarted(OCPPCoreContext dbContext, string chargePointId, int connectorId, string chargeTagId, int transactionId) { }
            public void CompleteReservation(OCPPCoreContext dbContext, Transaction transaction) { }
            public void HandleWebhookEvent(OCPPCoreContext dbContext, string payload, string signatureHeader) { }
        }
    }
}
