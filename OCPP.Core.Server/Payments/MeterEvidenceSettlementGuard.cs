using System;
using System.Globalization;
using OCPP.Core.Database;

namespace OCPP.Core.Server.Payments
{
    internal sealed class MeterEvidenceSettlementDecision
    {
        public bool Ready { get; init; }
        public string Reason { get; init; }
    }

    internal static class MeterEvidenceSettlementGuard
    {
        public static MeterEvidenceSettlementDecision EnsureReady(
            OCPPCoreContext dbContext,
            ChargePaymentReservation reservation,
            Transaction transaction,
            string source)
        {
            if (transaction == null || !transaction.MeterStop.HasValue)
            {
                return Assess(reservation, transaction);
            }

            if (string.Equals(transaction.MeterEvidenceState, MeterEvidenceSettlementState.FallbackAccepted, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(transaction.MeterEvidenceState, MeterEvidenceSettlementState.ReviewRequired, StringComparison.OrdinalIgnoreCase))
            {
                return Assess(reservation, transaction);
            }

            if (string.Equals(transaction.MeterEvidenceState, MeterEvidenceSettlementState.Accepted, StringComparison.OrdinalIgnoreCase) &&
                transaction.AcceptedMeterKwh.HasValue &&
                Math.Abs(transaction.MeterStop.Value - transaction.AcceptedMeterKwh.Value) <=
                Math.Max(0, transaction.AcceptedMeterToleranceKwh.GetValueOrDefault()))
            {
                return Assess(reservation, transaction);
            }

            if (string.IsNullOrWhiteSpace(transaction.MeterEvidenceState) &&
                !transaction.AcceptedMeterKwh.HasValue &&
                !transaction.TrustedMaximumPowerKw.HasValue &&
                transaction.StopTime.HasValue)
            {
                // Compatibility for transactions closed before this safeguard existed.
                // New stop/recovery paths always create meter-evidence state before
                // settlement, while immutable historical rows retain structural checks.
                return Assess(reservation, transaction);
            }

            var observation = new MeterEvidenceObservation
            {
                RawValue = transaction.MeterStop.Value.ToString("R", CultureInfo.InvariantCulture),
                Unit = "kWh",
                ObservedAtUtc = transaction.StopTime ?? reservation?.StopTransactionAtUtc ?? DateTime.UtcNow,
                Protocol = "Settlement",
                Source = source ?? "Settlement",
                IsTerminal = true
            };
            MeterEvidenceProcessor.Process(dbContext, transaction, observation);
            return Assess(reservation, transaction);
        }

        public static MeterEvidenceSettlementDecision AssessInvoice(
            ChargePaymentReservation reservation,
            Transaction transaction)
        {
            if (transaction == null)
            {
                return Blocked("Transaction meter evidence is missing.");
            }

            if (!string.IsNullOrWhiteSpace(transaction.MeterEvidenceState) ||
                transaction.MeterStop.HasValue)
            {
                return Assess(reservation, transaction);
            }

            if (string.Equals(reservation?.Status, PaymentReservationStatus.Completed, StringComparison.OrdinalIgnoreCase) &&
                reservation.CapturedAtUtc.HasValue &&
                reservation.CapturedAmountCents.GetValueOrDefault() > 0 &&
                double.IsFinite(transaction.EnergyKwh) &&
                transaction.EnergyKwh >= 0 &&
                (!reservation.ActualEnergyKwh.HasValue ||
                 Math.Abs(reservation.ActualEnergyKwh.Value - transaction.EnergyKwh) <= 0.000001d))
            {
                // Rows completed before accepted-meter projections existed retain their
                // already-captured immutable billing evidence. New capture paths always
                // pass EnsureReady before they can reach this invoice boundary.
                return new MeterEvidenceSettlementDecision { Ready = true };
            }

            return Blocked("Transaction meter evidence is missing.");
        }

        public static MeterEvidenceSettlementDecision Assess(
            ChargePaymentReservation reservation,
            Transaction transaction)
        {
            if (transaction == null)
            {
                return Blocked("Transaction meter evidence is missing.");
            }
            if (string.Equals(transaction.MeterEvidenceState, MeterEvidenceSettlementState.ReviewRequired, StringComparison.OrdinalIgnoreCase))
            {
                return Blocked($"Transaction meter evidence requires review ({transaction.MeterEvidenceReason ?? "unspecified"}).");
            }
            if (!transaction.MeterStop.HasValue ||
                !double.IsFinite(transaction.MeterStart) ||
                !double.IsFinite(transaction.MeterStop.Value) ||
                transaction.MeterStart < 0 ||
                transaction.MeterStop.Value < transaction.MeterStart)
            {
                return Blocked("Transaction meter evidence is missing, non-finite, negative, or non-monotonic.");
            }

            if (!string.IsNullOrWhiteSpace(transaction.MeterEvidenceState))
            {
                if (!transaction.AcceptedMeterKwh.HasValue ||
                    Math.Abs(transaction.MeterStop.Value - transaction.AcceptedMeterKwh.Value) >
                    Math.Max(0, transaction.AcceptedMeterToleranceKwh.GetValueOrDefault()))
                {
                    return Blocked("Settlement meter does not match the accepted meter projection.");
                }
            }

            var deliveredKwh = transaction.MeterStop.Value - transaction.MeterStart;
            var maxEnergyKwh = transaction.MaxEnergyKwh > 0
                ? transaction.MaxEnergyKwh
                : reservation?.MaxEnergyKwh ?? 0;
            if (maxEnergyKwh > 0 && deliveredKwh > maxEnergyKwh)
            {
                return Blocked("Delivered energy exceeds the authorization boundary and requires review.");
            }

            return new MeterEvidenceSettlementDecision { Ready = true };
        }

        private static MeterEvidenceSettlementDecision Blocked(string reason) => new()
        {
            Ready = false,
            Reason = reason
        };
    }
}
