using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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

        public PaymentReservationCleanupService(
            IServiceScopeFactory scopeFactory,
            ILogger<PaymentReservationCleanupService> logger,
            IConfiguration configuration)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
            _configuration = configuration;

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

            int pendingTimeoutMinutes = _configuration.GetValue<int?>("Maintenance:ReservationTimeoutMinutes") ?? 60;
            if (pendingTimeoutMinutes <= 0) return;

            var cutoff = DateTime.UtcNow.AddMinutes(-pendingTimeoutMinutes);
            var stale = await db.ChargePaymentReservations
                .Where(r =>
                    r.UpdatedAtUtc < cutoff &&
                    (r.Status == PaymentReservationStatus.Pending ||
                     r.Status == PaymentReservationStatus.Authorized ||
                     r.Status == PaymentReservationStatus.StartRequested))
                .ToListAsync(token);

            if (stale.Count == 0) return;

            foreach (var reservation in stale)
            {
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
                reservation.UpdatedAtUtc = DateTime.UtcNow;
            }

            await db.SaveChangesAsync(token);
            _logger.LogInformation("PaymentReservationCleanupService => cancelled {Count} stale reservations (>{Timeout} min)", stale.Count, pendingTimeoutMinutes);
        }
    }
}
