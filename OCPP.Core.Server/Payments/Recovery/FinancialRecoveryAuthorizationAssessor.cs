using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using OCPP.Core.Database;

namespace OCPP.Core.Server.Payments.Recovery
{
    public static class FinancialRecoveryAuthorizationAssessor
    {
        public static FinancialRecoveryTransactionEvidenceDecision EvaluateTransactionEvidence(
            OCPPCoreContext dbContext,
            ChargePaymentReservation reservation)
        {
            if (reservation == null)
            {
                return IndeterminateTransactionEvidence("Reservation evidence is missing.");
            }

            if (reservation.TransactionId.HasValue || reservation.StartTransactionId.HasValue)
            {
                return EvaluatedTransactionEvidence(hasTransaction: true);
            }

            if (dbContext == null)
            {
                return IndeterminateTransactionEvidence("Database evidence is unavailable.");
            }

            if (string.IsNullOrWhiteSpace(reservation.ChargePointId))
            {
                return IndeterminateTransactionEvidence("Charge point identity is missing.");
            }

            if (reservation.ConnectorId <= 0)
            {
                return IndeterminateTransactionEvidence("Connector identity is missing or invalid.");
            }

            var hasOcppIdTag = !string.IsNullOrWhiteSpace(reservation.OcppIdTag);
            var hasChargeTagId = !string.IsNullOrWhiteSpace(reservation.ChargeTagId);
            if (!hasOcppIdTag && !hasChargeTagId)
            {
                return IndeterminateTransactionEvidence("Charge tag identity is missing.");
            }

            var windowStartUtc = reservation.AuthorizedAtUtc ?? reservation.CreatedAtUtc;
            if (windowStartUtc == default)
            {
                return IndeterminateTransactionEvidence("Transaction linkage window start is missing.");
            }

            if (!reservation.StartDeadlineAtUtc.HasValue)
            {
                return IndeterminateTransactionEvidence("Transaction linkage window end is missing.");
            }

            var windowEndUtc = reservation.StartDeadlineAtUtc.Value;
            if (windowEndUtc <= windowStartUtc)
            {
                return IndeterminateTransactionEvidence("Transaction linkage time window is invalid.");
            }

            var hasTransaction = dbContext.Transactions.AsNoTracking().Any(transaction =>
                transaction.ChargePointId == reservation.ChargePointId &&
                transaction.ConnectorId == reservation.ConnectorId &&
                ((hasOcppIdTag && transaction.StartTagId == reservation.OcppIdTag) ||
                 (hasChargeTagId && transaction.StartTagId == reservation.ChargeTagId)) &&
                transaction.StartTime >= windowStartUtc &&
                transaction.StartTime <= windowEndUtc);
            return EvaluatedTransactionEvidence(hasTransaction);
        }

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

        private static FinancialRecoveryTransactionEvidenceDecision EvaluatedTransactionEvidence(
            bool hasTransaction) => new()
        {
            CanEvaluate = true,
            HasTransaction = hasTransaction
        };

        private static FinancialRecoveryTransactionEvidenceDecision IndeterminateTransactionEvidence(
            string reason) => new()
        {
            CanEvaluate = false,
            HasTransaction = false,
            Reason = $"Authoritative transaction linkage cannot be evaluated: {reason}"
        };
    }

    public sealed class FinancialRecoveryTransactionEvidenceDecision
    {
        public bool CanEvaluate { get; set; }
        public bool HasTransaction { get; set; }
        public string Reason { get; set; }
    }

    public sealed class FinancialRecoveryAuthorizationDecision
    {
        public bool Eligible { get; set; }
        public string Reason { get; set; }
    }
}
