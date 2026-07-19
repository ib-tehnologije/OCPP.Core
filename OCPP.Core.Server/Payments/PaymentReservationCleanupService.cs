using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OCPP.Core.Database;

namespace OCPP.Core.Server.Payments
{
    /// <summary>
    /// Periodic cleanup to cancel stale payment reservations so connectors don’t stay locked.
    /// </summary>
    public class PaymentReservationCleanupService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<PaymentReservationCleanupService> _logger;
        private readonly IConfiguration _configuration;
        private readonly TimeSpan _interval;
        private readonly TimeSpan _availableStatusCloseGrace;
        private readonly int _startWindowMinutes;

        public PaymentReservationCleanupService(
            IServiceScopeFactory scopeFactory,
            ILogger<PaymentReservationCleanupService> logger,
            IConfiguration configuration,
            IOptions<PaymentFlowOptions> paymentOptions)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
            _configuration = configuration;
            _startWindowMinutes = Math.Max(1, paymentOptions?.Value?.StartWindowMinutes ?? 5);

            int intervalSeconds = _configuration.GetValue<int?>("Maintenance:CleanupIntervalSeconds") ?? 60;
            _interval = TimeSpan.FromSeconds(Math.Max(30, intervalSeconds));

            int availableStatusGraceMinutes = _configuration.GetValue<int?>("Maintenance:AvailableStatusOpenTransactionGraceMinutes") ?? 5;
            _availableStatusCloseGrace = TimeSpan.FromMinutes(Math.Max(0, availableStatusGraceMinutes));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_interval, stoppingToken);
                    await CleanupAsync(stoppingToken);
                }
                catch (TaskCanceledException)
                {
                    // shutting down
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "PaymentReservationCleanupService => sweep failed");
                }
            }
        }

        protected virtual DateTime UtcNow => DateTime.UtcNow;

        protected virtual async Task CleanupAsync(CancellationToken token)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OCPPCoreContext>();
            var coordinator = scope.ServiceProvider.GetService<IPaymentCoordinator>();

            int pendingTimeoutMinutes =
                _configuration.GetValue<int?>("Maintenance:PendingPaymentTimeoutMinutes") ??
                _configuration.GetValue<int?>("Maintenance:ReservationTimeoutMinutes") ??
                15;

            var now = UtcNow;
            var stale = Enumerable.Empty<ChargePaymentReservation>();
            if (pendingTimeoutMinutes > 0)
            {
                var cutoff = now.AddMinutes(-pendingTimeoutMinutes);
                stale = await db.ChargePaymentReservations
                    .Where(r =>
                        r.UpdatedAtUtc < cutoff &&
                        r.Status == PaymentReservationStatus.Pending)
                    .ToListAsync(token);
            }

            var startDeadline = now;
            var timedOutStarts = await db.ChargePaymentReservations
                .Where(r =>
                    (r.Status == PaymentReservationStatus.Authorized ||
                     r.Status == PaymentReservationStatus.StartRequested) &&
                    r.StartDeadlineAtUtc.HasValue &&
                    r.StartDeadlineAtUtc <= startDeadline &&
                    r.TransactionId == null)
                .ToListAsync(token);

            var availableOpenTransactions = await LoadAvailableOpenTransactionsAsync(db, now, token);
            var waitingForDisconnectReservations = await LoadWaitingForDisconnectAvailableReservationsAsync(db, now, token);
            var inProgressTimeoutMinutes = Math.Clamp(
                _configuration.GetValue<int?>("Maintenance:AuthorizationReleaseInProgressTimeoutMinutes") ?? 5,
                1,
                60);
            var expiredInProgressCutoff = now.AddMinutes(-inProgressTimeoutMinutes);
            var dueAuthorizationReleases = await db.ChargePaymentReservations
                .Where(r =>
                    (r.Status == PaymentReservationStatus.Abandoned ||
                     r.Status == PaymentReservationStatus.Failed) &&
                    (((r.AuthorizationReleaseState == PaymentAuthorizationReleaseState.Pending ||
                       r.AuthorizationReleaseState == PaymentAuthorizationReleaseState.RetryScheduled) &&
                      (!r.AuthorizationReleaseNextAttemptAtUtc.HasValue ||
                       r.AuthorizationReleaseNextAttemptAtUtc.Value <= now)) ||
                     (r.AuthorizationReleaseState == PaymentAuthorizationReleaseState.InProgress &&
                      (!r.AuthorizationReleaseLastAttemptAtUtc.HasValue ||
                       r.AuthorizationReleaseLastAttemptAtUtc.Value <= expiredInProgressCutoff))))
                .ToListAsync(token);

            if (!stale.Any() &&
                !timedOutStarts.Any() &&
                !availableOpenTransactions.Any() &&
                !waitingForDisconnectReservations.Any() &&
                !dueAuthorizationReleases.Any()) return;

            foreach (var reservation in stale)
            {
                var previousStatus = reservation.Status;
                var previousUpdatedAt = reservation.UpdatedAtUtc;
                reservation.Status = PaymentReservationStatus.Abandoned;
                const string cleanupMessage = "Auto-cancelled: stale reservation (background sweep)";
                if (string.IsNullOrWhiteSpace(reservation.LastError))
                {
                    reservation.LastError = cleanupMessage;
                }
                reservation.FailureCode = "CleanupTimeout";
                reservation.FailureMessage = cleanupMessage;
                reservation.AuthorizationReleaseState = PaymentAuthorizationReleaseState.Pending;
                reservation.AuthorizationReleaseNextAttemptAtUtc = null;
                reservation.UpdatedAtUtc = now;

                _logger.LogInformation(
                    "PaymentReservationCleanup => Arming stale reservation for authorization release reservation={ReservationId} cp={ChargePointId} connector={ConnectorId} previousStatus={Status} lastUpdate={LastUpdate:u}",
                    reservation.ReservationId,
                    reservation.ChargePointId,
                    reservation.ConnectorId,
                    previousStatus,
                    previousUpdatedAt);
            }

            foreach (var reservation in timedOutStarts)
            {
                _logger.LogWarning(
                    "PaymentReservationCleanup => Start window expired reservation={ReservationId} cp={ChargePointId} connector={ConnectorId} status={Status} deadline={Deadline:u}",
                    reservation.ReservationId,
                    reservation.ChargePointId,
                    reservation.ConnectorId,
                    reservation.Status,
                    reservation.StartDeadlineAtUtc);

                try
                {
                    coordinator?.CancelPaymentIntentIfCancelable(db, reservation, "Start window expired");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "PaymentReservationCleanup => CancelPaymentIntent failed reservation={ReservationId}", reservation.ReservationId);
                }

                reservation.Status = PaymentReservationStatus.StartTimeout;
                reservation.LastError = "Start window expired without transaction.";
                reservation.FailureCode = "StartTimeout";
                reservation.FailureMessage = reservation.LastError;
                reservation.UpdatedAtUtc = now;
            }

            foreach (var availableOpenTransaction in availableOpenTransactions)
            {
                var transaction = availableOpenTransaction.Transaction;
                var connectorStatus = availableOpenTransaction.ConnectorStatus;

                LogOpenTransactionRecoveryCandidate(availableOpenTransaction, now);

                if (!OpenTransactionRecovery.TryCloseForAvailableConnector(
                    db,
                    transaction,
                    connectorStatus.ChargePointId,
                    connectorStatus.ConnectorId,
                    connectorStatus.LastStatusTime.Value,
                    connectorStatus.LastMeter,
                    _logger,
                    "PaymentReservationCleanup"))
                {
                    continue;
                }

                try
                {
                    coordinator?.CompleteReservation(db, transaction);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "PaymentReservationCleanup => CompleteReservation failed for recovered transaction reservation={ReservationId} tx={TransactionId}",
                        availableOpenTransaction.Reservation.ReservationId,
                        transaction.TransactionId);
                }
            }

            foreach (var waitingForDisconnect in waitingForDisconnectReservations)
            {
                LogWaitingForDisconnectRecoveryCandidate(waitingForDisconnect, now);

                try
                {
                    coordinator?.CompleteReservation(db, waitingForDisconnect.Transaction);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "PaymentReservationCleanup => CompleteReservation failed for WaitingForDisconnect reservation={ReservationId} tx={TransactionId}",
                        waitingForDisconnect.Reservation.ReservationId,
                        waitingForDisconnect.Transaction.TransactionId);
                }
            }

            // Persist stale terminal state and arm the release before the strict reconciler
            // calls the provider. A crash or restart leaves a durable candidate for the next sweep.
            await db.SaveChangesAsync(token);

            if (coordinator != null)
            {
                var releaseCandidates = dueAuthorizationReleases
                    .Concat(stale)
                    .GroupBy(r => r.ReservationId)
                    .Select(group => group.First())
                    .ToList();

                foreach (var reservation in releaseCandidates)
                {
                    try
                    {
                        coordinator.ReconcileTerminalPaymentAuthorization(
                            db,
                            reservation,
                            PaymentAuthorizationReleaseTrigger.CleanupSweep);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "PaymentReservationCleanup => authorization release reconciliation failed reservation={ReservationId}",
                            reservation.ReservationId);
                    }
                }
            }

            if (stale.Any())
            {
                _logger.LogInformation("PaymentReservationCleanupService => abandoned {Count} stale pending reservations (>{Timeout} min)", stale.Count(), pendingTimeoutMinutes);
            }
            if (timedOutStarts.Any())
            {
                _logger.LogInformation("PaymentReservationCleanupService => marked {Count} reservations as StartTimeout (>{Window} min window)", timedOutStarts.Count, _startWindowMinutes);
            }
            if (availableOpenTransactions.Any())
            {
                _logger.LogWarning(
                    "PaymentReservationCleanupService => recovered {Count} open transactions from persisted Available connector status",
                    availableOpenTransactions.Count);
            }
            if (waitingForDisconnectReservations.Any())
            {
                _logger.LogWarning(
                    "PaymentReservationCleanupService => retried completion for {Count} WaitingForDisconnect reservations from persisted Available connector status",
                    waitingForDisconnectReservations.Count);
            }
        }

        private async Task<List<AvailableOpenTransaction>> LoadAvailableOpenTransactionsAsync(
            OCPPCoreContext db,
            DateTime now,
            CancellationToken token)
        {
            if (_availableStatusCloseGrace <= TimeSpan.Zero)
            {
                return new List<AvailableOpenTransaction>();
            }

            DateTime cutoff = now.Subtract(_availableStatusCloseGrace);

            return await db.ChargePaymentReservations
                .Where(r =>
                    r.Status == PaymentReservationStatus.Charging &&
                    r.TransactionId.HasValue)
                .Join(
                    db.Transactions,
                    reservation => reservation.TransactionId.Value,
                    transaction => transaction.TransactionId,
                    (reservation, transaction) => new { Reservation = reservation, Transaction = transaction })
                .Join(
                    db.ConnectorStatuses,
                    joined => new { joined.Reservation.ChargePointId, joined.Reservation.ConnectorId },
                    connectorStatus => new { connectorStatus.ChargePointId, connectorStatus.ConnectorId },
                    (joined, connectorStatus) => new AvailableOpenTransaction
                    {
                        Reservation = joined.Reservation,
                        Transaction = joined.Transaction,
                        ConnectorStatus = connectorStatus
                    })
                .Where(item =>
                    !item.Transaction.StopTime.HasValue &&
                    item.ConnectorStatus.LastStatus == OcppConnectorStatus.Available &&
                    item.ConnectorStatus.LastStatusTime.HasValue &&
                    item.ConnectorStatus.LastStatusTime.Value >= item.Transaction.StartTime &&
                    item.ConnectorStatus.LastStatusTime.Value <= cutoff)
                .ToListAsync(token);
        }

        private async Task<List<AvailableOpenTransaction>> LoadWaitingForDisconnectAvailableReservationsAsync(
            OCPPCoreContext db,
            DateTime now,
            CancellationToken token)
        {
            if (_availableStatusCloseGrace <= TimeSpan.Zero)
            {
                return new List<AvailableOpenTransaction>();
            }

            DateTime cutoff = now.Subtract(_availableStatusCloseGrace);

            return await db.ChargePaymentReservations
                .Where(r =>
                    r.Status == PaymentReservationStatus.WaitingForDisconnect &&
                    r.TransactionId.HasValue)
                .Join(
                    db.Transactions,
                    reservation => reservation.TransactionId.Value,
                    transaction => transaction.TransactionId,
                    (reservation, transaction) => new { Reservation = reservation, Transaction = transaction })
                .Join(
                    db.ConnectorStatuses,
                    joined => new { joined.Reservation.ChargePointId, joined.Reservation.ConnectorId },
                    connectorStatus => new { connectorStatus.ChargePointId, connectorStatus.ConnectorId },
                    (joined, connectorStatus) => new AvailableOpenTransaction
                    {
                        Reservation = joined.Reservation,
                        Transaction = joined.Transaction,
                        ConnectorStatus = connectorStatus
                    })
                .Where(item =>
                    item.Transaction.StopTime.HasValue &&
                    item.ConnectorStatus.LastStatus == OcppConnectorStatus.Available &&
                    item.ConnectorStatus.LastStatusTime.HasValue &&
                    item.ConnectorStatus.LastStatusTime.Value >= item.Transaction.StartTime &&
                    item.ConnectorStatus.LastStatusTime.Value <= cutoff)
                .ToListAsync(token);
        }

        private void LogOpenTransactionRecoveryCandidate(AvailableOpenTransaction item, DateTime now)
        {
            if (item?.Transaction == null || item.ConnectorStatus == null)
            {
                return;
            }

            var transaction = item.Transaction;
            var connectorStatus = item.ConnectorStatus;
            var reservation = item.Reservation;

            double? deliveredKwh = null;
            if (connectorStatus.LastMeter.HasValue)
            {
                deliveredKwh = Math.Max(0, connectorStatus.LastMeter.Value - transaction.MeterStart);
            }

            _logger.LogWarning(
                "PaymentReservationCleanup => Open transaction recovery candidate cp={ChargePointId} connector={ConnectorId} tx={TransactionId} txStart={TransactionStart:u} txAgeMinutes={TransactionAgeMinutes} meterStart={MeterStart} currentMeterStop={CurrentMeterStop} connectorMeter={ConnectorMeter} deliveredKwh={DeliveredKwh} status={ConnectorStatus} connectorStatusAt={ConnectorStatusAt:u} statusAgeMinutes={StatusAgeMinutes} connectorMeterAt={ConnectorMeterAt:u} meterAgeMinutes={MeterAgeMinutes} reservation={ReservationId} reservationStatus={ReservationStatus} reservationUpdatedAt={ReservationUpdatedAt:u}",
                transaction.ChargePointId,
                transaction.ConnectorId,
                transaction.TransactionId,
                transaction.StartTime,
                WholeMinutes(now - transaction.StartTime),
                transaction.MeterStart,
                transaction.MeterStop,
                connectorStatus.LastMeter,
                deliveredKwh,
                connectorStatus.LastStatus,
                connectorStatus.LastStatusTime,
                connectorStatus.LastStatusTime.HasValue ? WholeMinutes(now - connectorStatus.LastStatusTime.Value) : (int?)null,
                connectorStatus.LastMeterTime,
                connectorStatus.LastMeterTime.HasValue ? WholeMinutes(now - connectorStatus.LastMeterTime.Value) : (int?)null,
                reservation?.ReservationId,
                reservation?.Status,
                reservation?.UpdatedAtUtc);
        }

        private void LogWaitingForDisconnectRecoveryCandidate(AvailableOpenTransaction item, DateTime now)
        {
            if (item?.Transaction == null || item.ConnectorStatus == null)
            {
                return;
            }

            var transaction = item.Transaction;
            var connectorStatus = item.ConnectorStatus;
            var reservation = item.Reservation;

            _logger.LogWarning(
                "PaymentReservationCleanup => WaitingForDisconnect recovery candidate cp={ChargePointId} connector={ConnectorId} tx={TransactionId} txStart={TransactionStart:u} txStop={TransactionStop:u} stopReason={StopReason} connectorStatus={ConnectorStatus} connectorStatusAt={ConnectorStatusAt:u} statusAgeMinutes={StatusAgeMinutes} reservation={ReservationId} reservationUpdatedAt={ReservationUpdatedAt:u}",
                transaction.ChargePointId,
                transaction.ConnectorId,
                transaction.TransactionId,
                transaction.StartTime,
                transaction.StopTime,
                transaction.StopReason,
                connectorStatus.LastStatus,
                connectorStatus.LastStatusTime,
                connectorStatus.LastStatusTime.HasValue ? WholeMinutes(now - connectorStatus.LastStatusTime.Value) : (int?)null,
                reservation?.ReservationId,
                reservation?.UpdatedAtUtc);
        }

        private static int WholeMinutes(TimeSpan value)
        {
            return (int)Math.Floor(value.TotalMinutes);
        }

        private sealed class AvailableOpenTransaction
        {
            public ChargePaymentReservation Reservation { get; set; }
            public Transaction Transaction { get; set; }
            public ConnectorStatus ConnectorStatus { get; set; }
        }
    }
}
