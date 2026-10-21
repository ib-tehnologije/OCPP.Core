using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using OCPP.Core.Server.Payments.Providers.Saferpay;

namespace OCPP.Core.Server.Tests
{
    /// <summary>Shared fixtures for the Saferpay gateway, API client, signer and mock tests.</summary>
    internal static class SaferpayTestData
    {
        public const string BaseUrl = "https://saferpay.example.com/api";
        public const string CustomerId = "123456";
        public const string TerminalId = "17654321";
        public const string ApiUsername = "API_123456_87654321";
        public const string ApiPassword = "TestOnlyApiPassword0123456789";
        public const string NotifyBaseUrl = "https://ocpp.example.com/API";
        public const string ReturnUrl = "https://portal.example.com/Payments/Success?reservationId=00000000-0000-0000-0000-000000000001";
        public const string TransactionId = "723n4MAjMdhjSAhAKEUdA8jtl9jb";
        public const string Token = "234uhfh78234hlasdfh8234e1234";

        public static SaferpayOptions Options(Action<SaferpayOptions>? configure = null)
        {
            var options = new SaferpayOptions
            {
                Enabled = true,
                BaseUrl = BaseUrl,
                CustomerId = CustomerId,
                TerminalId = TerminalId,
                ApiUsername = ApiUsername,
                ApiPassword = ApiPassword,
                NotifyBaseUrl = NotifyBaseUrl,
                TimeoutSeconds = 100
            };
            configure?.Invoke(options);
            return options;
        }

        public static SaferpayPaymentGateway Gateway(
            SaferpayRecordingHandler handler,
            SaferpayOptions? options = null,
            TimeSpan? timeout = null,
            ILogger<SaferpayPaymentGateway>? logger = null) =>
            Gateway((HttpMessageHandler)handler, options, timeout, logger);

        public static SaferpayPaymentGateway Gateway(
            HttpMessageHandler handler,
            SaferpayOptions? options = null,
            TimeSpan? timeout = null,
            ILogger<SaferpayPaymentGateway>? logger = null) =>
            new SaferpayPaymentGateway(
                new SaferpayStubHttpClientFactory(handler, timeout),
                Microsoft.Extensions.Options.Options.Create(options ?? Options()),
                logger ?? NullLogger<SaferpayPaymentGateway>.Instance);

        public static SaferpayApiClient Client(HttpMessageHandler handler, SaferpayOptions? options = null, TimeSpan? timeout = null) =>
            new SaferpayApiClient(new SaferpayStubHttpClientFactory(handler, timeout), options ?? Options(), NullLogger.Instance);

        public static HttpResponseMessage Json(HttpStatusCode statusCode, JObject body) =>
            Json(statusCode, body.ToString(Newtonsoft.Json.Formatting.None));

        public static HttpResponseMessage Json(HttpStatusCode statusCode, string body) =>
            new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };

        public static HttpResponseMessage Text(HttpStatusCode statusCode, string body, string mediaType = "text/html") =>
            new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, mediaType)
            };

        public static string ReadBody(HttpResponseMessage response)
        {
            using var stream = response.Content.ReadAsStream();
            using var reader = new System.IO.StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        public static JObject ResponseHeader() =>
            new JObject { ["SpecVersion"] = "1.54", ["RequestId"] = "test" };

        public static HttpResponseMessage Error(
            HttpStatusCode statusCode,
            string errorName,
            string behavior = "DO_NOT_RETRY",
            string errorMessage = "Request failed",
            string? payerMessage = null,
            string? orderId = null,
            string? transactionId = null)
        {
            var body = new JObject
            {
                ["ResponseHeader"] = ResponseHeader(),
                ["Behavior"] = behavior,
                ["ErrorName"] = errorName,
                ["ErrorMessage"] = errorMessage
            };
            if (payerMessage != null) body["PayerMessage"] = payerMessage;
            if (orderId != null) body["OrderId"] = orderId;
            if (transactionId != null) body["TransactionId"] = transactionId;
            return Json(statusCode, body);
        }

        public static JObject Transaction(
            string status,
            long amount = 2500,
            string currency = "EUR",
            string? orderId = null,
            string id = TransactionId,
            string? captureId = null)
        {
            var transaction = new JObject
            {
                ["Type"] = "PAYMENT",
                ["Status"] = status,
                ["Id"] = id,
                ["Date"] = "2026-10-20T12:45:22.258+02:00",
                ["Amount"] = new JObject { ["Value"] = amount.ToString(System.Globalization.CultureInfo.InvariantCulture), ["CurrencyCode"] = currency },
                ["SixTransactionReference"] = "0:0:3:" + id
            };
            if (orderId != null) transaction["OrderId"] = orderId;
            if (captureId != null) transaction["CaptureId"] = captureId;
            return transaction;
        }

        public static HttpResponseMessage TransactionResponse(
            string status,
            long amount = 2500,
            string? orderId = null,
            string? captureId = null,
            JObject? payer = null,
            string holderName = "Card Holder",
            string currency = "EUR")
        {
            var body = new JObject
            {
                ["ResponseHeader"] = ResponseHeader(),
                ["Transaction"] = Transaction(status, amount, currency, orderId: orderId, captureId: captureId),
                ["PaymentMeans"] = new JObject
                {
                    ["Brand"] = new JObject { ["PaymentMethod"] = "VISA", ["Name"] = "VISA Saferpay Test" },
                    ["DisplayText"] = "xxxx xxxx xxxx 1234",
                    ["Card"] = new JObject
                    {
                        ["MaskedNumber"] = "912345xxxxxx1234",
                        ["ExpYear"] = 2029,
                        ["ExpMonth"] = 9,
                        ["HolderName"] = holderName
                    }
                }
            };
            if (payer != null) body["Payer"] = payer;
            return Json(HttpStatusCode.OK, body);
        }
    }

    internal sealed class SaferpayRecordedRequest
    {
        public HttpMethod Method { get; init; } = HttpMethod.Get;
        public Uri? Uri { get; init; }
        public string Body { get; init; } = string.Empty;
        public JObject? Json { get; init; }
        public string? ContentType { get; init; }
        public string? CharSet { get; init; }
        public long? ContentLength { get; init; }
        public bool? TransferEncodingChunked { get; init; }
        public bool? ConnectionClose { get; init; }
        public string? AuthorizationScheme { get; init; }
        public string? AuthorizationParameter { get; init; }
        public IReadOnlyList<string> Accept { get; init; } = Array.Empty<string>();

        public string Path => Uri?.AbsolutePath ?? string.Empty;
    }

    /// <summary>Records each request (including headers) and answers from a queue or a responder.</summary>
    internal sealed class SaferpayRecordingHandler : HttpMessageHandler
    {
        private readonly Queue<Func<SaferpayRecordedRequest, HttpResponseMessage>> _responses = new();

        public List<SaferpayRecordedRequest> Requests { get; } = new();
        public Func<SaferpayRecordedRequest, HttpResponseMessage>? Responder { get; set; }
        public Exception? ExceptionToThrow { get; set; }
        public bool AsyncSendUsed { get; private set; }

        public SaferpayRecordedRequest LastRequest => Requests.Last();

        public SaferpayRecordingHandler Enqueue(HttpResponseMessage response)
        {
            _responses.Enqueue(_ => response);
            return this;
        }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Handle(request);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            AsyncSendUsed = true;
            return Task.FromResult(Handle(request));
        }

        private HttpResponseMessage Handle(HttpRequestMessage request)
        {
            string body = request.Content == null
                ? string.Empty
                : request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var recorded = new SaferpayRecordedRequest
            {
                Method = request.Method,
                Uri = request.RequestUri,
                Body = body,
                Json = string.IsNullOrEmpty(body) ? null : JObject.Parse(body),
                ContentType = request.Content?.Headers.ContentType?.MediaType,
                CharSet = request.Content?.Headers.ContentType?.CharSet,
                ContentLength = request.Content?.Headers.ContentLength,
                TransferEncodingChunked = request.Headers.TransferEncodingChunked,
                ConnectionClose = request.Headers.ConnectionClose,
                AuthorizationScheme = request.Headers.Authorization?.Scheme,
                AuthorizationParameter = request.Headers.Authorization?.Parameter,
                Accept = request.Headers.Accept.Select(value => value.MediaType ?? string.Empty).ToList()
            };
            Requests.Add(recorded);

            if (ExceptionToThrow != null)
            {
                throw ExceptionToThrow;
            }

            if (_responses.Count > 0)
            {
                return _responses.Dequeue()(recorded);
            }

            if (Responder != null)
            {
                return Responder(recorded);
            }

            throw new InvalidOperationException($"No response configured for {recorded.Path}.");
        }
    }

    /// <summary>Waits until the HttpClient timeout cancels the request.</summary>
    internal sealed class SaferpayHangingHandler : HttpMessageHandler
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The request was not canceled.");
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));
    }

    internal sealed class SaferpayStubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        private readonly TimeSpan? _timeout;

        public SaferpayStubHttpClientFactory(HttpMessageHandler handler, TimeSpan? timeout = null)
        {
            _handler = handler;
            _timeout = timeout;
        }

        public List<string> RequestedNames { get; } = new();

        public HttpClient CreateClient(string name)
        {
            RequestedNames.Add(name);
            var client = new HttpClient(_handler, disposeHandler: false);
            if (_timeout.HasValue)
            {
                client.Timeout = _timeout.Value;
            }

            return client;
        }
    }
}
