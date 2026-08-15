using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using OCPP.Core.Database;
using OCPP.Core.Server.Payments.Invoices;

namespace OCPP.Core.Server.Payments.Recovery
{
    public sealed class FinancialRecoveryService
    {
        private readonly IPaymentCoordinator _paymentCoordinator;
        private readonly IInvoiceIntegrationService _invoiceIntegrationService;

        public FinancialRecoveryService(
            IPaymentCoordinator paymentCoordinator,
            IInvoiceIntegrationService invoiceIntegrationService)
        {
            _paymentCoordinator = paymentCoordinator;
            _invoiceIntegrationService = invoiceIntegrationService;
        }

        public FinancialRecoveryReport Run(
            OCPPCoreContext dbContext,
            FinancialRecoveryManifest manifest,
            bool execute,
            string confirmationSha256)
        {
            if (dbContext == null) throw new ArgumentNullException(nameof(dbContext));
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            manifest.RequireExecutionConfirmation(execute, confirmationSha256);

            var items = new List<FinancialRecoveryReportItem>();
            foreach (var entry in manifest.Entries)
            {
                dbContext.ChangeTracker.Clear();
                var reservation = dbContext.ChargePaymentReservations
                    .SingleOrDefault(candidate => candidate.ReservationId == entry.ReservationId);
                items.Add(RunEntry(dbContext, entry, reservation, execute));
            }

            return new FinancialRecoveryReport(manifest.Sha256, execute, items);
        }

        private FinancialRecoveryReportItem RunEntry(
            OCPPCoreContext dbContext,
            FinancialRecoveryManifestEntry entry,
            ChargePaymentReservation reservation,
            bool execute)
        {
            if (reservation == null)
            {
                return Blocked(entry, "Allowlisted reservation was not found.");
            }

            if (string.Equals(entry.Operation, "recover-settlement", StringComparison.OrdinalIgnoreCase))
            {
                return RunSettlement(dbContext, entry, reservation, execute);
            }

            if (string.Equals(entry.Operation, "release-authorization", StringComparison.OrdinalIgnoreCase))
            {
                return RunAuthorizationRelease(dbContext, entry, reservation, execute);
            }

            if (string.Equals(entry.Operation, "recover-invoice", StringComparison.OrdinalIgnoreCase))
            {
                return RunInvoice(dbContext, entry, reservation, execute);
            }

            return Blocked(entry, "Operation is unsupported.");
        }

        private FinancialRecoveryReportItem RunSettlement(
            OCPPCoreContext dbContext,
            FinancialRecoveryManifestEntry entry,
            ChargePaymentReservation reservation,
            bool execute)
        {
            var transaction = reservation.TransactionId.HasValue
                ? dbContext.Transactions.SingleOrDefault(candidate => candidate.TransactionId == reservation.TransactionId.Value)
                : null;
            var assessment = FinancialRecoverySettlementAssessor.Assess(reservation, transaction);
            if (!assessment.Eligible || assessment.TotalCents <= 0)
            {
                return Blocked(entry, assessment.Eligible
                    ? "Derived billable amount is not positive."
                    : assessment.Reason);
            }

            if (execute)
            {
                if (_paymentCoordinator == null)
                {
                    return Blocked(entry, "Payment coordinator is unavailable.");
                }

                _paymentCoordinator.RecoverTerminalSettlement(dbContext, transaction);
            }

            return Eligible(entry, execute ? "Executed" : "DryRunEligible");
        }

        private FinancialRecoveryReportItem RunAuthorizationRelease(
            OCPPCoreContext dbContext,
            FinancialRecoveryManifestEntry entry,
            ChargePaymentReservation reservation,
            bool execute)
        {
            var hasTransaction = reservation.TransactionId.HasValue &&
                dbContext.Transactions.Any(candidate => candidate.TransactionId == reservation.TransactionId.Value);
            var hasInvoiceEvidence = dbContext.InvoiceSubmissionLogs.AsNoTracking().Any(log =>
                log.ReservationId == reservation.ReservationId &&
                (log.Status == "Submitted" ||
                 log.ExternalDocumentId != null ||
                 log.ExternalInvoiceNumber != null ||
                 log.ExternalPublicUrl != null ||
                 log.ExternalPdfUrl != null));
            var assessment = FinancialRecoveryAuthorizationAssessor.Assess(
                reservation,
                hasTransaction,
                hasInvoiceEvidence);
            if (!assessment.Eligible)
            {
                return Blocked(entry, assessment.Reason);
            }

            if (execute)
            {
                if (_paymentCoordinator == null)
                {
                    return Blocked(entry, "Payment coordinator is unavailable.");
                }

                reservation.AuthorizationReleaseState = PaymentAuthorizationReleaseState.Pending;
                reservation.AuthorizationReleaseNextAttemptAtUtc = null;
                reservation.UpdatedAtUtc = DateTime.UtcNow;
                dbContext.SaveChanges();
                var result = _paymentCoordinator.ReconcileTerminalPaymentAuthorization(
                    dbContext,
                    reservation,
                    "ExplicitFinancialRecovery");
                return new FinancialRecoveryReportItem(
                    entry.Operation,
                    entry.ReservationId,
                    !string.Equals(result.Outcome, PaymentAuthorizationReleaseOutcome.ReviewRequired, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(result.Outcome, PaymentAuthorizationReleaseOutcome.PermanentFailure, StringComparison.OrdinalIgnoreCase),
                    result.Outcome);
            }

            return Eligible(entry, "DryRunEligibleProviderRecheckRequired");
        }

        private FinancialRecoveryReportItem RunInvoice(
            OCPPCoreContext dbContext,
            FinancialRecoveryManifestEntry entry,
            ChargePaymentReservation reservation,
            bool execute)
        {
            if (!string.Equals(reservation.Status, PaymentReservationStatus.Completed, StringComparison.OrdinalIgnoreCase) ||
                !reservation.CapturedAtUtc.HasValue || reservation.CapturedAmountCents.GetValueOrDefault() <= 0 ||
                !reservation.TransactionId.HasValue)
            {
                return Blocked(entry, "Invoice recovery requires a completed captured reservation with a linked transaction.");
            }

            var transaction = dbContext.Transactions
                .SingleOrDefault(candidate => candidate.TransactionId == reservation.TransactionId.Value);
            if (transaction == null)
            {
                return Blocked(entry, "Linked transaction was not found.");
            }

            if (execute)
            {
                if (_invoiceIntegrationService == null)
                {
                    return Blocked(entry, "Invoice integration service is unavailable.");
                }

                _invoiceIntegrationService.HandleCompletedReservation(dbContext, reservation, transaction, checkoutSession: null);
            }

            return Eligible(entry, execute ? "Executed" : "DryRunEligibleProviderLookupRequired");
        }

        private static FinancialRecoveryReportItem Eligible(
            FinancialRecoveryManifestEntry entry,
            string outcome) =>
            new(entry.Operation, entry.ReservationId, true, outcome);

        private static FinancialRecoveryReportItem Blocked(
            FinancialRecoveryManifestEntry entry,
            string reason) =>
            new(entry.Operation, entry.ReservationId, false, reason);
    }

    public sealed class FinancialRecoveryReport
    {
        public FinancialRecoveryReport(
            string manifestSha256,
            bool executed,
            IReadOnlyList<FinancialRecoveryReportItem> items)
        {
            ManifestSha256 = manifestSha256;
            Executed = executed;
            Items = items;
        }

        public string ManifestSha256 { get; }
        public bool Executed { get; }
        public IReadOnlyList<FinancialRecoveryReportItem> Items { get; }
        public bool Succeeded => Items.All(item => item.Eligible);
    }

    public sealed record FinancialRecoveryReportItem(
        string Operation,
        Guid ReservationId,
        bool Eligible,
        string Outcome);
}
