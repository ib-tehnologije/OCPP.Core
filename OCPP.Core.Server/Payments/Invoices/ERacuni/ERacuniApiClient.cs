using System;
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
        ERacuniInvoiceLookupResult LookupSalesInvoiceByApiTransactionId(ERacuniApiRequestEnvelope request) =>
            ERacuniInvoiceLookupResult.Unknown("Provider lookup is not implemented.");
    }

    public class ERacuniApiClient : IERacuniApiClient
    {
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

        public ERacuniInvoiceLookupResult LookupSalesInvoiceByApiTransactionId(ERacuniApiRequestEnvelope request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.Parameters is not ERacuniSalesInvoiceLookupParameters parameters ||
                string.IsNullOrWhiteSpace(parameters.ApiTransactionId))
            {
                return Unknown(
                    "Exact provider transaction reference is missing.",
                    requestAttempted: false,
                    ERacuniInvoiceLookupFailureCategory.MissingReference);
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
            var responseShape = ClassifyResponseShape(response);
            if (status < 200 || status > 299)
            {
                return Unknown(
                    "Provider lookup returned a non-success HTTP response.",
                    requestAttempted,
                    ERacuniInvoiceLookupFailureCategory.HttpStatus,
                    status,
                    responseShape);
            }

            if (response.ParsedBody == null)
            {
                return Unknown(
                    "Provider lookup returned a non-JSON response.",
                    requestAttempted,
                    ERacuniInvoiceLookupFailureCategory.NonJsonResponse,
                    status,
                    responseShape);
            }

            var exactMatches = response.ParsedBody
                .SelectTokens("$..apiTransactionId")
                .Select(token => token.Parent?.Parent)
                .OfType<JObject>()
                .Where(candidate => string.Equals(
                    candidate.GetValue("apiTransactionId", StringComparison.OrdinalIgnoreCase)?.ToString(),
                    parameters.ApiTransactionId,
                    StringComparison.Ordinal))
                .ToList();

            if (exactMatches.Count > 1)
            {
                return Unknown(
                    "Provider lookup returned duplicate exact matches.",
                    requestAttempted,
                    ERacuniInvoiceLookupFailureCategory.DuplicateMatch,
                    status,
                    responseShape);
            }

            if (exactMatches.Count == 1)
            {
                var match = exactMatches[0];
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

            var isRecognizedEmptyResult = response.ParsedBody is JArray rootArray && rootArray.Count == 0;
            if (response.ParsedBody is JObject rootObject &&
                rootObject.GetValue("result", StringComparison.OrdinalIgnoreCase) is JArray resultArray)
            {
                isRecognizedEmptyResult = resultArray.Count == 0;
            }

            return isRecognizedEmptyResult
                ? ERacuniInvoiceLookupResult.NotFound(Diagnostics(
                    requestAttempted,
                    ERacuniInvoiceLookupFailureCategory.None,
                    status,
                    responseShape))
                : Unknown(
                    "Provider lookup returned a non-empty or unrecognized response without one exact match.",
                    requestAttempted,
                    ERacuniInvoiceLookupFailureCategory.UnrecognizedResponse,
                    status,
                    responseShape);
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

        private static ERacuniInvoiceLookupResponseShape ClassifyResponseShape(ERacuniApiResult response)
        {
            if (response?.ParsedBody == null)
            {
                return ERacuniInvoiceLookupResponseShape.NonJson;
            }

            if (response.ParsedBody is JArray)
            {
                return ERacuniInvoiceLookupResponseShape.JsonArray;
            }

            if (response.ParsedBody is JObject rootObject)
            {
                return rootObject.GetValue("result", StringComparison.OrdinalIgnoreCase) is JArray
                    ? ERacuniInvoiceLookupResponseShape.ResultArray
                    : ERacuniInvoiceLookupResponseShape.JsonObject;
            }

            return ERacuniInvoiceLookupResponseShape.OtherJson;
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
