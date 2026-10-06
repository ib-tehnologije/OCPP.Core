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
        public string CandidateOfferedPowerRawValue { get; set; }
        public string CandidateOfferedPowerUnit { get; set; }
        public string CandidateOfferedPowerMultiplier { get; set; }

        /// <summary>
        /// Overrides <see cref="MeterEvidenceProcessor.FallbackMaximumPowerKw"/> for this observation.
        /// A value of zero or less disables the fallback capacity ceiling.
        /// </summary>
        public double? FallbackMaximumPowerKw { get; set; }
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
        /// <summary>
        /// Default physical ceiling used when a charger reports no accepted offered power.
        /// It is deliberately above any real charging power, so it only rejects readings
        /// that no charger could have delivered in the elapsed time.
        /// </summary>
        public const double DefaultFallbackMaximumPowerKw = 400d;

        /// <summary>
        /// Floating-point allowance for the authorization boundary. Reading precision is
        /// deliberately not used here, because it is re-derived from formatted kWh strings on
        /// some paths and could otherwise admit a real overshoot.
        /// </summary>
        internal const double AuthorizationLimitEpsilonKwh = 0.000001d;

        /// <summary>
        /// Smallest elapsed time used for the capacity bound. Charger timestamps have whole-second
        /// resolution and can be corrected by small amounts, so two samples in the same second or a
        /// short clock step must not make an ordinary increase look impossible.
        /// </summary>
        internal static readonly TimeSpan MinimumCapacityWindow = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Configured fallback ceiling (<c>MeterEvidence:FallbackMaximumPowerKw</c>).
        /// Zero or less disables the fallback, so increases without offered-power
        /// evidence are rejected or require review.
        /// </summary>
        public static double FallbackMaximumPowerKw { get; private set; } = DefaultFallbackMaximumPowerKw;

        public static void ConfigureFallbackMaximumPowerKw(double? fallbackMaximumPowerKw)
        {
            FallbackMaximumPowerKw = fallbackMaximumPowerKw.HasValue && double.IsFinite(fallbackMaximumPowerKw.Value)
                ? fallbackMaximumPowerKw.Value
                : DefaultFallbackMaximumPowerKw;
        }

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

            var fallbackPowerKw = observation.FallbackMaximumPowerKw is double configuredFallbackKw && double.IsFinite(configuredFallbackKw)
                ? configuredFallbackKw
                : FallbackMaximumPowerKw;

            var hasCurrentPower = TryNormalizePower(
                observation.OfferedPowerRawValue,
                observation.OfferedPowerUnit,
                observation.OfferedPowerMultiplier,
                out var currentPowerKw,
                out var currentPowerToleranceKw);
            if (hasCurrentPower && fallbackPowerKw > 0 && currentPowerKw > fallbackPowerKw)
            {
                // Offered power is charger-reported like the energy register; it cannot raise
                // the physical bound above the ceiling (for example a W value labelled kW).
                currentPowerKw = fallbackPowerKw;
                currentPowerToleranceKw = 0;
            }

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
                if (increaseKwh < 0)
                {
                    // A decrease within reading precision is not a real decrease: keep the
                    // accepted projection so settlement never sees MeterStop below it.
                    normalizedMeterKwh = transaction.AcceptedMeterKwh.Value;
                    increaseKwh = 0;
                }

                // Once a jump was rejected as impossible, readings that continue from the jumped
                // register level stay rejected until the register returns to the accepted
                // trajectory. Otherwise the capacity bound, which grows with the time since the
                // last accepted reading, would eventually admit a persistent offset.
                // A terminal reading in that state requires review rather than settling silently
                // from a projection that may be stale for reasons such as a charger clock step.
                if (increaseKwh > 0 &&
                    ContinuesRejectedImpossibleJump(dbContext, transaction, normalizedMeterKwh, monotonicTolerance))
                {
                    return observation.IsTerminal
                        ? RequireReview(dbContext, transaction, observation, observedAtUtc, normalizedMeterKwh, MeterEvidenceReason.PhysicallyImpossibleIncrease)
                        : RejectOrFallback(dbContext, transaction, observation, observedAtUtc, normalizedMeterKwh, MeterEvidenceReason.PhysicallyImpossibleIncrease);
                }

                var capacityKw = transaction.TrustedMaximumPowerKw ?? (fallbackPowerKw > 0 ? fallbackPowerKw : (double?)null);
                var capacityToleranceKw = transaction.TrustedMaximumPowerKw.HasValue
                    ? Math.Max(0, transaction.TrustedMaximumPowerToleranceKw.GetValueOrDefault())
                    : 0;

                if (increaseKwh > 0 &&
                    !capacityKw.HasValue &&
                    (observation.IsTerminal || !hasCurrentPower))
                {
                    return observation.IsTerminal
                        ? RequireReview(
                            dbContext,
                            transaction,
                            observation,
                            observedAtUtc,
                            normalizedMeterKwh,
                            MeterEvidenceReason.PhysicalCapacityUnavailable)
                        : RejectOrFallback(
                            dbContext,
                            transaction,
                            observation,
                            observedAtUtc,
                            normalizedMeterKwh,
                            MeterEvidenceReason.PhysicalCapacityUnavailable);
                }

                if (capacityKw.HasValue &&
                    transaction.AcceptedMeterAtUtc.HasValue)
                {
                    var elapsedHours = Math.Max(
                        MinimumCapacityWindow.TotalHours,
                        (observedAtUtc - NormalizeUtc(transaction.AcceptedMeterAtUtc.Value)).TotalHours);
                    var historicalMaximumIncreaseKwh = capacityKw.Value * elapsedHours +
                                             capacityToleranceKw * elapsedHours +
                                             monotonicTolerance;
                    var maximumIncreaseKwh = historicalMaximumIncreaseKwh;
                    if (hasCurrentPower && currentPowerKw > capacityKw.Value)
                    {
                        maximumIncreaseKwh = currentPowerKw * elapsedHours +
                                             currentPowerToleranceKw * elapsedHours +
                                             monotonicTolerance;
                    }
                    if (increaseKwh > maximumIncreaseKwh)
                    {
                        return RejectOrFallback(dbContext, transaction, observation, observedAtUtc, normalizedMeterKwh, MeterEvidenceReason.PhysicallyImpossibleIncrease);
                    }
                    // With a fallback ceiling, a non-terminal increase explained only by newly
                    // higher (ceiling-bounded) offered power is accepted and raises the trusted
                    // capacity; otherwise every later reading would be judged against the stale
                    // lower capacity. A terminal reading has no later evidence, and strict mode
                    // keeps the original behaviour, so both still require review.
                    if (increaseKwh > historicalMaximumIncreaseKwh &&
                        (observation.IsTerminal || fallbackPowerKw <= 0))
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

            var hadCapacityBasis = transaction.TrustedMaximumPowerKw.HasValue || fallbackPowerKw > 0;

            var deliveredKwh = normalizedMeterKwh - transaction.MeterStart;
            if (observation.IsTerminal &&
                transaction.MaxEnergyKwh > 0 &&
                deliveredKwh > transaction.MaxEnergyKwh + AuthorizationLimitEpsilonKwh &&
                !hadCapacityBasis)
            {
                return RequireReview(
                    dbContext,
                    transaction,
                    observation,
                    observedAtUtc,
                    normalizedMeterKwh,
                    MeterEvidenceReason.PhysicalCapacityUnavailable);
            }

            AccumulateNightEnergy(transaction, normalizedMeterKwh, observedAtUtc);
            transaction.AcceptedMeterKwh = normalizedMeterKwh;
            transaction.AcceptedMeterAtUtc = observedAtUtc;
            transaction.AcceptedMeterToleranceKwh = meterToleranceKwh;
            UpdateTrustedPower(transaction, observation, observedAtUtc, fallbackPowerKw);

            if (observation.IsTerminal &&
                transaction.MaxEnergyKwh > 0 &&
                deliveredKwh > transaction.MaxEnergyKwh + AuthorizationLimitEpsilonKwh)
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

        /// <summary>
        /// Adds the night-window share of an accepted meter increase to the transaction's night energy.
        /// Only accepted evidence moves this total, so rejected or review-required readings never affect pricing.
        /// </summary>
        private static void AccumulateNightEnergy(Transaction transaction, double acceptedMeterKwh, DateTime observedAtUtc)
        {
            var window = NightTariffWindow.ForTransaction(transaction);
            if (window == null ||
                !transaction.AcceptedMeterKwh.HasValue ||
                !transaction.AcceptedMeterAtUtc.HasValue)
            {
                return;
            }

            var increaseKwh = acceptedMeterKwh - transaction.AcceptedMeterKwh.Value;
            if (!double.IsFinite(increaseKwh) || increaseKwh <= 0)
            {
                return;
            }

            transaction.NightEnergyKwh += increaseKwh * window.NightShare(
                NormalizeUtc(transaction.AcceptedMeterAtUtc.Value),
                observedAtUtc);
        }

        private static bool ContinuesRejectedImpossibleJump(
            OCPPCoreContext dbContext,
            Transaction transaction,
            double normalizedMeterKwh,
            double toleranceKwh)
        {
            if (!transaction.AcceptedMeterAtUtc.HasValue)
            {
                return false;
            }

            var acceptedAtUtc = NormalizeUtc(transaction.AcceptedMeterAtUtc.Value);
            var transactionId = transaction.TransactionId;

            // A charger clock that stepped back since the last accepted reading (for example a
            // daylight-saving change on a charger that labels local time as UTC) makes the first
            // reading after the step look like a jump. Do not lock the session in that case; the
            // growing capacity bound admits the following readings within minutes.
            var regression = MeterEvidenceReason.TimestampRegression;
            var acceptedAtValue = transaction.AcceptedMeterAtUtc.Value;
            var clockSteppedBack =
                dbContext.MeterEvidenceAnomalies.Local.Any(item =>
                    item.TransactionId == transactionId &&
                    item.Reason == regression &&
                    item.AcceptedMeterAtUtc == acceptedAtValue) ||
                dbContext.MeterEvidenceAnomalies.AsNoTracking().Any(item =>
                    item.TransactionId == transactionId &&
                    item.Reason == regression &&
                    item.AcceptedMeterAtUtc == acceptedAtValue);
            if (clockSteppedBack)
            {
                return false;
            }

            var reason = MeterEvidenceReason.PhysicallyImpossibleIncrease;
            // Only non-terminal rejections establish a jumped register level; a replayed terminal
            // reading must reproduce its original settlement.
            var rejected = MeterEvidenceOutcome.Rejected;
            var lowestPendingKwh = dbContext.MeterEvidenceAnomalies.Local
                .Where(item => item.TransactionId == transactionId &&
                               item.Reason == reason &&
                               item.Outcome == rejected &&
                               item.NormalizedMeterKwh.HasValue &&
                               item.ObservedAtUtc >= acceptedAtUtc)
                .Min(item => item.NormalizedMeterKwh);
            var lowestPersistedKwh = dbContext.MeterEvidenceAnomalies
                .AsNoTracking()
                .Where(item => item.TransactionId == transactionId &&
                               item.Reason == reason &&
                               item.Outcome == rejected &&
                               item.NormalizedMeterKwh != null &&
                               item.ObservedAtUtc >= acceptedAtUtc)
                .Min(item => item.NormalizedMeterKwh);
            var lowestRejectedKwh = lowestPendingKwh.HasValue && lowestPersistedKwh.HasValue
                ? Math.Min(lowestPendingKwh.Value, lowestPersistedKwh.Value)
                : lowestPendingKwh ?? lowestPersistedKwh;
            return lowestRejectedKwh.HasValue &&
                   normalizedMeterKwh >= lowestRejectedKwh.Value - Math.Max(0, toleranceKwh);
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

        private static void UpdateTrustedPower(Transaction transaction, MeterEvidenceObservation observation, DateTime observedAtUtc, double fallbackPowerKw)
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
            if (fallbackPowerKw > 0 && powerKw > fallbackPowerKw)
            {
                powerKw = fallbackPowerKw;
                toleranceKw = 0;
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
            double divisor;
            if (string.IsNullOrEmpty(normalizedUnit) ||
                string.Equals(normalizedUnit, "Wh", StringComparison.OrdinalIgnoreCase))
            {
                // Divide (not multiply by 0.001) so a Wh reading normalizes to exactly the
                // same double as the transaction's MeterStart (meterStart / 1000).
                divisor = 1000d;
            }
            else if (string.Equals(normalizedUnit, "kWh", StringComparison.OrdinalIgnoreCase))
            {
                divisor = 1d;
            }
            else
            {
                reason = MeterEvidenceReason.UnsupportedUnit;
                return false;
            }

            var multiplierScale = Math.Pow(10d, multiplier);
            normalizedKwh = multiplier == 0 ? parsed / divisor : parsed * multiplierScale / divisor;
            toleranceKwh = NumericTolerance(raw) * multiplierScale / divisor;
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
            var candidateOfferedPowerRawValue = Truncate(
                observation.CandidateOfferedPowerRawValue ?? observation.OfferedPowerRawValue,
                500);
            var candidateOfferedPowerUnit = Truncate(
                observation.CandidateOfferedPowerUnit ?? observation.OfferedPowerUnit,
                50);
            var candidateOfferedPowerMultiplier = Truncate(observation.CandidateOfferedPowerMultiplier, 500);
            if (string.IsNullOrWhiteSpace(candidateOfferedPowerMultiplier) &&
                (!string.IsNullOrWhiteSpace(observation.OfferedPowerRawValue) ||
                 !string.IsNullOrWhiteSpace(observation.OfferedPowerUnit)))
            {
                candidateOfferedPowerMultiplier = observation.OfferedPowerMultiplier.ToString(CultureInfo.InvariantCulture);
            }
            var legacyEvidence = string.Join("\u001f",
                transaction.TransactionId.ToString(CultureInfo.InvariantCulture),
                observedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                protocol,
                source,
                rawValue,
                rawUnit ?? string.Empty,
                observation.UnitMultiplier.ToString(CultureInfo.InvariantCulture),
                reason ?? string.Empty);
            var hasPowerCandidate = !string.IsNullOrWhiteSpace(candidateOfferedPowerRawValue) ||
                                    !string.IsNullOrWhiteSpace(candidateOfferedPowerUnit) ||
                                    !string.IsNullOrWhiteSpace(candidateOfferedPowerMultiplier);
            var evidence = hasPowerCandidate
                ? string.Join("\u001f",
                    transaction.TransactionId.ToString(CultureInfo.InvariantCulture),
                    observedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                    protocol,
                    source,
                    rawValue,
                    rawUnit ?? string.Empty,
                    observation.UnitMultiplier.ToString(CultureInfo.InvariantCulture),
                    candidateOfferedPowerRawValue ?? string.Empty,
                    candidateOfferedPowerUnit ?? string.Empty,
                    candidateOfferedPowerMultiplier ?? string.Empty,
                    reason ?? string.Empty)
                : legacyEvidence;
            var evidenceKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(evidence))).ToLowerInvariant();
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
                CandidateOfferedPowerRawValue = candidateOfferedPowerRawValue,
                CandidateOfferedPowerUnit = candidateOfferedPowerUnit,
                CandidateOfferedPowerMultiplier = candidateOfferedPowerMultiplier,
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
