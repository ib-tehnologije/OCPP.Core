using System;
using System.Net.Http;
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

                using var response = client.SendAsync(message).GetAwaiter().GetResult();
                var body = response.Content == null
                    ? null
                    : response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

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
