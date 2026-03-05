using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace OCPP.Core.Server.Payments.Invoices.ERacuni
{
    public class ERacuniApiResponseMetadata
    {
        public string DocumentId { get; set; }
        public string InvoiceNumber { get; set; }
        public string PublicUrl { get; set; }
        public string PdfUrl { get; set; }
        public string Status { get; set; }
    }

    public static class ERacuniApiResponseMetadataReader
    {
        public static ERacuniApiResponseMetadata Read(JToken parsedBody)
        {
            if (parsedBody == null)
            {
                return new ERacuniApiResponseMetadata();
            }

            return new ERacuniApiResponseMetadata
            {
                DocumentId = FindFirstString(parsedBody, "documentId", "salesInvoiceId", "invoiceId", "salesInvoiceDocumentId"),
                InvoiceNumber = FindFirstString(parsedBody, "invoiceNumber", "documentNumber", "number"),
                PublicUrl = FindFirstString(parsedBody, "publicURL", "publicUrl", "publicLink"),
                PdfUrl = FindFirstString(parsedBody, "pdfURL", "pdfUrl", "pdfLink", "downloadPdfUrl"),
                Status = FindFirstString(parsedBody, "status", "documentStatus")
            };
        }

        private static string FindFirstString(JToken token, params string[] candidateNames)
        {
            if (token == null || candidateNames == null || candidateNames.Length == 0)
            {
                return null;
            }

            var candidates = new HashSet<string>(candidateNames, StringComparer.OrdinalIgnoreCase);
            foreach (var value in EnumerateMatches(token, candidates))
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return null;
        }

        private static IEnumerable<string> EnumerateMatches(JToken token, HashSet<string> candidateNames)
        {
            if (token is JObject obj)
            {
                foreach (var property in obj.Properties())
                {
                    if (candidateNames.Contains(property.Name))
                    {
                        var directValue = property.Value.Type == JTokenType.Null
                            ? null
                            : property.Value.ToString();

                        if (!string.IsNullOrWhiteSpace(directValue))
                        {
                            yield return directValue;
                        }
                    }

                    foreach (var nested in EnumerateMatches(property.Value, candidateNames))
                    {
                        yield return nested;
                    }
                }
            }
            else if (token is JArray array)
            {
                foreach (var item in array)
                {
                    foreach (var nested in EnumerateMatches(item, candidateNames))
                    {
                        yield return nested;
                    }
                }
            }
        }
    }
}
