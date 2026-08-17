using System;
using Microsoft.Extensions.Logging;
using OCPP.Core.Database;

namespace OCPP.Core.Server.Payments.Recovery
{
    public static class FinancialRecoverySettlementAssessor
    {
        public static FinancialRecoverySettlementDecision Assess(
            ChargePaymentReservation reservation,
            Transaction transaction) =>
            Assess(reservation, transaction, new PaymentFlowOptions());

        public static FinancialRecoverySettlementDecision Assess(
            ChargePaymentReservation reservation,
            Transaction transaction,
            PaymentFlowOptions flowOptions,
            ILogger logger = null)
        {
            if (reservation == null)
            {
                return Blocked("Reservation evidence is missing.");
            }

            if (transaction == null)
            {
                return Blocked("Transaction evidence is missing.");
            }

            if (!reservation.TransactionId.HasValue || reservation.TransactionId.Value != transaction.TransactionId)
            {
                return Blocked("Reservation transaction link does not match the supplied transaction.");
            }

            if (!string.Equals(reservation.Status, PaymentReservationStatus.Failed, StringComparison.OrdinalIgnoreCase))
            {
                return Blocked("Recovery settlement requires a failed terminal reservation.");
            }

            if (!transaction.StopTime.HasValue || transaction.StopTime.Value < transaction.StartTime)
            {
                return Blocked("Transaction stop time is missing or precedes its start time.");
            }

            if (!reservation.StopTransactionAtUtc.HasValue ||
                reservation.StopTransactionAtUtc.Value != transaction.StopTime.Value)
            {
                return Blocked("Reservation stop evidence is missing or contradicts the linked transaction.");
            }

            if (!reservation.DisconnectedAtUtc.HasValue ||
                reservation.DisconnectedAtUtc.Value < transaction.StopTime.Value)
            {
                return Blocked("Final disconnect evidence is missing or precedes the transaction stop.");
            }

            if (!transaction.MeterStop.HasValue)
            {
                return Blocked("Transaction meter stop is missing.");
            }

            if (transaction.MeterStart < 0 || transaction.MeterStop.Value < 0)
            {
                return Blocked("Transaction meter evidence cannot be negative.");
            }

            if (transaction.MeterStop.Value < transaction.MeterStart)
            {
                return Blocked("Transaction meter stop is below meter start.");
            }

            if (reservation.PricePerKwh < 0 || reservation.UserSessionFee < 0 ||
                reservation.UsageFeePerMinute < 0 || transaction.IdleUsageFeeAmount < 0 ||
                transaction.UsageFeeMinutes < 0)
            {
                return Blocked("Persisted pricing or fee evidence cannot be negative.");
            }

            if (string.IsNullOrWhiteSpace(reservation.Currency))
            {
                return Blocked("Reservation currency is missing.");
            }

            var energyKwh = transaction.MeterStop.Value - transaction.MeterStart;
            if (reservation.ActualEnergyKwh.HasValue &&
                Math.Abs(reservation.ActualEnergyKwh.Value - energyKwh) > 0.000001d)
            {
                return Blocked("Persisted reservation energy contradicts transaction meter evidence.");
            }

            if (reservation.CapturedAtUtc.HasValue || reservation.CapturedAmountCents.GetValueOrDefault() > 0)
            {
                return Blocked("Recovery settlement refuses a reservation with captured funds.");
            }

            var calculation = FinancialSettlementCalculator.Calculate(
                reservation,
                transaction,
                flowOptions,
                reservation.DisconnectedAtUtc.Value,
                logger);
            if (!calculation.HasValidDeliveredEnergy || calculation.ShouldNoChargeForDeliveredEnergy)
            {
                return Blocked(calculation.DeliveredEnergyNoChargeReason ?? "Delivered energy is not billable.");
            }

            if (calculation.AmountToCaptureCents <= 0)
            {
                return Blocked("Derived billable amount is not positive.");
            }

            if (calculation.MinimumChargeAmountCents > 0 &&
                calculation.AmountToCaptureCents < calculation.MinimumChargeAmountCents)
            {
                return Blocked("Derived billable amount is below the configured minimum capture amount.");
            }

            return new FinancialRecoverySettlementDecision
            {
                Eligible = true,
                EnergyKwh = calculation.ActualEnergyKwh,
                EnergyCostCents = calculation.EnergyCostCents,
                SessionFeeCents = calculation.SessionFeeCents,
                UsageFeeMinutes = calculation.UsageFeeMinutes,
                UsageFeeCents = calculation.UsageFeeCents,
                IdleFeeCents = reservation.UsageFeeAnchorMinutes == 1 ? calculation.UsageFeeCents : 0,
                TotalCents = calculation.AmountToCaptureCents,
                Calculation = calculation
            };
        }

        private static FinancialRecoverySettlementDecision Blocked(string reason) => new()
        {
            Eligible = false,
            Reason = reason
        };
    }

    public sealed class FinancialRecoverySettlementDecision
    {
        public bool Eligible { get; set; }
        public string Reason { get; set; }
        public double EnergyKwh { get; set; }
        public long EnergyCostCents { get; set; }
        public long SessionFeeCents { get; set; }
        public int UsageFeeMinutes { get; set; }
        public long UsageFeeCents { get; set; }
        public long IdleFeeCents { get; set; }
        public long TotalCents { get; set; }
        internal FinancialSettlementCalculation Calculation { get; set; }
    }
}
