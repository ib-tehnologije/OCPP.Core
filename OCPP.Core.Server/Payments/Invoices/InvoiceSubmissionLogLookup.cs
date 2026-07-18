using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OCPP.Core.Database;

namespace OCPP.Core.Server.Payments.Invoices
{
    public static class InvoiceSubmissionLogLookup
    {
        public static InvoiceSubmissionLog TryGetLatest(
            OCPPCoreContext dbContext,
            Guid reservationId,
            ILogger logger,
            string reason)
        {
            return TryQuery(
                dbContext,
                reservationId,
                logger,
                reason,
                query => query
                    .OrderByDescending(log => log.CompletedAtUtc ?? log.CreatedAtUtc)
                    .ThenByDescending(log => log.InvoiceSubmissionLogId)
                    .FirstOrDefault());
        }

        public static InvoiceSubmissionLog TryGetLatestDeliverable(
            OCPPCoreContext dbContext,
            Guid reservationId,
            ILogger logger,
            string reason)
        {
            return TryQuery(
                dbContext,
                reservationId,
                logger,
                reason,
                query => query
                    .Where(log =>
                        log.Status == "Submitted" ||
                        log.ExternalPdfUrl != null ||
                        log.ExternalPublicUrl != null ||
                        log.ExternalInvoiceNumber != null ||
                        log.ExternalDocumentId != null)
                    .OrderByDescending(log => log.CompletedAtUtc ?? log.CreatedAtUtc)
                    .ThenByDescending(log => log.InvoiceSubmissionLogId)
                    .FirstOrDefault());
        }

        public static string GetPreferredDocumentUrl(InvoiceSubmissionLog log)
        {
            if (log == null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(log.ExternalPdfUrl))
            {
                return log.ExternalPdfUrl;
            }

            return string.IsNullOrWhiteSpace(log.ExternalPublicUrl) ? null : log.ExternalPublicUrl;
        }

        public static bool HasSubmittedOrExternalInvoice(
            OCPPCoreContext dbContext,
            Guid reservationId,
            ILogger logger,
            string reason)
        {
            return TryHasSubmittedOrExternalInvoice(
                       dbContext,
                       reservationId,
                       logger,
                       reason,
                       out var hasInvoice) &&
                   hasInvoice;
        }

        public static bool TryHasSubmittedOrExternalInvoice(
            OCPPCoreContext dbContext,
            Guid reservationId,
            ILogger logger,
            string reason,
            out bool hasInvoice)
        {
            hasInvoice = false;
            if (dbContext == null || reservationId == Guid.Empty)
            {
                return false;
            }

            try
            {
                hasInvoice = dbContext.InvoiceSubmissionLogs.AsNoTracking().Any(log =>
                    log.ReservationId == reservationId &&
                    (log.Status == "Submitted" ||
                     log.ExternalDocumentId != null ||
                     log.ExternalInvoiceNumber != null ||
                     log.ExternalPublicUrl != null ||
                     log.ExternalPdfUrl != null));
                return true;
            }
            catch (Exception ex)
            {
                logger?.LogDebug(
                    ex,
                    "Invoice/Lookup => Unable to determine issued state reservation={ReservationId} reason={Reason}",
                    reservationId,
                    reason);
                hasInvoice = false;
                return false;
            }
        }

        public static string GetCustomerSafeError(InvoiceSubmissionLog log)
        {
            if (log == null || !string.Equals(log.Status, "Failed", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (log.HttpStatusCode is >= 400 and <= 499)
            {
                return "The invoice provider rejected the buyer details. Review the invoice data or contact support for correction.";
            }

            return "Invoice submission could not be completed. Contact support before retrying or correcting the invoice.";
        }

        private static InvoiceSubmissionLog TryQuery(
            OCPPCoreContext dbContext,
            Guid reservationId,
            ILogger logger,
            string reason,
            Func<IQueryable<InvoiceSubmissionLog>, InvoiceSubmissionLog> selector)
        {
            if (dbContext == null || reservationId == Guid.Empty || selector == null)
            {
                return null;
            }

            try
            {
                var baseQuery = dbContext.InvoiceSubmissionLogs
                    .AsNoTracking()
                    .Where(log => log.ReservationId == reservationId);

                return selector(baseQuery);
            }
            catch (Exception ex)
            {
                logger?.LogDebug(
                    ex,
                    "Invoice/Lookup => Unable to load invoice submission log reservation={ReservationId} reason={Reason}",
                    reservationId,
                    reason);
                return null;
            }
        }
    }
}
