using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;

namespace OCPP.Core.Server.Payments.Invoices.ERacuni
{
    public interface IERacuniInvoiceRequestFactory
    {
        ERacuniApiRequestEnvelope BuildCreateSalesInvoiceRequest(InvoiceDraft draft);
        object BuildSanitizedLogPayload(ERacuniApiRequestEnvelope request);
    }

    public class ERacuniInvoiceRequestFactory : IERacuniInvoiceRequestFactory
    {
        private readonly InvoiceIntegrationOptions _options;

        public ERacuniInvoiceRequestFactory(IOptions<InvoiceIntegrationOptions> options)
        {
            _options = options?.Value ?? new InvoiceIntegrationOptions();
        }

        public ERacuniApiRequestEnvelope BuildCreateSalesInvoiceRequest(InvoiceDraft draft)
        {
            if (draft == null) throw new ArgumentNullException(nameof(draft));

            var eracuni = _options.ERacuni ?? new ERacuniInvoiceOptions();
            if (draft.Lines == null || draft.Lines.Count == 0)
            {
                throw new InvalidOperationException("Cannot create e-racuni payload without invoice lines.");
            }

            if (string.Equals(draft.InvoiceKind, "R1", StringComparison.OrdinalIgnoreCase) &&
                eracuni.RequireBuyerTaxNumberForR1 &&
                string.IsNullOrWhiteSpace(draft.BuyerOib))
            {
                throw new InvalidOperationException("R1 invoice requires buyer OIB/tax number before e-racuni submission.");
            }

            var timeZone = ResolveTimeZone(eracuni.TimeZoneId);
            var issueDate = ToLocalDate(draft.IssueDateUtc, timeZone);
            var supplyFromDate = ToLocalDate(draft.ServiceDateFromUtc, timeZone);
            var supplyUntilDate = ToLocalDate(draft.ServiceDateToUtc ?? draft.IssueDateUtc, timeZone);
            var paymentDueDate = issueDate.AddDays(Math.Max(0, eracuni.PaymentDueDays));
            var documentType = ResolveDocumentType(draft, eracuni);

            var salesInvoice = new ERacuniSalesInvoice
            {
                Status = NullIfWhiteSpace(eracuni.DocumentStatus) ?? "IssuedInvoice",
                City = NullIfWhiteSpace(eracuni.City),
                BusinessYear = issueDate.Year,
                BusinessUnit = NullIfWhiteSpace(eracuni.BusinessUnit),
                WarehouseCode = NullIfWhiteSpace(eracuni.WarehouseCode),
                CashRegisterCode = NullIfWhiteSpace(eracuni.CashRegisterCode),
                DocumentCurrency = NullIfWhiteSpace(draft.Currency) ?? "EUR",
                Date = FormatDate(issueDate),
                DocumentLanguage = NullIfWhiteSpace(eracuni.DocumentLanguage),
                PaymentDueDate = FormatDate(paymentDueDate),
                DateOfSupplyFrom = FormatDate(supplyFromDate),
                DateOfSupplyUntil = FormatDate(supplyUntilDate),
                VatTransactionType = NullIfWhiteSpace(eracuni.VatTransactionType),
                BuyerName = NullIfWhiteSpace(draft.BuyerCompanyName),
                BuyerCountry = "HR",
                BuyerTaxNumber = NullIfWhiteSpace(draft.BuyerOib),
                BuyerVatRegistration = ResolveBuyerVatRegistration(draft, eracuni),
                BuyerCode = ResolveBuyerCode(draft),
                BuyerEMail = NullIfWhiteSpace(draft.BuyerEmail),
                MethodOfPayment = NullIfWhiteSpace(eracuni.MethodOfPayment),
                BankAccountNumber = NullIfWhiteSpace(eracuni.BankAccountNumber),
                Reference = ResolveReference(draft, eracuni),
                Type = documentType,
                Remarks = ResolveRemarks(draft, eracuni),
                OrderReference = ResolveOrderReference(draft, eracuni),
                Items = draft.Lines.Select(line => MapItem(line, draft.Currency, documentType, eracuni)).ToList()
            };

            return new ERacuniApiRequestEnvelope
            {
                Username = NullIfWhiteSpace(eracuni.Username),
                SecretKey = NullIfWhiteSpace(eracuni.SecretKey),
                Token = NullIfWhiteSpace(eracuni.Token),
                Method = "SalesInvoiceCreate",
                Parameters = new ERacuniSalesInvoiceCreateParameters
                {
                    ApiTransactionId = draft.ReservationId.ToString("N"),
                    SalesInvoice = salesInvoice,
                    SendIssuedInvoiceByEmail = eracuni.SendIssuedInvoiceByEmail,
                    GeneratePublicUrl = eracuni.GeneratePublicUrl
                }
            };
        }

        public object BuildSanitizedLogPayload(ERacuniApiRequestEnvelope request)
        {
            if (request == null)
            {
                return null;
            }

            var clone = JObject.FromObject(request);
            clone["secretKey"] = MaskValue(request.SecretKey);
            clone["token"] = MaskValue(request.Token);
            return clone;
        }

        private static ERacuniSalesInvoiceItem MapItem(InvoiceDraftLine line, string currency, string documentType, ERacuniInvoiceOptions options)
        {
            if (line == null) throw new ArgumentNullException(nameof(line));

            var lineOptions = ResolveLineOptions(line.Type, options);
            var item = new ERacuniSalesInvoiceItem
            {
                ProductCode = NullIfWhiteSpace(lineOptions?.ProductCode),
                ProductCatalogueCode = NullIfWhiteSpace(lineOptions?.ProductCatalogueCode),
                Description = NullIfWhiteSpace(line.Description),
                Quantity = line.Quantity,
                Unit = NullIfWhiteSpace(lineOptions?.Unit) ?? NullIfWhiteSpace(line.UnitCode),
                Currency = NullIfWhiteSpace(currency) ?? "EUR",
                VatTransactionType = NullIfWhiteSpace(lineOptions?.VatTransactionType) ?? NullIfWhiteSpace(options.VatTransactionType),
                DiscountPercentage = lineOptions?.DiscountPercentage,
                VatPercentage = lineOptions?.VatPercentage ?? options.DefaultVatPercentage
            };

            if (string.Equals(documentType, "Gross", StringComparison.OrdinalIgnoreCase))
            {
                item.NetPrice = line.UnitPrice;
            }
            else
            {
                item.Price = line.UnitPrice;
            }

            return item;
        }

        private static ERacuniLineItemOptions ResolveLineOptions(string lineType, ERacuniInvoiceOptions options)
        {
            if (options?.LineItems == null || options.LineItems.Count == 0 || string.IsNullOrWhiteSpace(lineType))
            {
                return null;
            }

            if (options.LineItems.TryGetValue(lineType, out var lineOptions))
            {
                return lineOptions;
            }

            return options.LineItems.FirstOrDefault(entry =>
                string.Equals(entry.Key, lineType, StringComparison.OrdinalIgnoreCase)).Value;
        }

        private static string ResolveDocumentType(InvoiceDraft draft, ERacuniInvoiceOptions options)
        {
            if (draft == null)
            {
                return "Retail";
            }

            if (string.Equals(draft.InvoiceKind, "R1", StringComparison.OrdinalIgnoreCase))
            {
                return NullIfWhiteSpace(options?.R1DocumentType) ?? NullIfWhiteSpace(options?.DocumentType) ?? "Retail";
            }

            return NullIfWhiteSpace(options?.DocumentType) ?? "Retail";
        }

        private static string ResolveBuyerVatRegistration(InvoiceDraft draft, ERacuniInvoiceOptions options)
        {
            if (draft == null || string.IsNullOrWhiteSpace(draft.BuyerOib))
            {
                return null;
            }

            return NullIfWhiteSpace(options?.BuyerVatRegistration) ?? "Registered";
        }

        private static string ResolveBuyerCode(InvoiceDraft draft)
        {
            return NullIfWhiteSpace(draft?.BuyerCode);
        }

        private static string ResolveReference(InvoiceDraft draft, ERacuniInvoiceOptions options)
        {
            var identifier = NullIfWhiteSpace(draft?.StripePaymentIntentId) ??
                             NullIfWhiteSpace(draft?.StripeCheckoutSessionId) ??
                             draft?.ReservationId.ToString("N");
            var prefix = NullIfWhiteSpace(options?.ReferencePrefix);
            return JoinParts(prefix, identifier);
        }

        private static string ResolveOrderReference(InvoiceDraft draft, ERacuniInvoiceOptions options)
        {
            var prefix = NullIfWhiteSpace(options?.OrderReferencePrefix);
            return JoinParts(prefix, draft?.TransactionId.ToString(CultureInfo.InvariantCulture));
        }

        private static string ResolveRemarks(InvoiceDraft draft, ERacuniInvoiceOptions options)
        {
            var template = NullIfWhiteSpace(options?.RemarksTemplate);
            if (string.IsNullOrWhiteSpace(template) || draft == null)
            {
                return null;
            }

            return template
                .Replace("{ChargePointId}", draft.ChargePointId ?? string.Empty)
                .Replace("{ConnectorId}", draft.ConnectorId.ToString(CultureInfo.InvariantCulture))
                .Replace("{ReservationId}", draft.ReservationId.ToString())
                .Replace("{TransactionId}", draft.TransactionId.ToString(CultureInfo.InvariantCulture))
                .Replace("{StripePaymentIntentId}", draft.StripePaymentIntentId ?? string.Empty)
                .Replace("{StripeCheckoutSessionId}", draft.StripeCheckoutSessionId ?? string.Empty)
                .Trim();
        }

        private static TimeZoneInfo ResolveTimeZone(string timeZoneId)
        {
            try
            {
                return string.IsNullOrWhiteSpace(timeZoneId)
                    ? TimeZoneInfo.Utc
                    : TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            }
            catch (TimeZoneNotFoundException)
            {
                return TimeZoneInfo.Utc;
            }
            catch (InvalidTimeZoneException)
            {
                return TimeZoneInfo.Utc;
            }
        }

        private static DateTime ToLocalDate(DateTime utcDateTime, TimeZoneInfo timeZone)
        {
            var utc = utcDateTime.Kind == DateTimeKind.Utc
                ? utcDateTime
                : DateTime.SpecifyKind(utcDateTime, DateTimeKind.Utc);
            return TimeZoneInfo.ConvertTimeFromUtc(utc, timeZone).Date;
        }

        private static string FormatDate(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        private static string JoinParts(params string[] values)
        {
            var parts = values
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .ToArray();
            return parts.Length == 0 ? null : string.Join("-", parts);
        }

        private static string NullIfWhiteSpace(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static string MaskValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var trimmed = value.Trim();
            if (trimmed.Length <= 4)
            {
                return "****";
            }

            return $"{trimmed.Substring(0, 2)}***{trimmed.Substring(trimmed.Length - 2, 2)}";
        }
    }
}
