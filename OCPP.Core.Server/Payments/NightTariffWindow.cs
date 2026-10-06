using System;
using System.Collections.Concurrent;
using OCPP.Core.Database;

namespace OCPP.Core.Server.Payments
{
    /// <summary>
    /// Local-time night energy window, e.g. 22:00-07:00. The window is half-open: the start minute is night,
    /// the end minute is day. Instants are classified in the configured time zone, so daylight-saving nights are
    /// 8 or 10 elapsed hours long instead of 9.
    /// </summary>
    public sealed class NightTariffWindow
    {
        public const int DefaultStartMinute = 22 * 60;
        public const int DefaultEndMinute = 7 * 60;
        public const string DefaultTimeZoneId = "Europe/Zagreb";

        private static readonly ConcurrentDictionary<string, TimeZoneInfo> TimeZones = new(StringComparer.OrdinalIgnoreCase);

        private NightTariffWindow(int startMinute, int endMinute, TimeZoneInfo timeZone)
        {
            StartMinute = startMinute;
            EndMinute = endMinute;
            TimeZone = timeZone;
        }

        public int StartMinute { get; }
        public int EndMinute { get; }
        public TimeZoneInfo TimeZone { get; }

        public static bool IsValidMinute(int minute) => minute >= 0 && minute < 24 * 60;

        /// <summary>
        /// Returns null (no night tariff) unless both minutes are valid, differ and the time zone resolves.
        /// An unknown time zone deliberately disables the discount instead of guessing a local clock.
        /// </summary>
        public static NightTariffWindow TryCreate(int? startMinute, int? endMinute, string timeZoneId)
        {
            if (!startMinute.HasValue || !endMinute.HasValue ||
                !IsValidMinute(startMinute.Value) || !IsValidMinute(endMinute.Value) ||
                startMinute.Value == endMinute.Value ||
                !TryResolveTimeZone(timeZoneId, out var timeZone))
            {
                return null;
            }

            return new NightTariffWindow(startMinute.Value, endMinute.Value, timeZone);
        }

        public static NightTariffWindow ForTransaction(Transaction transaction) =>
            transaction == null
                ? null
                : TryCreate(transaction.NightTariffStartMinute, transaction.NightTariffEndMinute, transaction.NightTariffTimeZoneId);

        public static NightTariffWindow ForReservation(ChargePaymentReservation reservation) =>
            reservation?.NightPricePerKwh == null
                ? null
                : TryCreate(reservation.NightTariffStartMinute, reservation.NightTariffEndMinute, reservation.NightTariffTimeZoneId);

        public bool IsNight(DateTime utc)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(AsUtc(utc), TimeZone);
            var minute = local.Hour * 60 + local.Minute;
            return StartMinute > EndMinute
                ? minute >= StartMinute || minute < EndMinute
                : minute >= StartMinute && minute < EndMinute;
        }

        /// <summary>Elapsed time of [fromUtc, toUtc) that falls inside the night window.</summary>
        public TimeSpan NightDuration(DateTime fromUtc, DateTime toUtc)
        {
            fromUtc = AsUtc(fromUtc);
            toUtc = AsUtc(toUtc);
            if (toUtc <= fromUtc)
            {
                return TimeSpan.Zero;
            }

            var total = TimeSpan.Zero;
            var firstDay = TimeZoneInfo.ConvertTimeFromUtc(fromUtc, TimeZone).Date.AddDays(-1);
            var lastDay = TimeZoneInfo.ConvertTimeFromUtc(toUtc, TimeZone).Date;
            for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
            {
                var windowStart = ToUtc(day.AddMinutes(StartMinute));
                var windowEnd = ToUtc((StartMinute > EndMinute ? day.AddDays(1) : day).AddMinutes(EndMinute));
                var overlapStart = windowStart > fromUtc ? windowStart : fromUtc;
                var overlapEnd = windowEnd < toUtc ? windowEnd : toUtc;
                if (overlapEnd > overlapStart)
                {
                    total += overlapEnd - overlapStart;
                }
            }

            return total;
        }

        /// <summary>
        /// Share of an accepted meter increase that is attributed to the night window. Energy between two readings
        /// is assumed to flow evenly over time (an estimate; chargers report about once a minute). A reading pair
        /// with no elapsed time is attributed by its timestamp.
        /// </summary>
        public double NightShare(DateTime fromUtc, DateTime toUtc)
        {
            fromUtc = AsUtc(fromUtc);
            toUtc = AsUtc(toUtc);
            if (toUtc <= fromUtc)
            {
                return IsNight(toUtc) ? 1d : 0d;
            }

            var share = NightDuration(fromUtc, toUtc).TotalSeconds / (toUtc - fromUtc).TotalSeconds;
            return Math.Clamp(share, 0d, 1d);
        }

        public static string FormatMinute(int minute) => $"{minute / 60:00}:{minute % 60:00}";

        private DateTime ToUtc(DateTime local)
        {
            local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            // A boundary inside a spring-forward gap starts at the first valid local minute after it.
            var guard = 0;
            while (TimeZone.IsInvalidTime(local) && guard++ < 24 * 60)
            {
                local = local.AddMinutes(1);
            }

            // For an ambiguous fall-back time ConvertTimeToUtc uses standard time (the later occurrence).
            return TimeZoneInfo.ConvertTimeToUtc(local, TimeZone);
        }

        private static DateTime AsUtc(DateTime value) =>
            value.Kind switch
            {
                DateTimeKind.Utc => value,
                DateTimeKind.Local => value.ToUniversalTime(),
                _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
            };

        private static bool TryResolveTimeZone(string timeZoneId, out TimeZoneInfo timeZone)
        {
            var id = string.IsNullOrWhiteSpace(timeZoneId) ? DefaultTimeZoneId : timeZoneId.Trim();
            if (TimeZones.TryGetValue(id, out timeZone))
            {
                return true;
            }

            foreach (var candidate in new[] { id, WindowsOrIanaAlias(id) })
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                try
                {
                    timeZone = TimeZoneInfo.FindSystemTimeZoneById(candidate);
                    TimeZones[id] = timeZone;
                    return true;
                }
                catch (TimeZoneNotFoundException)
                {
                }
                catch (InvalidTimeZoneException)
                {
                }
            }

            timeZone = null;
            return false;
        }

        private static string WindowsOrIanaAlias(string id) =>
            string.Equals(id, "Europe/Zagreb", StringComparison.OrdinalIgnoreCase) ? "Central European Standard Time"
            : string.Equals(id, "Central European Standard Time", StringComparison.OrdinalIgnoreCase) ? "Europe/Zagreb"
            : null;
    }
}
