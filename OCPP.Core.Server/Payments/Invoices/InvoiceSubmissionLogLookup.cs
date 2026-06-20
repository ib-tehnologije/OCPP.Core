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
