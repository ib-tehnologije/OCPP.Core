using System;
using System.Collections.Generic;

namespace OCPP.Core.Server.Payments.Invoices
{
    public class InvoiceIntegrationOptions
    {
        public bool Enabled { get; set; }
        public string Provider { get; set; } = "ERacuni";
        public string Mode { get; set; } = "LogOnly";
        public ERacuniInvoiceOptions ERacuni { get; set; } = new ERacuniInvoiceOptions();
    }

    public class ERacuniInvoiceOptions
    {
        public string ApiBaseUrl { get; set; } = "https://eurofaktura.com";
        public string ApiPath { get; set; } = "/WebServices/API";
        public string Username { get; set; }
        public string SecretKey { get; set; }
        public string Token { get; set; }
        public string DocumentStatus { get; set; } = "IssuedInvoice";
        public string DocumentType { get; set; } = "Retail";
        public string R1DocumentType { get; set; } = "Retail";
        public string MethodOfPayment { get; set; }
        public string City { get; set; }
        public string BusinessUnit { get; set; }
        public string WarehouseCode { get; set; }
        public string CashRegisterCode { get; set; }
        public string DocumentLanguage { get; set; } = "Croatian";
        public string VatTransactionType { get; set; } = "0";
        public decimal DefaultVatPercentage { get; set; } = 25m;
        public string BuyerVatRegistration { get; set; } = "Registered";
        public string BankAccountNumber { get; set; }
        public string ReferencePrefix { get; set; } = "STRIPE";
        public string OrderReferencePrefix { get; set; } = "EVSE";
        public string RemarksTemplate { get; set; } = "Charge point {ChargePointId}, connector {ConnectorId}, reservation {ReservationId}, transaction {TransactionId}";
        public string TimeZoneId { get; set; } = "Europe/Zagreb";
        public int PaymentDueDays { get; set; }
        public bool SendIssuedInvoiceByEmail { get; set; }
        public bool GeneratePublicUrl { get; set; }
        public int MinimumRequestIntervalMilliseconds { get; set; } = 1100;
        public bool RequireBuyerTaxNumberForR1 { get; set; } = true;
        public Dictionary<string, ERacuniLineItemOptions> LineItems { get; set; } = new Dictionary<string, ERacuniLineItemOptions>();
    }

    public class ERacuniLineItemOptions
    {
        public string ProductCode { get; set; }
        public string ProductCatalogueCode { get; set; }
        public string Unit { get; set; }
        public decimal? VatPercentage { get; set; }
        public string VatTransactionType { get; set; }
        public decimal? DiscountPercentage { get; set; }
    }
}
