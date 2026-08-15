using System;
using OCPP.Core.Database;

namespace OCPP.Core.Server.Payments.Recovery
{
    public static class FinancialRecoveryAuthorizationAssessor
    {
        public static FinancialRecoveryAuthorizationDecision Assess(
            ChargePaymentReservation reservation,
            bool hasTransaction,
            bool hasInvoiceEvidence)
        {
            if (reservation == null)
            {
                return Blocked("Reservation evidence is missing.");
            }

            if (!string.Equals(reservation.Status, PaymentReservationStatus.Abandoned, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(reservation.Status, PaymentReservationStatus.Failed, StringComparison.OrdinalIgnoreCase))
            {
                return Blocked("Reservation is not in an eligible terminal status.");
            }

            if (hasTransaction || reservation.TransactionId.HasValue || reservation.StartTransactionId.HasValue)
            {
                return Blocked("Reservation has transaction evidence.");
            }

            if (reservation.ActualEnergyKwh.GetValueOrDefault() != 0)
            {
                return Blocked("Reservation has delivered energy evidence.");
            }

            if (reservation.CapturedAmountCents.GetValueOrDefault() != 0 || reservation.CapturedAtUtc.HasValue)
            {
                return Blocked("Reservation has captured payment evidence.");
            }

            if (hasInvoiceEvidence)
            {
                return Blocked("Reservation has invoice evidence.");
            }

            if (string.IsNullOrWhiteSpace(reservation.StripePaymentIntentId))
            {
                return Blocked("Reservation has no provider payment intent.");
            }

            if (string.Equals(reservation.AuthorizationReleaseState, PaymentAuthorizationReleaseState.Released, StringComparison.OrdinalIgnoreCase))
            {
                return Blocked("Reservation authorization is already released.");
            }

            return new FinancialRecoveryAuthorizationDecision { Eligible = true };
        }

        private static FinancialRecoveryAuthorizationDecision Blocked(string reason) => new()
        {
            Eligible = false,
            Reason = reason
        };
    }

    public sealed class FinancialRecoveryAuthorizationDecision
    {
        public bool Eligible { get; set; }
        public string Reason { get; set; }
    }
}
