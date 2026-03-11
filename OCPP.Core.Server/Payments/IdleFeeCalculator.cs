using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using OCPP.Core.Database;

namespace OCPP.Core.Server.Payments
{
    public sealed class IdleFeeSnapshot
    {
        public DateTime? SuspendedSinceUtc { get; set; }
        public DateTime? IdleFeeStartAtUtc { get; set; }
        public int AccumulatedMinutes { get; set; }
        public decimal AccumulatedAmount { get; set; }
        public int CurrentIntervalMinutes { get; set; }
        public decimal CurrentIntervalAmount { get; set; }
        public int TotalMinutes { get; set; }
        public decimal TotalAmount { get; set; }
    }

    internal static class IdleFeeCalculator
    {
        public static bool IsIdleFeeEnabled(ChargePaymentReservation reservation)
        {
            return reservation != null &&
                   reservation.UsageFeeAnchorMinutes == 1 &&
                   reservation.UsageFeePerMinute > 0 &&
                   reservation.MaxUsageFeeMinutes > 0;
        }

        public static IdleFeeSnapshot CalculateSnapshot(
            Transaction transaction,
            ChargePaymentReservation reservation,
            PaymentFlowOptions flowOptions,
            DateTime asOfUtc,
            ILogger logger = null)
        {
            var snapshot = new IdleFeeSnapshot();
            if (!IsIdleFeeEnabled(reservation) || transaction == null)
            {
                return snapshot;
            }

            int accumulatedMinutes = Math.Max(0, transaction.IdleUsageFeeMinutes);
            snapshot.AccumulatedMinutes = accumulatedMinutes;
            snapshot.AccumulatedAmount = CalculateAmount(accumulatedMinutes, reservation.UsageFeePerMinute);

            if (transaction.ChargingEndedAtUtc.HasValue)
            {
                snapshot.SuspendedSinceUtc = transaction.ChargingEndedAtUtc.Value;
                snapshot.IdleFeeStartAtUtc = CalculateIdleFeeStartAtUtc(
                    transaction.ChargingEndedAtUtc.Value,
                    reservation,
                    flowOptions,
                    logger);

                DateTime intervalEndUtc = transaction.StopTime ?? asOfUtc;
                if (intervalEndUtc > transaction.ChargingEndedAtUtc.Value)
                {
                    snapshot.CurrentIntervalMinutes = CalculateIntervalBillableMinutes(
                        transaction.ChargingEndedAtUtc.Value,
                        intervalEndUtc,
                        reservation,
                        flowOptions,
                        logger);
                }
            }

            int cappedTotalMinutes = Math.Min(
                accumulatedMinutes + Math.Max(0, snapshot.CurrentIntervalMinutes),
                Math.Max(0, reservation.MaxUsageFeeMinutes));

            snapshot.TotalMinutes = cappedTotalMinutes;
            snapshot.TotalAmount = CalculateAmount(snapshot.TotalMinutes, reservation.UsageFeePerMinute);

            snapshot.AccumulatedMinutes = Math.Min(snapshot.AccumulatedMinutes, snapshot.TotalMinutes);
            snapshot.AccumulatedAmount = CalculateAmount(snapshot.AccumulatedMinutes, reservation.UsageFeePerMinute);
            snapshot.CurrentIntervalMinutes = Math.Max(0, snapshot.TotalMinutes - snapshot.AccumulatedMinutes);
            snapshot.CurrentIntervalAmount = CalculateAmount(snapshot.CurrentIntervalMinutes, reservation.UsageFeePerMinute);

            return snapshot;
        }

        public static DateTime? CalculateIdleFeeStartAtUtc(
            DateTime suspendedSinceUtc,
            ChargePaymentReservation reservation,
            PaymentFlowOptions flowOptions,
            ILogger logger = null)
        {
            if (!IsIdleFeeEnabled(reservation))
            {
                return null;
            }

            DateTime candidateUtc = suspendedSinceUtc.AddMinutes(Math.Max(0, reservation.StartUsageFeeAfterMinutes));
            if (!TryParseDailyWindow(flowOptions?.IdleFeeExcludedWindow, out var excludedStart, out var excludedEnd) ||
                !TryResolveTimeZone(flowOptions?.IdleFeeExcludedTimeZoneId, out var timeZone))
            {
                return candidateUtc;
            }

            return AdvancePastExcludedWindow(candidateUtc, timeZone, excludedStart, excludedEnd, logger);
        }

        public static int CalculateIntervalBillableMinutes(
            DateTime intervalStartUtc,
            DateTime intervalEndUtc,
            ChargePaymentReservation reservation,
            PaymentFlowOptions flowOptions,
            ILogger logger = null)
        {
            if (!IsIdleFeeEnabled(reservation) || intervalEndUtc <= intervalStartUtc)
            {
                return 0;
            }

            int totalMinutes;
            if (TryParseDailyWindow(flowOptions?.IdleFeeExcludedWindow, out var excludedStart, out var excludedEnd) &&
                TryResolveTimeZone(flowOptions?.IdleFeeExcludedTimeZoneId, out var timeZone))
            {
                totalMinutes = CalculateChargeableMinutesExcludingWindow(
                    intervalStartUtc,
                    intervalEndUtc,
                    timeZone,
                    excludedStart,
                    excludedEnd,
                    logger);
            }
            else
            {
                totalMinutes = Math.Max(0, (int)Math.Ceiling((intervalEndUtc - intervalStartUtc).TotalMinutes));
            }

            return Math.Max(0, totalMinutes - Math.Max(0, reservation.StartUsageFeeAfterMinutes));
        }

        public static decimal CalculateAmount(int minutes, decimal pricePerMinute)
        {
            if (minutes <= 0 || pricePerMinute <= 0)
            {
                return 0m;
            }

            return Math.Round(minutes * pricePerMinute, 4, MidpointRounding.AwayFromZero);
        }

        private static DateTime AdvancePastExcludedWindow(
            DateTime utc,
            TimeZoneInfo timeZone,
            TimeSpan excludedStart,
            TimeSpan excludedEnd,
            ILogger logger)
        {
            DateTime currentUtc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            for (int i = 0; i < 366; i++)
            {
                DateTime currentLocal = TimeZoneInfo.ConvertTimeFromUtc(currentUtc, timeZone);
                if (!TryGetContainingExcludedWindowEnd(currentLocal, excludedStart, excludedEnd, out var excludedLocalEnd))
                {
                    return currentUtc;
                }

                try
                {
                    currentUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(excludedLocalEnd, DateTimeKind.Unspecified), timeZone);
                }
                catch (Exception ex)
                {
                    logger?.LogDebug(ex, "IdleFeeCalculator => Failed converting excluded window end to UTC tz={TimeZoneId} localEnd={LocalEnd:yyyy-MM-dd HH:mm:ss}", timeZone.Id, excludedLocalEnd);
                    return currentUtc;
                }
            }

            return currentUtc;
        }

        private static bool TryGetContainingExcludedWindowEnd(
            DateTime localTime,
            TimeSpan excludedStart,
            TimeSpan excludedEnd,
            out DateTime excludedLocalEnd)
        {
            excludedLocalEnd = default;

            if (excludedStart <= excludedEnd)
            {
                DateTime sameDayStart = localTime.Date.Add(excludedStart);
                DateTime sameDayEnd = localTime.Date.Add(excludedEnd);
                if (localTime >= sameDayStart && localTime < sameDayEnd)
                {
                    excludedLocalEnd = sameDayEnd;
                    return true;
                }

                return false;
            }

            DateTime todayStart = localTime.Date.Add(excludedStart);
            if (localTime >= todayStart)
            {
                excludedLocalEnd = localTime.Date.AddDays(1).Add(excludedEnd);
                return true;
            }

            DateTime overnightEnd = localTime.Date.Add(excludedEnd);
            if (localTime < overnightEnd)
            {
                excludedLocalEnd = overnightEnd;
                return true;
            }

            return false;
        }

        private static int CalculateChargeableMinutesExcludingWindow(
            DateTime intervalStartUtc,
            DateTime intervalEndUtc,
            TimeZoneInfo timeZone,
            TimeSpan excludedStartLocal,
            TimeSpan excludedEndLocal,
            ILogger logger)
        {
            if (intervalEndUtc <= intervalStartUtc)
            {
                return 0;
            }

            DateTime startUtc = DateTime.SpecifyKind(intervalStartUtc, DateTimeKind.Utc);
            DateTime endUtc = DateTime.SpecifyKind(intervalEndUtc, DateTimeKind.Utc);
            DateTime startLocal = TimeZoneInfo.ConvertTimeFromUtc(startUtc, timeZone);
            DateTime endLocal = TimeZoneInfo.ConvertTimeFromUtc(endUtc, timeZone);

            DateTime day = startLocal.Date.AddDays(-1);
            DateTime lastDay = endLocal.Date;
            double excludedSeconds = 0;

            while (day <= lastDay)
            {
                DateTime excludedLocalStart;
                DateTime excludedLocalEnd;
                if (excludedStartLocal <= excludedEndLocal)
                {
                    excludedLocalStart = day.Add(excludedStartLocal);
                    excludedLocalEnd = day.Add(excludedEndLocal);
                }
                else
                {
                    excludedLocalStart = day.Add(excludedStartLocal);
                    excludedLocalEnd = day.AddDays(1).Add(excludedEndLocal);
                }

                try
                {
                    DateTime excludedUtcStart = TimeZoneInfo.ConvertTimeToUtc(
                        DateTime.SpecifyKind(excludedLocalStart, DateTimeKind.Unspecified),
                        timeZone);
                    DateTime excludedUtcEnd = TimeZoneInfo.ConvertTimeToUtc(
                        DateTime.SpecifyKind(excludedLocalEnd, DateTimeKind.Unspecified),
                        timeZone);

                    DateTime overlapStart = excludedUtcStart > startUtc ? excludedUtcStart : startUtc;
                    DateTime overlapEnd = excludedUtcEnd < endUtc ? excludedUtcEnd : endUtc;
                    if (overlapEnd > overlapStart)
                    {
                        excludedSeconds += (overlapEnd - overlapStart).TotalSeconds;
                    }
                }
                catch (Exception ex)
                {
                    logger?.LogDebug(ex, "IdleFeeCalculator => Failed excluded-window UTC conversion tz={TimeZoneId} day={Day:yyyy-MM-dd}", timeZone.Id, day);
                }

                day = day.AddDays(1);
            }

            double totalSeconds = Math.Max(0, (endUtc - startUtc).TotalSeconds);
            double chargeableSeconds = Math.Max(0, totalSeconds - excludedSeconds);
            return (int)Math.Ceiling(chargeableSeconds / 60.0);
        }

        private static bool TryParseDailyWindow(string window, out TimeSpan start, out TimeSpan end)
        {
            start = default;
            end = default;
            if (string.IsNullOrWhiteSpace(window))
            {
                return false;
            }

            string[] parts = window.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return parts.Length == 2 &&
                   TimeSpan.TryParse(parts[0], out start) &&
                   TimeSpan.TryParse(parts[1], out end);
        }

        private static bool TryResolveTimeZone(string timeZoneId, out TimeZoneInfo timeZone)
        {
            timeZone = null;
            if (string.IsNullOrWhiteSpace(timeZoneId))
            {
                timeZone = TimeZoneInfo.Local;
                return timeZone != null;
            }

            var candidates = new List<string> { timeZoneId };
            if (string.Equals(timeZoneId, "Europe/Zagreb", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add("Central European Standard Time");
            }
            else if (string.Equals(timeZoneId, "Central European Standard Time", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add("Europe/Zagreb");
            }

            foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    timeZone = TimeZoneInfo.FindSystemTimeZoneById(candidate);
                    if (timeZone != null)
                    {
                        return true;
                    }
                }
                catch
                {
                    // Try next candidate.
                }
            }

            timeZone = TimeZoneInfo.Local;
            return timeZone != null;
        }
    }
}
