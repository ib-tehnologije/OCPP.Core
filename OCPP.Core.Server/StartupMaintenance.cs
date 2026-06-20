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

            int reservationTimeoutMinutes =
                configuration.GetValue<int?>("Maintenance:PendingPaymentTimeoutMinutes") ??
                configuration.GetValue<int?>("Maintenance:ReservationTimeoutMinutes") ??
                15;
            int statusReleaseMinutes = configuration.GetValue<int?>("Maintenance:StatusReleaseMinutes") ?? 240;

            int cancelledReservations = 0;
            int repairedActiveKeys = 0;
            int releasedStatuses = 0;

            try
            {
                repairedActiveKeys = RepairReservationActiveConnectorKeys(dbContext, logger);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "StartupMaintenance => Failed to repair reservation active keys");
            }

            try
            {
                cancelledReservations = CancelStalePendingReservations(dbContext, now, reservationTimeoutMinutes, logger);
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

            if (repairedActiveKeys > 0 || cancelledReservations > 0 || releasedStatuses > 0)
            {
                logger?.LogInformation("StartupMaintenance => Repaired {Repaired} reservation active keys; cancelled {Cancelled} stale reservations; released {Released} stale connector statuses",
                    repairedActiveKeys,
                    cancelledReservations,
                    releasedStatuses);
            }
        }

        private static int RepairReservationActiveConnectorKeys(OCPPCoreContext dbContext, ILogger logger = null)
        {
            if (!ChargePaymentReservationActiveKey.RequiresManualSync(dbContext?.Database?.ProviderName))
            {
                return 0;
            }

            var reservations = dbContext.ChargePaymentReservations.ToList();
            var repaired = 0;

            foreach (var reservation in reservations)
            {
                var entry = dbContext.Entry(reservation);
                var property = entry.Property<string>("ActiveConnectorKey");
                var expected = ChargePaymentReservationActiveKey.Compute(reservation.ReservationId, reservation.Status);

                if (string.Equals(property.CurrentValue, expected, StringComparison.Ordinal))
                {
                    continue;
                }

                logger?.LogInformation(
                    "StartupMaintenance => Repairing active connector key reservation={ReservationId} cp={ChargePointId} connector={ConnectorId} status={Status} current={Current} expected={Expected}",
                    reservation.ReservationId,
                    reservation.ChargePointId,
                    reservation.ConnectorId,
                    reservation.Status,
                    property.CurrentValue ?? "(null)",
                    expected);

                property.CurrentValue = expected;
                repaired++;
            }

            if (repaired > 0)
            {
                dbContext.SaveChanges();
            }

            return repaired;
        }

        private static int CancelStalePendingReservations(OCPPCoreContext dbContext, DateTime now, int timeoutMinutes, ILogger logger = null)
        {
            if (timeoutMinutes <= 0) return 0;

            var cutoff = now.AddMinutes(-timeoutMinutes);
            var stale = dbContext.ChargePaymentReservations
                .Where(r =>
                    r.UpdatedAtUtc < cutoff &&
                    r.Status == PaymentReservationStatus.Pending)
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

                reservation.Status = PaymentReservationStatus.Abandoned;
                reservation.LastError = "Auto-cancelled: stale pending reservation at startup";
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
                        r.Status != null &&
                        PaymentReservationStatus.ConnectorLockStatuses.Contains(r.Status));
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
