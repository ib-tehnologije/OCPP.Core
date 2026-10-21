using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
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
        public void Compute_MatchesSqlServerComputedColumnStatusList()
        {
            using var context = CreateSqlServerModelContext();
            var computedSql = GetActiveConnectorKeyComputedSql(context.GetService<IDesignTimeModel>().Model);
            var sqlInactiveStatuses = ParseComputedColumnInactiveStatuses(computedSql);

            Assert.Equal(
                ChargePaymentReservationState.InactiveStatuses.OrderBy(s => s, StringComparer.Ordinal),
                sqlInactiveStatuses.OrderBy(s => s, StringComparer.Ordinal));

            var reservationId = Guid.NewGuid();
            var computeMismatches = GetKnownReservationStatuses()
                .Where(status => IsActiveKey(ChargePaymentReservationActiveKey.Compute(reservationId, status)) == sqlInactiveStatuses.Contains(status))
                .ToList();
            Assert.Empty(computeMismatches);

            // The unique index must hold exactly the statuses the portal and middleware treat as locking the connector.
            var lockMismatches = GetKnownReservationStatuses()
                .Where(status => ChargePaymentReservationState.LocksConnector(status) == sqlInactiveStatuses.Contains(status))
                .ToList();
            Assert.Empty(lockMismatches);
        }

        [Fact]
        public void MigrationSnapshot_ActiveConnectorKeyComputedSql_MatchesModel()
        {
            using var context = CreateSqlServerModelContext();
            var snapshot = context.GetService<IMigrationsAssembly>().ModelSnapshot;

            Assert.NotNull(snapshot);
            Assert.Equal(
                GetActiveConnectorKeyComputedSql(context.GetService<IDesignTimeModel>().Model),
                GetActiveConnectorKeyComputedSql(snapshot!.Model));
        }

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

        [Fact]
        public void StartupMaintenance_Run_ArmsNewlyAbandonedStaleReservationForRelease()
        {
            using var connection = CreateConnection();
            using var context = CreateContext(connection);
            var now = new DateTime(2026, 7, 18, 12, 0, 0, DateTimeKind.Utc);
            var reservation = CreateReservation(PaymentReservationStatus.Pending);
            reservation.StripePaymentIntentId = "pi_startup_stale";
            reservation.CreatedAtUtc = now.AddHours(-1);
            reservation.UpdatedAtUtc = now.AddHours(-1);
            var historicalReservation = CreateReservation(PaymentReservationStatus.Abandoned);
            historicalReservation.StripePaymentIntentId = "pi_historical_terminal";
            historicalReservation.CreatedAtUtc = now.AddDays(-30);
            historicalReservation.UpdatedAtUtc = now.AddDays(-30);
            context.ChargePaymentReservations.AddRange(reservation, historicalReservation);
            context.SaveChanges();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new System.Collections.Generic.Dictionary<string, string?>
                {
                    ["Maintenance:PendingPaymentTimeoutMinutes"] = "1",
                    ["Maintenance:StatusReleaseMinutes"] = "0"
                })
                .Build();

            StartupMaintenance.Run(context, NullLogger.Instance, configuration, () => now);

            Assert.Equal(PaymentReservationStatus.Abandoned, reservation.Status);
            Assert.Equal(PaymentAuthorizationReleaseState.Pending, reservation.AuthorizationReleaseState);
            Assert.Null(reservation.AuthorizationReleaseNextAttemptAtUtc);
            Assert.Equal(0, reservation.AuthorizationReleaseAttemptCount);
            Assert.Null(historicalReservation.AuthorizationReleaseState);
            Assert.Equal(0, historicalReservation.AuthorizationReleaseAttemptCount);
        }

        [Theory]
        [InlineData(PaymentReservationStatus.Cancelled)]
        [InlineData(PaymentReservationStatus.Failed)]
        [InlineData(PaymentReservationStatus.Completed)]
        [InlineData(PaymentReservationStatus.Abandoned)]
        [InlineData(PaymentReservationStatus.StartRejected)]
        [InlineData(PaymentReservationStatus.StartTimeout)]
        [InlineData(PaymentReservationStatus.ReviewRequired)]
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

        private static OCPPCoreContext CreateSqlServerModelContext()
        {
            // Builds the SQL Server model only; no connection is opened.
            var options = new DbContextOptionsBuilder<OCPPCoreContext>()
                .UseSqlServer("Server=model-only;Database=model-only")
                .Options;

            return new OCPPCoreContext(options);
        }

        private static string GetActiveConnectorKeyComputedSql(IReadOnlyModel model)
        {
            var computedSql = model
                .FindEntityType(typeof(ChargePaymentReservation).FullName!)?
                .FindProperty("ActiveConnectorKey")?
                .GetComputedColumnSql();

            Assert.False(string.IsNullOrWhiteSpace(computedSql), "ActiveConnectorKey has no SQL Server computed column SQL.");
            return computedSql!;
        }

        private static HashSet<string> ParseComputedColumnInactiveStatuses(string computedSql)
        {
            var match = Regex.Match(
                computedSql,
                @"^CASE WHEN \[Status\] NOT IN \(([^)]*)\) THEN '" + ChargePaymentReservationActiveKey.ActiveValue + "' ELSE ");
            Assert.True(match.Success, $"Unexpected ActiveConnectorKey computed column SQL: {computedSql}");

            return match.Groups[1].Value
                .Split(',')
                .Select(status => status.Trim().Trim('\''))
                .ToHashSet(StringComparer.Ordinal);
        }

        private static IEnumerable<string> GetKnownReservationStatuses()
        {
            return typeof(ChargePaymentReservationState)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(field => field.IsLiteral && field.FieldType == typeof(string))
                .Select(field => (string)field.GetRawConstantValue()!);
        }

        private static bool IsActiveKey(string activeConnectorKey)
        {
            return string.Equals(activeConnectorKey, ChargePaymentReservationActiveKey.ActiveValue, StringComparison.Ordinal);
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
            IConfiguration configuration = new ConfigurationBuilder().Build();

            return new OCPPMiddleware(
                _ => Task.CompletedTask,
                NullLoggerFactory.Instance,
                configuration,
                services.GetRequiredService<IServiceScopeFactory>(),
                new NoopPaymentCoordinator(),
                mediator,
                new ReservationLinkService(mediator),
                new OcppMessageDumpService(
                    configuration,
                    NullLogger<OcppMessageDumpService>.Instance));
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
            public void HandleConnectorAvailable(OCPPCoreContext dbContext, string chargePointId, int connectorId, DateTime disconnectedAtUtc) { }
            public void HandleWebhookEvent(OCPPCoreContext dbContext, string payload, string signatureHeader) { }
        }
    }
}
