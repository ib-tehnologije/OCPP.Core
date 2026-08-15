using System;
using OCPP.Core.Database;

namespace OCPP.Core.Server.Payments.Recovery
{
    public static class FinancialRecoveryInvoiceAssessor
    {
        public static FinancialRecoveryInvoiceDecision Assess(
            ChargePaymentReservation reservation,
            Transaction transaction)
        {
            if (reservation == null)
            {
                return Blocked("Reservation evidence is missing.");
            }

            if (transaction == null)
            {
                return Blocked("Linked transaction was not found.");
            }

            if (!string.Equals(reservation.Status, PaymentReservationStatus.Completed, StringComparison.OrdinalIgnoreCase) ||
                !reservation.CapturedAtUtc.HasValue ||
                reservation.CapturedAmountCents.GetValueOrDefault() <= 0)
            {
                return Blocked("Invoice recovery requires a completed captured reservation.");
            }

            if (!reservation.TransactionId.HasValue || reservation.TransactionId.Value != transaction.TransactionId)
            {
                return Blocked("Reservation transaction link does not match the supplied transaction.");
            }

            if (!transaction.StopTime.HasValue || transaction.StopTime.Value < transaction.StartTime)
            {
                return Blocked("Invoice recovery requires a terminal transaction with ordered timestamps.");
            }

            if (string.IsNullOrWhiteSpace(reservation.Currency) ||
                string.IsNullOrWhiteSpace(transaction.Currency) ||
                !string.Equals(reservation.Currency, transaction.Currency, StringComparison.OrdinalIgnoreCase))
            {
                return Blocked("Persisted invoice currency evidence is missing or contradictory.");
            }

            if (transaction.EnergyKwh < 0 || transaction.EnergyCost < 0 ||
                transaction.UserSessionFeeAmount < 0 || transaction.UsageFeeMinutes < 0 ||
                transaction.UsageFeeAmount < 0 || transaction.IdleUsageFeeMinutes < 0 ||
                transaction.IdleUsageFeeAmount < 0)
            {
                return Blocked("Persisted invoice billing breakdown cannot contain negative values.");
            }

            if (transaction.EnergyCost > 0 && transaction.EnergyKwh <= 0)
            {
                return Blocked("Persisted invoice energy breakdown is incomplete.");
            }

            if (transaction.UsageFeeAmount > 0 && transaction.UsageFeeMinutes <= 0)
            {
                return Blocked("Persisted invoice usage-fee breakdown is incomplete.");
            }

            if (reservation.UsageFeeAnchorMinutes == 1 &&
                (transaction.IdleUsageFeeMinutes != transaction.UsageFeeMinutes ||
                 transaction.IdleUsageFeeAmount != transaction.UsageFeeAmount))
            {
                return Blocked("Persisted invoice idle-fee breakdown is incomplete or contradictory.");
            }

            var total = transaction.EnergyCost +
                        transaction.UserSessionFeeAmount +
                        transaction.UsageFeeAmount;
            if (total <= 0)
            {
                return Blocked("Persisted invoice billing breakdown has no positive total.");
            }

            long totalCents;
            try
            {
                totalCents = checked((long)Math.Round(total * 100m, 0, MidpointRounding.AwayFromZero));
            }
            catch (OverflowException)
            {
                return Blocked("Persisted invoice billing total is outside the supported range.");
            }

            if (totalCents != reservation.CapturedAmountCents.Value)
            {
                return Blocked("Persisted invoice billing total contradicts the captured amount.");
            }

            return new FinancialRecoveryInvoiceDecision { Eligible = true };
        }

        private static FinancialRecoveryInvoiceDecision Blocked(string reason) => new()
        {
            Eligible = false,
            Reason = reason
        };
    }

    public sealed class FinancialRecoveryInvoiceDecision
    {
        public bool Eligible { get; set; }
        public string Reason { get; set; }
    }
}
