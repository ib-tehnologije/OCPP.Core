using System;
using OCPP.Core.Database;

namespace OCPP.Core.Server.Payments.Recovery
{
    public static class FinancialRecoverySettlementAssessor
    {
        public static FinancialRecoverySettlementDecision Assess(
            ChargePaymentReservation reservation,
            Transaction transaction)
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

            if (!transaction.StopTime.HasValue || transaction.StopTime.Value < transaction.StartTime)
            {
                return Blocked("Transaction stop time is missing or precedes its start time.");
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

            var energyCostCents = ToCents(reservation.PricePerKwh * Convert.ToDecimal(energyKwh));
            var sessionFeeCents = energyKwh > 0 ? ToCents(reservation.UserSessionFee) : 0;
            var usageFeeCents = ToCents(reservation.UsageFeePerMinute * transaction.UsageFeeMinutes);
            var idleFeeCents = ToCents(transaction.IdleUsageFeeAmount);
            var totalCents = checked(energyCostCents + sessionFeeCents + usageFeeCents + idleFeeCents);

            if (reservation.CapturedAmountCents.HasValue && reservation.CapturedAmountCents.Value > 0 &&
                reservation.CapturedAmountCents.Value != totalCents)
            {
                return Blocked("Persisted captured amount contradicts the derived billable total.");
            }

            return new FinancialRecoverySettlementDecision
            {
                Eligible = true,
                EnergyKwh = energyKwh,
                EnergyCostCents = energyCostCents,
                SessionFeeCents = sessionFeeCents,
                UsageFeeCents = usageFeeCents,
                IdleFeeCents = idleFeeCents,
                TotalCents = totalCents
            };
        }

        private static long ToCents(decimal amount)
        {
            var rounded = Math.Round(amount, 2, MidpointRounding.AwayFromZero);
            return checked((long)Math.Round(rounded * 100m, 0, MidpointRounding.AwayFromZero));
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
        public long UsageFeeCents { get; set; }
        public long IdleFeeCents { get; set; }
        public long TotalCents { get; set; }
    }
}
