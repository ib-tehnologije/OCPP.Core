using System;
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
            _startWindowMinutes = Math.Max(1, paymentOptions?.Value?.StartWindowMinutes ?? 7);

            int intervalSeconds = _configuration.GetValue<int?>("Maintenance:CleanupIntervalSeconds") ?? 60;
            _interval = TimeSpan.FromSeconds(Math.Max(30, intervalSeconds));
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

        protected virtual async Task CleanupAsync(CancellationToken token)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OCPPCoreContext>();
            var coordinator = scope.ServiceProvider.GetService<IPaymentCoordinator>();

            int pendingTimeoutMinutes = _configuration.GetValue<int?>("Maintenance:ReservationTimeoutMinutes") ?? 60;

            var now = DateTime.UtcNow;
            var stale = Enumerable.Empty<ChargePaymentReservation>();
            if (pendingTimeoutMinutes > 0)
            {
                var cutoff = now.AddMinutes(-pendingTimeoutMinutes);
                stale = await db.ChargePaymentReservations
                    .Where(r =>
                        r.UpdatedAtUtc < cutoff &&
                        (r.Status == PaymentReservationStatus.Pending ||
                         r.Status == PaymentReservationStatus.Authorized ||
                         r.Status == PaymentReservationStatus.StartRequested))
                    .ToListAsync(token);
            }

            var startDeadline = now;
            var timedOutStarts = await db.ChargePaymentReservations
                .Where(r =>
                    (r.Status == PaymentReservationStatus.Authorized ||
                     r.Status == PaymentReservationStatus.StartRequested) &&
                    r.StartDeadlineAtUtc.HasValue &&
                    r.StartDeadlineAtUtc < startDeadline &&
                    r.TransactionId == null)
                .ToListAsync(token);

            if (!stale.Any() && !timedOutStarts.Any()) return;

            foreach (var reservation in stale)
            {
                try
                {
                    coordinator?.CancelPaymentIntentIfCancelable(db, reservation, "Reservation stale");
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "PaymentReservationCleanup => CancelPaymentIntent stale failed reservation={ReservationId}", reservation.ReservationId);
                }

                _logger.LogInformation(
                    "PaymentReservationCleanup => Cancelling stale reservation={ReservationId} cp={ChargePointId} connector={ConnectorId} status={Status} lastUpdate={LastUpdate:u}",
                    reservation.ReservationId,
                    reservation.ChargePointId,
                    reservation.ConnectorId,
                    reservation.Status,
                    reservation.UpdatedAtUtc);
            }

            foreach (var reservation in stale)
            {
                reservation.Status = PaymentReservationStatus.Cancelled;
                reservation.LastError = "Auto-cancelled: stale reservation (background sweep)";
                reservation.FailureCode = "CleanupTimeout";
                reservation.FailureMessage = reservation.LastError;
                reservation.UpdatedAtUtc = now;
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

                reservation.Status = PaymentReservationStatus.Cancelled;
                reservation.LastError = "Start window expired without transaction.";
                reservation.FailureCode = "StartTimeout";
                reservation.FailureMessage = reservation.LastError;
                reservation.UpdatedAtUtc = now;
            }

            await db.SaveChangesAsync(token);
            if (stale.Any())
            {
                _logger.LogInformation("PaymentReservationCleanupService => cancelled {Count} stale reservations (>{Timeout} min)", stale.Count(), pendingTimeoutMinutes);
            }
            if (timedOutStarts.Any())
            {
                _logger.LogInformation("PaymentReservationCleanupService => marked {Count} reservations as StartTimeout (>{Window} min window)", timedOutStarts.Count, _startWindowMinutes);
            }
        }
    }
}
