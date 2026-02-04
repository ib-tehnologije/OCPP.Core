using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OCPP.Core.Database;
using OCPP.Core.Server.Payments;

namespace OCPP.Core.Server
{
    /// <summary>
    /// One-time cleanup to keep occupancy logic sane after restarts or stalled sessions.
    /// </summary>
    public static class StartupMaintenance
    {
        public static void Run(OCPPCoreContext dbContext, ILogger logger, IConfiguration configuration, Func<DateTime> utcNow = null)
        {
            if (dbContext == null || configuration == null) return;

            utcNow ??= () => DateTime.UtcNow;
            var now = utcNow();

            int reservationTimeoutMinutes = configuration.GetValue<int?>("Maintenance:ReservationTimeoutMinutes") ?? 60;
            int statusReleaseMinutes = configuration.GetValue<int?>("Maintenance:StatusReleaseMinutes") ?? 240;

            int cancelledReservations = 0;
            int releasedStatuses = 0;

            try
            {
                cancelledReservations = CancelStaleReservations(dbContext, now, reservationTimeoutMinutes, logger);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "StartupMaintenance => Failed to cancel stale reservations");
            }

            try
            {
                releasedStatuses = ReleaseStaleConnectorStatuses(dbContext, now, statusReleaseMinutes, logger);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "StartupMaintenance => Failed to release stale connector statuses");
            }

            if (cancelledReservations > 0 || releasedStatuses > 0)
            {
                logger?.LogInformation("StartupMaintenance => Cancelled {Cancelled} stale reservations; released {Released} stale connector statuses",
                    cancelledReservations,
                    releasedStatuses);
            }
        }

        private static int CancelStaleReservations(OCPPCoreContext dbContext, DateTime now, int timeoutMinutes, ILogger logger = null)
        {
            if (timeoutMinutes <= 0) return 0;

            var cutoff = now.AddMinutes(-timeoutMinutes);
            var stale = dbContext.ChargePaymentReservations
                .Where(r =>
                    r.UpdatedAtUtc < cutoff &&
                    (r.Status == PaymentReservationStatus.Pending ||
                     r.Status == PaymentReservationStatus.Authorized ||
                     r.Status == PaymentReservationStatus.StartRequested))
                .ToList();

            foreach (var reservation in stale)
            {
                logger?.LogInformation(
                    "StartupMaintenance => Cancelling stale reservation={ReservationId} cp={ChargePointId} connector={ConnectorId} status={Status} lastUpdate={LastUpdate:u}",
                    reservation.ReservationId,
                    reservation.ChargePointId,
                    reservation.ConnectorId,
                    reservation.Status,
                    reservation.UpdatedAtUtc);

                reservation.Status = PaymentReservationStatus.Cancelled;
                reservation.LastError = "Auto-cancelled: stale reservation at startup";
                reservation.UpdatedAtUtc = now;
            }

            if (stale.Count > 0)
            {
                dbContext.SaveChanges();
            }

            return stale.Count;
        }

        private static int ReleaseStaleConnectorStatuses(OCPPCoreContext dbContext, DateTime now, int releaseMinutes, ILogger logger = null)
        {
            if (releaseMinutes <= 0) return 0;

            var cutoff = now.AddMinutes(-releaseMinutes);
            var staleStatuses = dbContext.ConnectorStatuses
                .Where(cs =>
                    cs.LastStatusTime.HasValue &&
                    cs.LastStatusTime < cutoff &&
                    cs.LastStatus != null)
                .ToList()
                .Where(cs => !string.Equals(cs.LastStatus, "Available", StringComparison.InvariantCultureIgnoreCase))
                .ToList();

            int released = 0;

            foreach (var status in staleStatuses)
            {
                bool hasOpenTransaction = dbContext.Transactions.Any(t =>
                    t.ChargePointId == status.ChargePointId &&
                    t.ConnectorId == status.ConnectorId &&
                    t.StopTime == null);

                bool hasActiveReservation = false;
                try
                {
                    hasActiveReservation = dbContext.ChargePaymentReservations.Any(r =>
                        r.ChargePointId == status.ChargePointId &&
                        r.ConnectorId == status.ConnectorId &&
                        EF.Property<string>(r, "ActiveConnectorKey") == "ACTIVE");
                }
                catch
                {
                    // Older databases may not have reservations; ignore and continue.
                }

                if (hasOpenTransaction || hasActiveReservation)
                {
                    logger?.LogInformation(
                        "StartupMaintenance => Skipping release cp={ChargePointId} connector={ConnectorId} hasOpenTx={HasOpenTx} hasActiveRes={HasActiveRes}",
                        status.ChargePointId,
                        status.ConnectorId,
                        hasOpenTransaction,
                        hasActiveReservation);
                    continue;
                }

                logger?.LogInformation(
                    "StartupMaintenance => Releasing stale status cp={ChargePointId} connector={ConnectorId} previousStatus={PreviousStatus} lastStatusTime={LastStatusTime:u}",
                    status.ChargePointId,
                    status.ConnectorId,
                    status.LastStatus,
                    status.LastStatusTime);

                status.LastStatus = "Available";
                status.LastStatusTime = now;
                released++;
            }

            if (released > 0)
            {
                dbContext.SaveChanges();
            }

            return released;
        }
    }
}
