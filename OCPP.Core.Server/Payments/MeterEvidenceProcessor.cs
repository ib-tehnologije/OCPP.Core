using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using OCPP.Core.Database;

namespace OCPP.Core.Server.Payments
{
    public static class MeterEvidenceOutcome
    {
        public const string Accepted = "Accepted";
        public const string Rejected = "Rejected";
        public const string FallbackAccepted = "FallbackAccepted";
        public const string ReviewRequired = "ReviewRequired";
    }

    public static class MeterEvidenceSettlementState
    {
        public const string Accepted = "Accepted";
        public const string FallbackAccepted = "FallbackAccepted";
        public const string ReviewRequired = "ReviewRequired";
    }

    public static class MeterEvidenceReason
    {
        public const string Malformed = "Malformed";
        public const string NonFinite = "NonFinite";
        public const string Negative = "Negative";
        public const string UnsupportedUnit = "UnsupportedUnit";
        public const string TimestampRegression = "TimestampRegression";
        public const string NonMonotonic = "NonMonotonic";
        public const string PhysicallyImpossibleIncrease = "PhysicallyImpossibleIncrease";
        public const string PhysicalCapacityUnavailable = "PhysicalCapacityUnavailable";
        public const string AuthorizationLimitExceeded = "AuthorizationLimitExceeded";
        public const string Accepted = "Accepted";
    }

    public sealed class MeterEvidenceObservation
    {
        public string RawValue { get; set; }
        public string Unit { get; set; }
        public int UnitMultiplier { get; set; }
        public string RawEvidenceValue { get; set; }
        public double? EnergyToleranceKwh { get; set; }
        public DateTime ObservedAtUtc { get; set; }
        public string Protocol { get; set; }
        public string Source { get; set; }
        public bool IsTerminal { get; set; }
        public bool SkipMeterStartSeed { get; set; }
        public string OfferedPowerRawValue { get; set; }
        public string OfferedPowerUnit { get; set; }
        public int OfferedPowerMultiplier { get; set; }
    }

    public sealed class MeterEvidenceResult
    {
        public string Outcome { get; init; }
        public string Reason { get; init; }
        public double? NormalizedMeterKwh { get; init; }
        public double? SettlementMeterKwh { get; init; }
    }

    public static class MeterEvidenceProcessor
    {
        public static MeterEvidenceResult Process(
            OCPPCoreContext dbContext,
            Transaction transaction,
            MeterEvidenceObservation observation)
        {
            if (dbContext == null) throw new ArgumentNullException(nameof(dbContext));
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));
            if (observation == null) throw new ArgumentNullException(nameof(observation));

            var observedAtUtc = NormalizeUtc(observation.ObservedAtUtc);
            if (!observation.SkipMeterStartSeed)
            {
                SeedAcceptedProjection(transaction);
            }

            if (!TryNormalizeEnergy(
                    observation.RawValue,
                    observation.Unit,
                    observation.UnitMultiplier,
                    out var normalizedMeterKwh,
                    out var meterToleranceKwh,
                    out var normalizationReason))
            {
                return RejectOrFallback(dbContext, transaction, observation, observedAtUtc, null, normalizationReason);
            }
            if (observation.EnergyToleranceKwh.HasValue)
            {
                meterToleranceKwh = Math.Max(meterToleranceKwh, Math.Max(0, observation.EnergyToleranceKwh.Value));
            }

            var hasCurrentPower = TryNormalizePower(
                observation.OfferedPowerRawValue,
                observation.OfferedPowerUnit,
                observation.OfferedPowerMultiplier,
                out var currentPowerKw,
                out var currentPowerToleranceKw);

            if (transaction.AcceptedMeterAtUtc.HasValue && observedAtUtc < NormalizeUtc(transaction.AcceptedMeterAtUtc.Value))
            {
                return RejectOrFallback(dbContext, transaction, observation, observedAtUtc, normalizedMeterKwh, MeterEvidenceReason.TimestampRegression);
            }

            if (transaction.AcceptedMeterKwh.HasValue)
            {
                var monotonicTolerance = Math.Max(
                    Math.Max(0, transaction.AcceptedMeterToleranceKwh.GetValueOrDefault()),
                    Math.Max(0, meterToleranceKwh));
                var increaseKwh = normalizedMeterKwh - transaction.AcceptedMeterKwh.Value;
                if (increaseKwh < -monotonicTolerance)
                {
                    return RejectOrFallback(dbContext, transaction, observation, observedAtUtc, normalizedMeterKwh, MeterEvidenceReason.NonMonotonic);
                }

                if (transaction.TrustedMaximumPowerKw.HasValue &&
                    transaction.AcceptedMeterAtUtc.HasValue)
                {
                    var elapsedHours = Math.Max(0, (observedAtUtc - NormalizeUtc(transaction.AcceptedMeterAtUtc.Value)).TotalHours);
                    var powerToleranceKwh = Math.Max(0, transaction.TrustedMaximumPowerToleranceKw.GetValueOrDefault()) * elapsedHours;
                    var historicalMaximumIncreaseKwh = transaction.TrustedMaximumPowerKw.Value * elapsedHours +
                                             powerToleranceKwh +
                                             monotonicTolerance;
                    var maximumIncreaseKwh = historicalMaximumIncreaseKwh;
                    if (hasCurrentPower && currentPowerKw > transaction.TrustedMaximumPowerKw.Value)
                    {
                        maximumIncreaseKwh = currentPowerKw * elapsedHours +
                                             currentPowerToleranceKw * elapsedHours +
                                             monotonicTolerance;
                    }
                    if (increaseKwh > maximumIncreaseKwh)
                    {
                        return RejectOrFallback(dbContext, transaction, observation, observedAtUtc, normalizedMeterKwh, MeterEvidenceReason.PhysicallyImpossibleIncrease);
                    }
                    if (increaseKwh > historicalMaximumIncreaseKwh)
                    {
                        return RequireReview(
                            dbContext,
                            transaction,
                            observation,
                            observedAtUtc,
                            normalizedMeterKwh,
                            MeterEvidenceReason.PhysicalCapacityUnavailable);
                    }
                }
            }

            var hadTrustedMaximumPower = transaction.TrustedMaximumPowerKw.HasValue;

            var deliveredKwh = normalizedMeterKwh - transaction.MeterStart;
            if (observation.IsTerminal &&
                transaction.MaxEnergyKwh > 0 &&
                deliveredKwh > transaction.MaxEnergyKwh + meterToleranceKwh &&
                !hadTrustedMaximumPower)
            {
                return RequireReview(
                    dbContext,
                    transaction,
                    observation,
                    observedAtUtc,
                    normalizedMeterKwh,
                    MeterEvidenceReason.PhysicalCapacityUnavailable);
            }

            transaction.AcceptedMeterKwh = normalizedMeterKwh;
            transaction.AcceptedMeterAtUtc = observedAtUtc;
            transaction.AcceptedMeterToleranceKwh = meterToleranceKwh;
            UpdateTrustedPower(transaction, observation, observedAtUtc);

            if (observation.IsTerminal &&
                transaction.MaxEnergyKwh > 0 &&
                deliveredKwh > transaction.MaxEnergyKwh + meterToleranceKwh)
            {
                const string reason = MeterEvidenceReason.AuthorizationLimitExceeded;
                transaction.MeterEvidenceState = MeterEvidenceSettlementState.ReviewRequired;
                transaction.MeterEvidenceReason = reason;
                PersistAnomaly(dbContext, transaction, observation, observedAtUtc, normalizedMeterKwh, MeterEvidenceOutcome.ReviewRequired, reason);
                dbContext.SaveChanges();
                return new MeterEvidenceResult
                {
                    Outcome = MeterEvidenceOutcome.ReviewRequired,
                    Reason = reason,
                    NormalizedMeterKwh = normalizedMeterKwh
                };
            }

            transaction.MeterEvidenceState = MeterEvidenceSettlementState.Accepted;
            transaction.MeterEvidenceReason = MeterEvidenceReason.Accepted;
            if (observation.IsTerminal)
            {
                transaction.MeterStop = normalizedMeterKwh;
            }
            dbContext.SaveChanges();
            return new MeterEvidenceResult
            {
                Outcome = MeterEvidenceOutcome.Accepted,
                Reason = MeterEvidenceReason.Accepted,
                NormalizedMeterKwh = normalizedMeterKwh,
                SettlementMeterKwh = observation.IsTerminal ? normalizedMeterKwh : null
            };
        }

        private static MeterEvidenceResult RejectOrFallback(
            OCPPCoreContext dbContext,
            Transaction transaction,
            MeterEvidenceObservation observation,
            DateTime observedAtUtc,
            double? normalizedMeterKwh,
            string reason)
        {
            string outcome;
            double? settlementMeterKwh = null;
            if (observation.IsTerminal)
            {
                if (transaction.AcceptedMeterKwh.HasValue)
                {
                    outcome = MeterEvidenceOutcome.FallbackAccepted;
                    settlementMeterKwh = transaction.AcceptedMeterKwh.Value;
                    transaction.MeterStop = settlementMeterKwh;
                    transaction.MeterEvidenceState = MeterEvidenceSettlementState.FallbackAccepted;
                    transaction.MeterEvidenceReason = reason;
                }
                else
                {
                    outcome = MeterEvidenceOutcome.ReviewRequired;
                    transaction.MeterStop = null;
                    transaction.MeterEvidenceState = MeterEvidenceSettlementState.ReviewRequired;
                    transaction.MeterEvidenceReason = reason;
                }
            }
            else
            {
                outcome = MeterEvidenceOutcome.Rejected;
            }

            PersistAnomaly(dbContext, transaction, observation, observedAtUtc, normalizedMeterKwh, outcome, reason);
            dbContext.SaveChanges();
            return new MeterEvidenceResult
            {
                Outcome = outcome,
                Reason = reason,
                NormalizedMeterKwh = normalizedMeterKwh,
                SettlementMeterKwh = settlementMeterKwh
            };
        }

        private static MeterEvidenceResult RequireReview(
            OCPPCoreContext dbContext,
            Transaction transaction,
            MeterEvidenceObservation observation,
            DateTime observedAtUtc,
            double normalizedMeterKwh,
            string reason)
        {
            transaction.MeterStop = null;
            transaction.MeterEvidenceState = MeterEvidenceSettlementState.ReviewRequired;
            transaction.MeterEvidenceReason = reason;
            PersistAnomaly(
                dbContext,
                transaction,
                observation,
                observedAtUtc,
                normalizedMeterKwh,
                MeterEvidenceOutcome.ReviewRequired,
                reason);
            dbContext.SaveChanges();
            return new MeterEvidenceResult
            {
                Outcome = MeterEvidenceOutcome.ReviewRequired,
                Reason = reason,
                NormalizedMeterKwh = normalizedMeterKwh
            };
        }

        private static void SeedAcceptedProjection(Transaction transaction)
        {
            if (!transaction.AcceptedMeterKwh.HasValue &&
                double.IsFinite(transaction.MeterStart) &&
                transaction.MeterStart >= 0 &&
                transaction.StartTime != default)
            {
                transaction.AcceptedMeterKwh = transaction.MeterStart;
                transaction.AcceptedMeterAtUtc = NormalizeUtc(transaction.StartTime);
                transaction.AcceptedMeterToleranceKwh = 0;
            }
        }

        private static void UpdateTrustedPower(Transaction transaction, MeterEvidenceObservation observation, DateTime observedAtUtc)
        {
            if (!TryNormalizePower(
                    observation.OfferedPowerRawValue,
                    observation.OfferedPowerUnit,
                    observation.OfferedPowerMultiplier,
                    out var powerKw,
                    out var toleranceKw))
            {
                return;
            }

            if (transaction.TrustedMaximumPowerKw.HasValue &&
                powerKw <= transaction.TrustedMaximumPowerKw.Value)
            {
                return;
            }

            transaction.TrustedMaximumPowerKw = powerKw;
            transaction.TrustedMaximumPowerToleranceKw = toleranceKw;
            transaction.TrustedMaximumPowerAtUtc = observedAtUtc;
            transaction.TrustedMaximumPowerSource = $"{observation.Protocol}:{observation.Source}:Power.Offered";
        }

        internal static bool TryNormalizeEnergy(
            string raw,
            string unit,
            int multiplier,
            out double normalizedKwh,
            out double toleranceKwh,
            out string reason)
        {
            normalizedKwh = 0;
            toleranceKwh = 0;
            reason = null;
            if (string.IsNullOrWhiteSpace(raw) ||
                !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                reason = MeterEvidenceReason.Malformed;
                return false;
            }
            if (!double.IsFinite(parsed))
            {
                reason = MeterEvidenceReason.NonFinite;
                return false;
            }
            if (parsed < 0)
            {
                reason = MeterEvidenceReason.Negative;
                return false;
            }

            var normalizedUnit = (unit ?? string.Empty).Trim();
            double scale;
            if (string.IsNullOrEmpty(normalizedUnit) ||
                string.Equals(normalizedUnit, "Wh", StringComparison.OrdinalIgnoreCase))
            {
                scale = 0.001d;
            }
            else if (string.Equals(normalizedUnit, "kWh", StringComparison.OrdinalIgnoreCase))
            {
                scale = 1d;
            }
            else
            {
                reason = MeterEvidenceReason.UnsupportedUnit;
                return false;
            }

            scale *= Math.Pow(10d, multiplier);
            normalizedKwh = parsed * scale;
            toleranceKwh = NumericTolerance(raw) * scale;
            if (!double.IsFinite(normalizedKwh))
            {
                reason = MeterEvidenceReason.NonFinite;
                return false;
            }
            return true;
        }

        internal static bool TryNormalizePower(string raw, string unit, int multiplier, out double powerKw, out double toleranceKw)
        {
            powerKw = 0;
            toleranceKw = 0;
            if (string.IsNullOrWhiteSpace(raw) ||
                !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ||
                !double.IsFinite(parsed) ||
                parsed <= 0)
            {
                return false;
            }

            var normalizedUnit = (unit ?? string.Empty).Trim();
            double scale;
            if (string.Equals(normalizedUnit, "W", StringComparison.OrdinalIgnoreCase))
            {
                scale = 0.001d;
            }
            else if (string.Equals(normalizedUnit, "kW", StringComparison.OrdinalIgnoreCase))
            {
                scale = 1d;
            }
            else
            {
                return false;
            }

            scale *= Math.Pow(10d, multiplier);
            powerKw = parsed * scale;
            toleranceKw = NumericTolerance(raw) * scale;
            return double.IsFinite(powerKw) && powerKw > 0;
        }

        private static double NumericTolerance(string raw)
        {
            var value = (raw ?? string.Empty).Trim();
            var exponentIndex = value.IndexOfAny(new[] { 'e', 'E' });
            var exponent = 0;
            if (exponentIndex >= 0)
            {
                int.TryParse(value[(exponentIndex + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out exponent);
                value = value[..exponentIndex];
            }
            var decimalIndex = value.IndexOf('.');
            var decimalPlaces = decimalIndex < 0 ? 0 : value.Length - decimalIndex - 1;
            return 0.5d * Math.Pow(10d, exponent - decimalPlaces);
        }

        private static void PersistAnomaly(
            OCPPCoreContext dbContext,
            Transaction transaction,
            MeterEvidenceObservation observation,
            DateTime observedAtUtc,
            double? normalizedMeterKwh,
            string outcome,
            string reason)
        {
            var rawValue = Truncate(observation.RawEvidenceValue ?? observation.RawValue ?? string.Empty, 500);
            var protocol = Truncate(observation.Protocol ?? "Unknown", 20);
            var source = Truncate(observation.Source ?? "Unknown", 50);
            var rawUnit = Truncate(observation.Unit, 50);
            var evidenceKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\u001f",
                transaction.TransactionId.ToString(CultureInfo.InvariantCulture),
                observedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                protocol,
                source,
                rawValue,
                rawUnit ?? string.Empty,
                observation.UnitMultiplier.ToString(CultureInfo.InvariantCulture),
                reason ?? string.Empty)))).ToLowerInvariant();
            var exists = dbContext.MeterEvidenceAnomalies.Local.Any(item =>
                             item.TransactionId == transaction.TransactionId && item.EvidenceKey == evidenceKey) ||
                         dbContext.MeterEvidenceAnomalies.AsNoTracking().Any(item =>
                             item.TransactionId == transaction.TransactionId && item.EvidenceKey == evidenceKey);
            if (exists)
            {
                return;
            }

            dbContext.MeterEvidenceAnomalies.Add(new MeterEvidenceAnomaly
            {
                TransactionId = transaction.TransactionId,
                ChargePointId = transaction.ChargePointId,
                ConnectorId = transaction.ConnectorId,
                ObservedAtUtc = observedAtUtc,
                Protocol = protocol,
                Source = source,
                RawValue = rawValue,
                RawUnit = rawUnit,
                RawUnitMultiplier = observation.UnitMultiplier,
                EvidenceKey = evidenceKey,
                NormalizedMeterKwh = normalizedMeterKwh,
                Outcome = outcome,
                Reason = reason,
                AcceptedMeterKwh = transaction.AcceptedMeterKwh,
                AcceptedMeterAtUtc = transaction.AcceptedMeterAtUtc,
                TrustedMaximumPowerKw = transaction.TrustedMaximumPowerKw,
                CreatedAtUtc = DateTime.UtcNow
            });
        }

        private static DateTime NormalizeUtc(DateTime value)
        {
            if (value.Kind == DateTimeKind.Utc) return value;
            if (value.Kind == DateTimeKind.Local) return value.ToUniversalTime();
            return DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength) return value;
            return value[..maxLength];
        }
    }
}
