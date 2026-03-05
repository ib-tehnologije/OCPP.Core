using System;
using System.Collections.Generic;

namespace OCPP.Core.Server.Payments.Invoices
{
    public class InvoiceDraft
    {
        public Guid ReservationId { get; set; }
        public int TransactionId { get; set; }
        public string InvoiceKind { get; set; }
        public DateTime IssueDateUtc { get; set; }
        public DateTime ServiceDateFromUtc { get; set; }
        public DateTime? ServiceDateToUtc { get; set; }
        public string Currency { get; set; }
        public string BuyerCompanyName { get; set; }
        public string BuyerOib { get; set; }
        public string BuyerCode { get; set; }
        public string BuyerEmail { get; set; }
        public string ChargePointId { get; set; }
        public int ConnectorId { get; set; }
        public string StripeCheckoutSessionId { get; set; }
        public string StripePaymentIntentId { get; set; }
        public long? CapturedAmountCents { get; set; }
        public decimal TotalAmount { get; set; }
        public List<InvoiceDraftLine> Lines { get; set; } = new List<InvoiceDraftLine>();
    }

    public class InvoiceDraftLine
    {
        public string Type { get; set; }
        public string Description { get; set; }
        public decimal Quantity { get; set; }
        public string UnitCode { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal LineAmount { get; set; }
    }
}
