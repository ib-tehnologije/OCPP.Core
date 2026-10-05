using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OCPP.Core.Server.Payments.Invoices.ERacuni
{
    public interface IERacuniApiClient
    {
        ERacuniApiResult CreateSalesInvoice(ERacuniApiRequestEnvelope request);
        ERacuniInvoiceLookupResult LookupSalesInvoice(
            ERacuniApiRequestEnvelope request,
            ERacuniSalesInvoiceLookupCriteria criteria) =>
            ERacuniInvoiceLookupResult.Unknown("Provider lookup is not implemented.");
    }

    public class ERacuniApiClient : IERacuniApiClient
    {
        /// <summary>Maximum number of rows SalesInvoiceList returns in one response.</summary>
        public const int ProviderListRowLimit = 500;

        /// <summary>Upper bound on the inclusive invoice-date window a lookup may request.</summary>
        public const int MaxLookupWindowDays = 31;

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly InvoiceIntegrationOptions _options;
        private readonly ILogger<ERacuniApiClient> _logger;
        private readonly SemaphoreSlim _requestLock = new SemaphoreSlim(1, 1);
        private DateTime _lastRequestStartedUtc = DateTime.MinValue;

        public ERacuniApiClient(
            IHttpClientFactory httpClientFactory,
            IOptions<InvoiceIntegrationOptions> options,
            ILogger<ERacuniApiClient> logger)
        {
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _options = options?.Value ?? new InvoiceIntegrationOptions();
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public ERacuniApiResult CreateSalesInvoice(ERacuniApiRequestEnvelope request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            return Send(request);
        }

        public ERacuniInvoiceLookupResult LookupSalesInvoice(
            ERacuniApiRequestEnvelope request,
            ERacuniSalesInvoiceLookupCriteria criteria)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.Parameters is not ERacuniSalesInvoiceLookupParameters parameters ||
                string.IsNullOrWhiteSpace(criteria?.OrderReference) ||
                !criteria.TotalAmount.HasValue ||
                !TryParseProviderDate(parameters.DateFrom, out var dateFrom) ||
                !TryParseProviderDate(parameters.DateTo, out var dateTo))
            {
                return Unknown(
                    "Exact provider order reference, total amount, or invoice-date window is missing.",
                    requestAttempted: false,
                    ERacuniInvoiceLookupFailureCategory.MissingReference);
            }

            if (dateFrom > dateTo || (dateTo - dateFrom).TotalDays >= MaxLookupWindowDays)
            {
                return Unknown(
                    "Provider lookup invoice-date window is inverted or wider than the supported bound.",
                    requestAttempted: false,
                    ERacuniInvoiceLookupFailureCategory.Configuration);
            }

            ERacuniApiResult response;
            var requestAttempted = false;
            int? receivedHttpStatusCode = null;
            try
            {
                response = Send(
                    request,
                    () => requestAttempted = true,
                    statusCode => receivedHttpStatusCode = (int)statusCode);
            }
            catch (InvalidOperationException) when (!requestAttempted)
            {
                return Unknown(
                    "Provider lookup configuration preflight failed.",
                    requestAttempted: false,
                    ERacuniInvoiceLookupFailureCategory.Configuration);
            }
            catch (Exception)
            {
                return Unknown(
                    "Provider lookup transport failed.",
                    requestAttempted,
                    requestAttempted
                        ? ERacuniInvoiceLookupFailureCategory.Transport
                        : ERacuniInvoiceLookupFailureCategory.Configuration,
                    receivedHttpStatusCode);
            }

            var status = (int)response.StatusCode;
            var parsedBody = TryParseLookupJson(response.Body);
            var responseShape = ClassifyResponseShape(parsedBody);
            if (status < 200 || status > 299)
            {
                return Unknown(
                    "Provider lookup returned a non-success HTTP response.",
                    requestAttempted,
                    ERacuniInvoiceLookupFailureCategory.HttpStatus,
                    status,
                    responseShape);
            }

            if (parsedBody == null)
            {
                return Unknown(
                    "Provider lookup returned a non-JSON response.",
                    requestAttempted,
                    ERacuniInvoiceLookupFailureCategory.NonJsonResponse,
                    status,
                    responseShape);
            }

            var envelope = parsedBody is JObject rootObject &&
                           rootObject.GetValue("response", StringComparison.OrdinalIgnoreCase) is JObject nestedResponse
                ? nestedResponse
                : parsedBody as JObject;
            var providerStatus = envelope?.GetValue("status", StringComparison.OrdinalIgnoreCase);
            if (providerStatus != null &&
                !string.Equals(providerStatus.ToString().Trim(), "ok", StringComparison.OrdinalIgnoreCase))
            {
                return Unknown(
                    "Provider lookup returned a non-ok provider status.",
                    requestAttempted,
                    ERacuniInvoiceLookupFailureCategory.ProviderErrorStatus,
                    status,
                    responseShape);
            }

            var rows = parsedBody as JArray ??
                       envelope?.GetValue("result", StringComparison.OrdinalIgnoreCase) as JArray;
            if (rows == null)
            {
                return Unknown(
                    "Provider lookup returned an unrecognized response shape.",
                    requestAttempted,
                    ERacuniInvoiceLookupFailureCategory.UnrecognizedResponse,
                    status,
                    responseShape);
            }

            // SalesInvoiceList returns at most one page. A full page may hide the matching
            // invoice, so absence can only be proven from a page below the provider limit.
            if (rows.Count >= ProviderListRowLimit)
            {
                return Unknown(
                    "Provider lookup returned a full page that may be truncated; absence cannot be proven.",
                    requestAttempted,
                    ERacuniInvoiceLookupFailureCategory.TruncatedResponse,
                    status,
                    responseShape);
            }

            var expectedOrderReference = criteria.OrderReference.Trim();
            var expectedReference = string.IsNullOrWhiteSpace(criteria.Reference) ? null : criteria.Reference.Trim();
            var candidates = new List<JObject>();
            foreach (var row in rows)
            {
                if (row is not JObject invoice ||
                    !TryParseProviderDate(ReadString(invoice, "date"), out var invoiceDate) ||
                    invoice.GetValue("orderReference", StringComparison.OrdinalIgnoreCase) == null)
                {
                    return Unknown(
                        "Provider lookup row does not expose a recognizable invoice date and order reference.",
                        requestAttempted,
                        ERacuniInvoiceLookupFailureCategory.UnrecognizedRow,
                        status,
                        responseShape);
                }

                if (invoiceDate < dateFrom || invoiceDate > dateTo)
                {
                    return Unknown(
                        "Provider lookup returned an invoice outside the requested date window.",
                        requestAttempted,
                        ERacuniInvoiceLookupFailureCategory.RowOutsideDateWindow,
                        status,
                        responseShape);
                }

                // Either identifier makes a row a candidate. Absence is proven only when neither matches.
                if (IdentifierEquals(invoice, "orderReference", expectedOrderReference) ||
                    (expectedReference != null && IdentifierEquals(invoice, "reference", expectedReference)))
                {
                    candidates.Add(invoice);
                }
            }

            if (candidates.Count > 1)
            {
                return Unknown(
                    "Provider lookup returned duplicate order-reference matches.",
                    requestAttempted,
                    ERacuniInvoiceLookupFailureCategory.DuplicateMatch,
                    status,
                    responseShape);
            }

            if (candidates.Count == 0)
            {
                return ERacuniInvoiceLookupResult.NotFound(Diagnostics(
                    requestAttempted,
                    ERacuniInvoiceLookupFailureCategory.None,
                    status,
                    responseShape));
            }

            var match = candidates[0];
            var matchReference = ReadString(match, "reference");
            if (!IdentifierEquals(match, "orderReference", expectedOrderReference) ||
                (expectedReference != null &&
                 !string.IsNullOrWhiteSpace(matchReference) &&
                 !IdentifierEquals(match, "reference", expectedReference)))
            {
                return Unknown(
                    "Provider lookup candidate has contradicting order reference or reference identifiers.",
                    requestAttempted,
                    ERacuniInvoiceLookupFailureCategory.IdentifierMismatch,
                    status,
                    responseShape);
            }

            if (!TryReadAmount(match.GetValue("totalAmount", StringComparison.OrdinalIgnoreCase), out var totalAmount) ||
                totalAmount != criteria.TotalAmount.Value ||
                (!string.IsNullOrWhiteSpace(criteria.Currency) &&
                 !string.Equals(
                     ReadString(match, "totalCurrency")?.Trim(),
                     criteria.Currency.Trim(),
                     StringComparison.OrdinalIgnoreCase)))
            {
                return Unknown(
                    "Provider lookup order-reference match has a different or unreadable total amount or currency.",
                    requestAttempted,
                    ERacuniInvoiceLookupFailureCategory.AmountMismatch,
                    status,
                    responseShape);
            }

            var metadata = ERacuniApiResponseMetadataReader.Read(match);
            if (string.IsNullOrWhiteSpace(metadata.DocumentId) &&
                string.IsNullOrWhiteSpace(metadata.InvoiceNumber))
            {
                return Unknown(
                    "Provider lookup match has no durable document identifier.",
                    requestAttempted,
                    ERacuniInvoiceLookupFailureCategory.MissingDurableIdentifier,
                    status,
                    responseShape);
            }

            return ERacuniInvoiceLookupResult.Found(new ERacuniApiResult
            {
                StatusCode = response.StatusCode,
                Body = match.ToString(Formatting.None),
                ParsedBody = match
            }, Diagnostics(
                requestAttempted,
                ERacuniInvoiceLookupFailureCategory.None,
                status,
                responseShape));
        }

        private ERacuniApiResult Send(ERacuniApiRequestEnvelope request) => Send(request, null, null);

        private ERacuniApiResult Send(
            ERacuniApiRequestEnvelope request,
            Action requestAttempted,
            Action<HttpStatusCode> responseReceived)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            var eracuni = _options.ERacuni ?? new ERacuniInvoiceOptions();
            ValidateLiveConfiguration(request, eracuni);

            _requestLock.Wait();
            try
            {
                ThrottleIfNeeded(eracuni.MinimumRequestIntervalMilliseconds);

                var client = _httpClientFactory.CreateClient(nameof(ERacuniApiClient));
                using var message = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint(eracuni));
                var payload = JsonConvert.SerializeObject(request, new JsonSerializerSettings
                {
                    NullValueHandling = NullValueHandling.Ignore
                });
                message.Content = new StringContent(payload, Encoding.UTF8, "application/json");

                _lastRequestStartedUtc = DateTime.UtcNow;
                requestAttempted?.Invoke();
                using var timeoutCancellation = CreateTimeoutCancellation(client.Timeout);
                var cancellationToken = timeoutCancellation?.Token ?? CancellationToken.None;

                using var response = client.SendAsync(
                    message,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).GetAwaiter().GetResult();
                responseReceived?.Invoke(response.StatusCode);
                var body = response.Content == null
                    ? null
                    : response.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();

                _logger.LogInformation(
                    "Invoice/ERacuni => HTTP {StatusCode} bodyLength={BodyLength}",
                    (int)response.StatusCode,
                    body?.Length ?? 0);

                return new ERacuniApiResult
                {
                    StatusCode = response.StatusCode,
                    Body = body,
                    ParsedBody = TryParseJson(body)
                };
            }
            finally
            {
                _requestLock.Release();
            }
        }

        private static CancellationTokenSource CreateTimeoutCancellation(TimeSpan timeout) =>
            timeout == Timeout.InfiniteTimeSpan
                ? null
                : new CancellationTokenSource(timeout);

        private static ERacuniInvoiceLookupResult Unknown(
            string error,
            bool requestAttempted,
            ERacuniInvoiceLookupFailureCategory failureCategory,
            int? httpStatusCode = null,
            ERacuniInvoiceLookupResponseShape responseShape = ERacuniInvoiceLookupResponseShape.NotAvailable) =>
            ERacuniInvoiceLookupResult.Unknown(
                error,
                Diagnostics(requestAttempted, failureCategory, httpStatusCode, responseShape));

        private static ERacuniInvoiceLookupDiagnostics Diagnostics(
            bool requestAttempted,
            ERacuniInvoiceLookupFailureCategory failureCategory,
            int? httpStatusCode,
            ERacuniInvoiceLookupResponseShape responseShape) =>
            new(requestAttempted, failureCategory, httpStatusCode, responseShape);

        private static ERacuniInvoiceLookupResponseShape ClassifyResponseShape(JToken parsedBody)
        {
            if (parsedBody == null)
            {
                return ERacuniInvoiceLookupResponseShape.NonJson;
            }

            if (parsedBody is JArray)
            {
                return ERacuniInvoiceLookupResponseShape.JsonArray;
            }

            if (parsedBody is JObject rootObject)
            {
                if (rootObject.GetValue("result", StringComparison.OrdinalIgnoreCase) is JArray)
                {
                    return ERacuniInvoiceLookupResponseShape.ResultArray;
                }

                return rootObject.GetValue("response", StringComparison.OrdinalIgnoreCase) is JObject nestedResponse &&
                       nestedResponse.GetValue("result", StringComparison.OrdinalIgnoreCase) is JArray
                    ? ERacuniInvoiceLookupResponseShape.ResponseResultArray
                    : ERacuniInvoiceLookupResponseShape.JsonObject;
            }

            return ERacuniInvoiceLookupResponseShape.OtherJson;
        }

        private static bool TryParseProviderDate(string value, out DateTime date)
        {
            date = default;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var trimmed = value.Trim();
            if (trimmed.Length > 10 && (trimmed[10] == 'T' || trimmed[10] == ' '))
            {
                trimmed = trimmed.Substring(0, 10);
            }

            return DateTime.TryParseExact(
                trimmed,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out date);
        }

        private static bool TryReadAmount(JToken token, out decimal amount)
        {
            amount = default;
            if (token == null)
            {
                return false;
            }

            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
            {
                try
                {
                    amount = token.Value<decimal>();
                    return true;
                }
                catch (OverflowException)
                {
                    return false;
                }
            }

            return token.Type == JTokenType.String &&
                   decimal.TryParse(
                       token.ToString().Trim(),
                       NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                       CultureInfo.InvariantCulture,
                       out amount);
        }

        private static bool IdentifierEquals(JObject value, string propertyName, string expected) =>
            string.Equals(
                ReadString(value, propertyName)?.Trim(),
                expected,
                StringComparison.OrdinalIgnoreCase);

        private static string ReadString(JObject value, string propertyName)
        {
            var token = value?.GetValue(propertyName, StringComparison.OrdinalIgnoreCase);
            return token == null || token.Type == JTokenType.Null ? null : token.ToString();
        }

        private static Uri BuildEndpoint(ERacuniInvoiceOptions options)
        {
            var baseUrl = string.IsNullOrWhiteSpace(options?.ApiBaseUrl)
                ? "https://eurofaktura.com"
                : options.ApiBaseUrl.Trim();
            var apiPath = string.IsNullOrWhiteSpace(options?.ApiPath)
                ? "/WebServices/API"
                : options.ApiPath.Trim();

            return new Uri(new Uri(EnsureTrailingSlash(baseUrl)), TrimLeadingSlash(apiPath));
        }

        private void ValidateLiveConfiguration(ERacuniApiRequestEnvelope request, ERacuniInvoiceOptions options)
        {
            if (string.IsNullOrWhiteSpace(request.Username) ||
                string.IsNullOrWhiteSpace(request.SecretKey) ||
                string.IsNullOrWhiteSpace(request.Token))
            {
                throw new InvalidOperationException("e-racuni credentials are missing. Configure Invoices:ERacuni:Username, SecretKey, and Token.");
            }

            if (options != null && options.MinimumRequestIntervalMilliseconds < 0)
            {
                throw new InvalidOperationException("e-racuni minimum request interval must be zero or greater.");
            }
        }

        private void ThrottleIfNeeded(int minimumIntervalMilliseconds)
        {
            if (minimumIntervalMilliseconds <= 0 || _lastRequestStartedUtc == DateTime.MinValue)
            {
                return;
            }

            var elapsed = DateTime.UtcNow - _lastRequestStartedUtc;
            var delay = minimumIntervalMilliseconds - (int)elapsed.TotalMilliseconds;
            if (delay <= 0)
            {
                return;
            }

            _logger.LogDebug("Invoice/ERacuni => throttling for {DelayMs} ms to respect provider rate limits", delay);
            Thread.Sleep(delay);
        }

        private static JToken TryParseJson(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            try
            {
                return JToken.Parse(body);
            }
            catch (JsonReaderException)
            {
                return null;
            }
        }

        // Lookup responses keep dates as strings and amounts as decimals so row matching never
        // depends on local time-zone conversion or binary floating-point rounding.
        private static JToken TryParseLookupJson(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            try
            {
                using var reader = new JsonTextReader(new StringReader(body))
                {
                    DateParseHandling = DateParseHandling.None,
                    FloatParseHandling = FloatParseHandling.Decimal
                };
                var token = JToken.ReadFrom(reader);
                while (reader.Read())
                {
                    if (reader.TokenType != JsonToken.Comment)
                    {
                        return null;
                    }
                }

                return token;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string EnsureTrailingSlash(string value)
        {
            return value.EndsWith("/", StringComparison.Ordinal) ? value : value + "/";
        }

        private static string TrimLeadingSlash(string value)
        {
            return value.TrimStart('/');
        }
    }
}
