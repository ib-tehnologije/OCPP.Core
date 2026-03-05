using System;

namespace OCPP.Core.Database
{
    /// <summary>
    /// Durable audit trail for invoice provider submissions.
    /// Stores sanitized request payloads plus raw provider responses for support/reconciliation.
    /// </summary>
    public class InvoiceSubmissionLog
    {
        public int InvoiceSubmissionLogId { get; set; }
        public Guid ReservationId { get; set; }
        public int? TransactionId { get; set; }
        public string Provider { get; set; }
        public string Mode { get; set; }
        public string Status { get; set; }
        public string InvoiceKind { get; set; }
        public string ProviderOperation { get; set; }
        public string ApiTransactionId { get; set; }
        public string StripeCheckoutSessionId { get; set; }
        public string StripePaymentIntentId { get; set; }
        public int? HttpStatusCode { get; set; }
        public string ExternalDocumentId { get; set; }
        public string ExternalInvoiceNumber { get; set; }
        public string ExternalPublicUrl { get; set; }
        public string ExternalPdfUrl { get; set; }
        public string ProviderResponseStatus { get; set; }
        public string RequestPayloadJson { get; set; }
        public string ResponseBody { get; set; }
        public string Error { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime? CompletedAtUtc { get; set; }
    }
}
