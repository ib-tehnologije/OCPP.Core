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
            var completedChargingReservations = await LoadCompletedChargingAvailableReservationsAsync(db, now, token);
            var waitingForDisconnectReservations = await LoadWaitingForDisconnectAvailableReservationsAsync(db, now, token);
            var settlementRetries = await LoadSettlementRetryReservationsAsync(
                db,
                now,
                completedChargingReservations.Concat(waitingForDisconnectReservations).Select(item => item.Reservation.ReservationId),
                token);
            var inProgressTimeoutMinutes = Math.Clamp(
                _configuration.GetValue<int?>("Maintenance:AuthorizationReleaseInProgressTimeoutMinutes") ?? 5,
                1,
                60);
            var expiredInProgressCutoff = now.AddMinutes(-inProgressTimeoutMinutes);
            // Every status a reservation can end in without a charging session; the reconciler re-checks
            // transaction, capture and invoice evidence before it releases anything.
            var dueAuthorizationReleases = await db.ChargePaymentReservations
                .Where(r =>
                    (r.Status == PaymentReservationStatus.Abandoned ||
                     r.Status == PaymentReservationStatus.Failed ||
                     r.Status == PaymentReservationStatus.Cancelled ||
                     r.Status == PaymentReservationStatus.StartRejected ||
                     r.Status == PaymentReservationStatus.StartTimeout) &&
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
                !completedChargingReservations.Any() &&
                !waitingForDisconnectReservations.Any() &&
                !settlementRetries.Any() &&
                !dueAuthorizationReleases.Any()) return;

            var abandoned = new List<ChargePaymentReservation>();
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
                PaymentCoordinator.ArmAuthorizationReleaseState(reservation);
                reservation.UpdatedAtUtc = now;

                // Abandon only a row that is still the stale pending row read above: a browser return or provider
                // notification may be authorizing it right now, and then it must neither end nor be released.
                if (!PaymentCoordinator.TryCommitReservationTransition(
                        db,
                        reservation,
                        previousStatus,
                        row => row.UpdatedAtUtc == previousUpdatedAt,
                        saveWhenNotRelational: false))
                {
                    _logger.LogInformation(
                        "PaymentReservationCleanup => Stale reservation changed before it was abandoned reservation={ReservationId} status={Status}",
                        reservation.ReservationId,
                        reservation.Status);
                    continue;
                }

                abandoned.Add(reservation);
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

                // End the reservation before its hold is cancelled, and only while no transaction has linked it: a
                // late StartTransaction that wins keeps the reservation charging on its uncancelled hold.
                var loadedStatus = reservation.Status;
                reservation.Status = PaymentReservationStatus.StartTimeout;
                ApplyStartTimeoutOutcome(reservation, now);
                if (!string.IsNullOrWhiteSpace(reservation.StripePaymentIntentId))
                {
                    // Armed in the same conditional update, so a stop or failure before the cancel below is confirmed
                    // leaves the release to a later sweep; a confirmed cancel marks it released.
                    PaymentCoordinator.ArmAuthorizationReleaseState(reservation);
                }

                if (!PaymentCoordinator.TryCommitReservationTransition(
                        db,
                        reservation,
                        loadedStatus,
                        row => row.TransactionId == null,
                        saveWhenNotRelational: false))
                {
                    _logger.LogInformation(
                        "PaymentReservationCleanup => Reservation changed before its start window was closed reservation={ReservationId} status={Status}",
                        reservation.ReservationId,
                        reservation.Status);
                    continue;
                }

                try
                {
                    coordinator?.CancelPaymentIntentIfCancelable(db, reservation, "Start window expired");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "PaymentReservationCleanup => CancelPaymentIntent failed reservation={ReservationId}", reservation.ReservationId);
                }

                // The cancel records its own problems in the release fields; the timeout stays the outcome.
                ApplyStartTimeoutOutcome(reservation, now);
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

            foreach (var completedCharging in completedChargingReservations)
            {
                LogCompletedChargingRecoveryCandidate(completedCharging, now);

                try
                {
                    coordinator?.CompleteReservation(db, completedCharging.Transaction);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "PaymentReservationCleanup => CompleteReservation failed for completed Charging reservation={ReservationId} tx={TransactionId}",
                        completedCharging.Reservation.ReservationId,
                        completedCharging.Transaction.TransactionId);
                }
            }

            foreach (var settlementRetry in settlementRetries)
            {
                _logger.LogInformation(
                    "PaymentReservationCleanup => Retrying settlement after a transient provider failure reservation={ReservationId} tx={TransactionId} status={Status}",
                    settlementRetry.Reservation.ReservationId,
                    settlementRetry.Transaction.TransactionId,
                    settlementRetry.Reservation.Status);

                try
                {
                    coordinator?.CompleteReservation(db, settlementRetry.Transaction);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "PaymentReservationCleanup => CompleteReservation failed for settlement retry reservation={ReservationId} tx={TransactionId}",
                        settlementRetry.Reservation.ReservationId,
                        settlementRetry.Transaction.TransactionId);
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
                    .Concat(abandoned)
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

            if (abandoned.Any())
            {
                _logger.LogInformation("PaymentReservationCleanupService => abandoned {Count} stale pending reservations (>{Timeout} min)", abandoned.Count, pendingTimeoutMinutes);
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
            if (completedChargingReservations.Any())
            {
                _logger.LogWarning(
                    "PaymentReservationCleanupService => retried completion for {Count} Charging reservations with completed transactions and persisted Available connector status",
                    completedChargingReservations.Count);
            }
            if (waitingForDisconnectReservations.Any())
            {
                _logger.LogWarning(
                    "PaymentReservationCleanupService => retried completion for {Count} WaitingForDisconnect reservations from persisted Available connector status",
                    waitingForDisconnectReservations.Count);
            }
        }

        private static void ApplyStartTimeoutOutcome(ChargePaymentReservation reservation, DateTime now)
        {
            reservation.LastError = "Start window expired without transaction.";
            reservation.FailureCode = "StartTimeout";
            reservation.FailureMessage = reservation.LastError;
            reservation.UpdatedAtUtc = now;
        }

        /// <summary>
        /// Charging reservations whose settlement failed transiently (retry marker) on a stopped transaction, retried
        /// at most once per <see cref="SettlementRetryInterval"/> whatever the connector reports. Rows the
        /// connector-based recoveries already retry in this sweep are left to them.
        /// </summary>
        private static async Task<List<AvailableOpenTransaction>> LoadSettlementRetryReservationsAsync(
            OCPPCoreContext db,
            DateTime now,
            IEnumerable<Guid> alreadyRetried,
            CancellationToken token)
        {
            var cutoff = now.Subtract(SettlementRetryInterval);
            var retryCode = PaymentCoordinator.SettlementRetryPendingCode;
            var candidates = await db.ChargePaymentReservations
                .Where(r =>
                    (r.Status == PaymentReservationStatus.Charging ||
                     r.Status == PaymentReservationStatus.WaitingForDisconnect) &&
                    r.TransactionId.HasValue &&
                    r.FailureCode == retryCode &&
                    r.UpdatedAtUtc <= cutoff)
                .Join(
                    db.Transactions,
                    reservation => reservation.TransactionId.Value,
                    transaction => transaction.TransactionId,
                    (reservation, transaction) => new AvailableOpenTransaction
                    {
                        Reservation = reservation,
                        Transaction = transaction
                    })
                .Where(item => item.Transaction.StopTime.HasValue)
                .ToListAsync(token);

            var skip = new HashSet<Guid>(alreadyRetried);
            return candidates
                .Where(item => !skip.Contains(item.Reservation.ReservationId))
                .ToList();
        }

        private static readonly TimeSpan SettlementRetryInterval = TimeSpan.FromMinutes(1);

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

        private async Task<List<AvailableOpenTransaction>> LoadCompletedChargingAvailableReservationsAsync(
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
                    item.Transaction.StopTime.HasValue &&
                    item.Transaction.ChargePointId == item.Reservation.ChargePointId &&
                    item.Transaction.ConnectorId == item.Reservation.ConnectorId &&
                    item.ConnectorStatus.LastStatus == OcppConnectorStatus.Available &&
                    item.ConnectorStatus.LastStatusTime.HasValue &&
                    item.ConnectorStatus.LastStatusTime.Value >= item.Transaction.StopTime.Value &&
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

        private void LogCompletedChargingRecoveryCandidate(AvailableOpenTransaction item, DateTime now)
        {
            if (item?.Transaction == null || item.ConnectorStatus == null)
            {
                return;
            }

            var transaction = item.Transaction;
            var connectorStatus = item.ConnectorStatus;
            var reservation = item.Reservation;

            _logger.LogWarning(
                "PaymentReservationCleanup => Completed Charging recovery candidate cp={ChargePointId} connector={ConnectorId} tx={TransactionId} txStart={TransactionStart:u} txStop={TransactionStop:u} stopReason={StopReason} connectorStatus={ConnectorStatus} connectorStatusAt={ConnectorStatusAt:u} statusAgeMinutes={StatusAgeMinutes} reservation={ReservationId} reservationUpdatedAt={ReservationUpdatedAt:u}",
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
