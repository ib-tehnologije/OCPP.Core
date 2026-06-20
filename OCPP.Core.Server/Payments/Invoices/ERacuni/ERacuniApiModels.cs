using System.Collections.Generic;
using System.Net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OCPP.Core.Server.Payments.Invoices.ERacuni
{
    public class ERacuniApiRequestEnvelope
    {
        [JsonProperty("username")]
        public string Username { get; set; }

        [JsonProperty("secretKey")]
        public string SecretKey { get; set; }

        [JsonProperty("token")]
        public string Token { get; set; }

        [JsonProperty("method")]
        public string Method { get; set; }

        [JsonProperty("parameters")]
        public object Parameters { get; set; }
    }

    public class ERacuniSalesInvoiceCreateParameters
    {
        [JsonProperty("apiTransactionId")]
        public string ApiTransactionId { get; set; }

        [JsonProperty("SalesInvoice")]
        public ERacuniSalesInvoice SalesInvoice { get; set; }

        [JsonProperty("sendIssuedInvoiceByEmail")]
        public bool SendIssuedInvoiceByEmail { get; set; }

        [JsonProperty("generatePublicURL")]
        public bool GeneratePublicUrl { get; set; }
    }

    public class ERacuniSalesInvoice
    {
        [JsonProperty("status")]
        public string Status { get; set; }

        [JsonProperty("city")]
        public string City { get; set; }

        [JsonProperty("businessYear")]
        public int? BusinessYear { get; set; }

        [JsonProperty("businessUnit")]
        public string BusinessUnit { get; set; }

        [JsonProperty("warehouseCode")]
        public string WarehouseCode { get; set; }

        [JsonProperty("cashRegisterCode")]
        public string CashRegisterCode { get; set; }

        [JsonProperty("documentCurrency")]
        public string DocumentCurrency { get; set; }

        [JsonProperty("date")]
        public string Date { get; set; }

        [JsonProperty("documentLanguage")]
        public string DocumentLanguage { get; set; }

        [JsonProperty("paymentDueDate")]
        public string PaymentDueDate { get; set; }

        [JsonProperty("dateOfSupplyFrom")]
        public string DateOfSupplyFrom { get; set; }

        [JsonProperty("dateOfSupplyUntil")]
        public string DateOfSupplyUntil { get; set; }

        [JsonProperty("vatTransactionType")]
        public string VatTransactionType { get; set; }

        [JsonProperty("buyerName")]
        public string BuyerName { get; set; }

        [JsonProperty("buyerCountry")]
        public string BuyerCountry { get; set; }

        [JsonProperty("buyerTaxNumber")]
        public string BuyerTaxNumber { get; set; }

        [JsonProperty("buyerVatRegistration")]
        public string BuyerVatRegistration { get; set; }

        [JsonProperty("buyerCode")]
        public string BuyerCode { get; set; }

        [JsonProperty("buyerEMail")]
        public string BuyerEMail { get; set; }

        [JsonProperty("methodOfPayment")]
        public string MethodOfPayment { get; set; }

        [JsonProperty("bankAccountNumber")]
        public string BankAccountNumber { get; set; }

        [JsonProperty("reference")]
        public string Reference { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("remarks")]
        public string Remarks { get; set; }

        [JsonProperty("orderReference")]
        public string OrderReference { get; set; }

        [JsonProperty("Items")]
        public List<ERacuniSalesInvoiceItem> Items { get; set; } = new List<ERacuniSalesInvoiceItem>();
    }

    public class ERacuniSalesInvoiceItem
    {
        [JsonProperty("productCode")]
        public string ProductCode { get; set; }

        [JsonProperty("productCatalogueCode")]
        public string ProductCatalogueCode { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("quantity")]
        public decimal Quantity { get; set; }

        [JsonProperty("unit")]
        public string Unit { get; set; }

        [JsonProperty("currency")]
        public string Currency { get; set; }

        [JsonProperty("price")]
        public decimal? Price { get; set; }

        [JsonProperty("netPrice")]
        public decimal? NetPrice { get; set; }

        [JsonProperty("vatTransactionType")]
        public string VatTransactionType { get; set; }

        [JsonProperty("discountPercentage")]
        public decimal? DiscountPercentage { get; set; }

        [JsonProperty("vatPercentage")]
        public decimal? VatPercentage { get; set; }
    }

    public class ERacuniApiResult
    {
        public HttpStatusCode StatusCode { get; set; }
        public string Body { get; set; }
        public JToken ParsedBody { get; set; }
    }
}
