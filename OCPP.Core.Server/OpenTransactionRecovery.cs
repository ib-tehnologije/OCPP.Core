using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OCPP.Core.Database;

namespace OCPP.Core.Server
{
    internal static class OpenTransactionRecovery
    {
        public const string ConnectorAvailableStopReason = "ConnectorAvailableWithoutStopTransaction";

        public static bool TryCloseForAvailableConnector(
            OCPPCoreContext dbContext,
            Transaction transaction,
            string chargePointId,
            int connectorId,
            DateTime availableAtUtc,
            double? liveMeterKwh,
            ILogger logger,
            string source)
        {
            if (dbContext == null ||
                transaction == null ||
                string.IsNullOrWhiteSpace(chargePointId) ||
                connectorId <= 0 ||
                transaction.StopTime.HasValue ||
                !string.Equals(transaction.ChargePointId, chargePointId, StringComparison.OrdinalIgnoreCase) ||
                transaction.ConnectorId != connectorId)
            {
                return false;
            }

            DateTime stopTimeUtc = ResolveStopTimeUtc(availableAtUtc, transaction.StartTime);
            double? previousMeterStop = transaction.MeterStop;
            double meterStop = ResolveMeterStop(dbContext, transaction, chargePointId, connectorId, liveMeterKwh);

            if (!transaction.MeterStop.HasValue ||
                transaction.MeterStop.Value < transaction.MeterStart ||
                meterStop > transaction.MeterStop.Value)
            {
                transaction.MeterStop = meterStop;
            }

            transaction.StopTime = stopTimeUtc;
            transaction.StopReason = ConnectorAvailableStopReason;
            transaction.ChargingEndedAtUtc ??= stopTimeUtc;

            dbContext.SaveChanges();

            logger?.LogWarning(
                "{Source} => Closed open transaction after connector reported Available cp={ChargePointId} connector={ConnectorId} tx={TransactionId} startTime={StartTime:u} stopTime={StopTime:u} meterStart={MeterStart} previousMeterStop={PreviousMeterStop} resolvedMeterStop={MeterStop} liveMeterKwh={LiveMeterKwh} deliveredKwh={DeliveredKwh} stopReason={StopReason}",
                source,
                transaction.ChargePointId,
                transaction.ConnectorId,
                transaction.TransactionId,
                transaction.StartTime,
                transaction.StopTime,
                transaction.MeterStart,
                previousMeterStop,
                transaction.MeterStop,
                liveMeterKwh,
                transaction.MeterStop.HasValue ? Math.Max(0, transaction.MeterStop.Value - transaction.MeterStart) : (double?)null,
                transaction.StopReason);

            return true;
        }

        private static DateTime ResolveStopTimeUtc(DateTime availableAtUtc, DateTime startTime)
        {
            DateTime stopTimeUtc = availableAtUtc.Kind == DateTimeKind.Local
                ? availableAtUtc.ToUniversalTime()
                : DateTime.SpecifyKind(availableAtUtc, DateTimeKind.Utc);

            DateTime startTimeUtc = startTime.Kind == DateTimeKind.Local
                ? startTime.ToUniversalTime()
                : DateTime.SpecifyKind(startTime, DateTimeKind.Utc);

            if (stopTimeUtc >= startTimeUtc)
            {
                return stopTimeUtc;
            }

            DateTime nowUtc = DateTime.UtcNow;
            return nowUtc >= startTimeUtc ? nowUtc : startTimeUtc;
        }

        private static double ResolveMeterStop(
            OCPPCoreContext dbContext,
            Transaction transaction,
            string chargePointId,
            int connectorId,
            double? liveMeterKwh)
        {
            double meterStop = transaction.MeterStop.HasValue && transaction.MeterStop.Value >= transaction.MeterStart
                ? transaction.MeterStop.Value
                : transaction.MeterStart;

            if (liveMeterKwh.HasValue && liveMeterKwh.Value >= meterStop)
            {
                meterStop = liveMeterKwh.Value;
            }

            double? persistedMeter = dbContext.ConnectorStatuses
                .AsNoTracking()
                .Where(cs => cs.ChargePointId == chargePointId && cs.ConnectorId == connectorId)
                .Select(cs => cs.LastMeter)
                .FirstOrDefault();

            if (persistedMeter.HasValue && persistedMeter.Value >= meterStop)
            {
                meterStop = persistedMeter.Value;
            }

            return meterStop;
        }
    }
}
