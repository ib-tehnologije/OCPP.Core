/*
 * OCPP.Core - https://github.com/dallmann-consulting/OCPP.Core
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OCPP.Core.Server.Payments.Providers.Saferpay
{
    /// <summary>
    /// Minimal synchronous client for the Saferpay JSON API (https://saferpay.github.io/jsonapi/).
    /// </summary>
    /// <remarks>
    /// Saferpay rejects chunked request bodies and asks clients not to keep connections alive, so every
    /// request is sent with a fixed Content-Length and <c>Connection: close</c>. The payment coordinator
    /// is synchronous, so the client uses <see cref="HttpClient.Send(HttpRequestMessage)"/> instead of
    /// blocking on asynchronous calls. Every failure is raised as <see cref="SaferpayApiException"/>.
    /// </remarks>
    internal sealed class SaferpayApiClient
    {
        /// <summary>Name of the <see cref="IHttpClientFactory"/> client used for Saferpay calls.</summary>
        public const string HttpClientName = "Saferpay";

        public const string InitializePath = "/Payment/v1/PaymentPage/Initialize";
        public const string AssertPath = "/Payment/v1/PaymentPage/Assert";
        public const string CapturePath = "/Payment/v1/Transaction/Capture";
        public const string CancelPath = "/Payment/v1/Transaction/Cancel";
        public const string InquirePath = "/Payment/v1/Transaction/Inquire";

        public const string DefaultSpecVersion = "1.54";

        /// <summary>Saferpay Id[1..50] limit of <c>RequestHeader.RequestId</c>.</summary>
        internal const int MaxRequestIdLength = 50;

        private const int DerivedRequestIdLength = 40;
        private const string PathPrefix = "/Payment/v1/";
        private const int MaxMessageLength = 500;

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly SaferpayOptions _options;
        private readonly ILogger _logger;

        public SaferpayApiClient(IHttpClientFactory httpClientFactory, SaferpayOptions options, ILogger logger)
        {
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? NullLogger.Instance;
        }

        /// <summary>
        /// Builds a Saferpay <c>RequestId</c> (Id format, max 50 characters). The id is derived from
        /// <paramref name="idempotencyKey"/> when one is supplied, so a repeated operation is recognizable
        /// in Saferpay's logs; otherwise a fresh random id is used.
        /// </summary>
        internal static string BuildRequestId(string idempotencyKey)
        {
            if (string.IsNullOrWhiteSpace(idempotencyKey))
            {
                return Guid.NewGuid().ToString("N");
            }

            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey));
            return Convert.ToHexString(hash).ToLowerInvariant().Substring(0, DerivedRequestIdLength);
        }

        /// <summary>
        /// Posts <paramref name="payload"/> (without request header) to <paramref name="path"/> and returns the
        /// parsed response object. Throws <see cref="SaferpayApiException"/> for transport, HTTP and API errors.
        /// </summary>
        public JObject Post(string path, JObject payload, string idempotencyKey)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Saferpay request path is required.", nameof(path));
            if (payload == null) throw new ArgumentNullException(nameof(payload));

            string operation = OperationName(path);
            string requestId = BuildRequestId(idempotencyKey);

            Uri endpoint = BuildEndpoint(operation, path);
            var body = new JObject
            {
                ["RequestHeader"] = new JObject
                {
                    ["SpecVersion"] = string.IsNullOrWhiteSpace(_options.SpecVersion) ? DefaultSpecVersion : _options.SpecVersion.Trim(),
                    ["CustomerId"] = _options.CustomerId?.Trim() ?? string.Empty,
                    ["RequestId"] = requestId,
                    ["RetryIndicator"] = 0
                }
            };
            foreach (var property in payload.Properties())
            {
                if (!string.Equals(property.Name, "RequestHeader", StringComparison.Ordinal))
                {
                    body[property.Name] = property.Value.DeepClone();
                }
            }

            byte[] bytes = Encoding.UTF8.GetBytes(body.ToString(Formatting.None));
            using var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
            message.Headers.ConnectionClose = true;
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var authorization = BuildAuthorizationHeader();
            if (authorization != null)
            {
                message.Headers.Authorization = authorization;
            }

            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            content.Headers.ContentLength = bytes.Length;
            message.Content = content;

            HttpResponseMessage response;
            try
            {
                var client = _httpClientFactory.CreateClient(HttpClientName);
                response = client.Send(message);
            }
            catch (OperationCanceledException exception)
            {
                // No caller token is passed, so a cancellation is the HttpClient timeout.
                throw LogFailure(new SaferpayApiException(
                    operation,
                    "timeout",
                    $"Saferpay {operation} timed out.",
                    isTransient: true,
                    innerException: exception), requestId);
            }
            catch (HttpRequestException exception)
            {
                throw LogFailure(new SaferpayApiException(
                    operation,
                    "network_error",
                    $"Saferpay {operation} could not reach Saferpay: {Clip(Sanitize(exception.Message))}",
                    isTransient: true,
                    innerException: exception), requestId);
            }
            catch (IOException exception)
            {
                throw LogFailure(new SaferpayApiException(
                    operation,
                    "network_error",
                    $"Saferpay {operation} connection failed: {Clip(Sanitize(exception.Message))}",
                    isTransient: true,
                    innerException: exception), requestId);
            }
            catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
            {
                throw LogFailure(new SaferpayApiException(
                    operation,
                    "client_error",
                    $"Saferpay {operation} could not be sent ({exception.GetType().Name}).",
                    isTransient: false,
                    innerException: exception), requestId);
            }

            using (response)
            {
                int statusCode = (int)response.StatusCode;
                string responseBody;
                try
                {
                    responseBody = ReadBody(response);
                }
                catch (Exception exception) when (exception is IOException or HttpRequestException or OperationCanceledException)
                {
                    throw LogFailure(new SaferpayApiException(
                        operation,
                        "network_error",
                        $"Saferpay {operation} response could not be read.",
                        isTransient: true,
                        httpStatusCode: statusCode,
                        innerException: exception), requestId);
                }

                _logger.LogInformation(
                    "Saferpay/{Operation} => HTTP {StatusCode} requestId={RequestId}",
                    operation,
                    statusCode,
                    requestId);

                if (statusCode >= 200 && statusCode <= 299)
                {
                    var parsed = TryParseObject(responseBody);
                    if (parsed == null)
                    {
                        throw LogFailure(new SaferpayApiException(
                            operation,
                            "invalid_response",
                            $"Saferpay {operation} returned HTTP {statusCode} without a JSON object.",
                            isTransient: true,
                            httpStatusCode: statusCode), requestId);
                    }

                    return parsed;
                }

                throw LogFailure(CreateHttpError(operation, statusCode, response, responseBody), requestId);
            }
        }

        internal static JObject TryParseObject(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            try
            {
                using var reader = new JsonTextReader(new StringReader(body))
                {
                    // Keep Saferpay ISO 8601 dates as strings so their offset is not lost.
                    DateParseHandling = DateParseHandling.None,
                    FloatParseHandling = FloatParseHandling.Decimal
                };
                return JToken.ReadFrom(reader) as JObject;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        internal static string ReadString(JToken token, params string[] path)
        {
            var current = token;
            foreach (var segment in path)
            {
                if (current is not JObject obj)
                {
                    return null;
                }

                current = obj.GetValue(segment, StringComparison.OrdinalIgnoreCase);
                if (current == null)
                {
                    return null;
                }
            }

            if (current == null || current.Type == JTokenType.Null || current.Type == JTokenType.Undefined ||
                current is JObject || current is JArray)
            {
                return null;
            }

            string value = current.Type == JTokenType.String
                ? (string)current
                : Convert.ToString(((JValue)current).Value, CultureInfo.InvariantCulture);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private SaferpayApiException CreateHttpError(string operation, int statusCode, HttpResponseMessage response, string body)
        {
            var error = IsJsonContent(response) ? TryParseObject(body) : null;
            string errorName = ReadString(error, "ErrorName");
            if (error == null || errorName == null)
            {
                // Web server or firewall error without a Saferpay error message.
                return new SaferpayApiException(
                    operation,
                    $"http_{statusCode}",
                    $"Saferpay {operation} failed with HTTP {statusCode}.",
                    SaferpayErrorClassification.IsTransientHttpStatus(statusCode),
                    httpStatusCode: statusCode);
            }

            string rawBehavior = ReadString(error, "Behavior");
            string behavior = SaferpayErrorClassification.NormalizeBehavior(rawBehavior);
            string errorMessage = ReadString(error, "ErrorMessage");
            string payerMessage = ReadString(error, "PayerMessage");
            string processorMessage = ReadString(error, "ProcessorMessage");
            var details = new List<string>();
            if (error.GetValue("ErrorDetail", StringComparison.OrdinalIgnoreCase) is JArray detailArray)
            {
                foreach (var detail in detailArray)
                {
                    if (detail.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string)detail))
                    {
                        details.Add(((string)detail).Trim());
                    }
                }
            }

            var message = new StringBuilder();
            message.Append($"Saferpay {operation} failed with HTTP {statusCode} {errorName}");
            if (errorMessage != null)
            {
                message.Append(": ").Append(Sanitize(errorMessage));
            }

            if (processorMessage != null)
            {
                message.Append(" (processor: ").Append(Sanitize(processorMessage)).Append(')');
            }

            bool isTransient = SaferpayErrorClassification.IsTransient(behavior, errorName, statusCode);
            return new SaferpayApiException(
                operation,
                errorName,
                Clip(message.ToString()),
                isTransient,
                httpStatusCode: statusCode,
                providerMessage: Clip(Sanitize(payerMessage ?? errorMessage ?? errorName)))
            {
                Behavior = behavior,
                RawBehavior = rawBehavior,
                ErrorName = errorName,
                ErrorMessage = errorMessage,
                ErrorDetail = details,
                TransactionId = ReadString(error, "TransactionId"),
                OrderId = ReadString(error, "OrderId"),
                PayerMessage = payerMessage,
                ProcessorName = ReadString(error, "ProcessorName"),
                ProcessorResult = ReadString(error, "ProcessorResult"),
                ProcessorMessage = processorMessage
            };
        }

        private SaferpayApiException LogFailure(SaferpayApiException exception, string requestId)
        {
            _logger.LogWarning(
                "Saferpay/{Operation} => failed code={Code} transient={Transient} httpStatus={StatusCode} requestId={RequestId}",
                exception.Operation,
                exception.Code,
                exception.IsTransient,
                exception.HttpStatusCode,
                requestId);
            return exception;
        }

        private Uri BuildEndpoint(string operation, string path)
        {
            string baseUrl = string.IsNullOrWhiteSpace(_options.BaseUrl) ? SaferpayOptions.TestBaseUrl : _options.BaseUrl.Trim();
            if (!Uri.TryCreate(baseUrl.TrimEnd('/') + path, UriKind.Absolute, out var endpoint) ||
                (endpoint.Scheme != Uri.UriSchemeHttps && endpoint.Scheme != Uri.UriSchemeHttp))
            {
                throw new SaferpayApiException(
                    operation,
                    "invalid_configuration",
                    "Saferpay:BaseUrl is not a valid absolute http(s) URL.",
                    isTransient: false);
            }

            // Every request carries the API password as basic authentication, so it is never sent over plain http.
            // Only the in-process mock (which never leaves the process) may use an http base URL.
            if (endpoint.Scheme != Uri.UriSchemeHttps && !_options.UseMockServices)
            {
                throw new SaferpayApiException(
                    operation,
                    "invalid_configuration",
                    "Saferpay:BaseUrl must be an absolute https URL.",
                    isTransient: false);
            }

            return endpoint;
        }

        /// <summary>True when <paramref name="value"/> is an absolute https URL.</summary>
        internal static bool IsAbsoluteHttpsUrl(string value) =>
            !string.IsNullOrWhiteSpace(value) &&
            Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps;

        private AuthenticationHeaderValue BuildAuthorizationHeader()
        {
            if (string.IsNullOrEmpty(_options.ApiUsername) && string.IsNullOrEmpty(_options.ApiPassword))
            {
                return null;
            }

            string credentials = $"{_options.ApiUsername}:{_options.ApiPassword}";
            return new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials)));
        }

        private static string ReadBody(HttpResponseMessage response)
        {
            if (response.Content == null)
            {
                return null;
            }

            using var stream = response.Content.ReadAsStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        private static bool IsJsonContent(HttpResponseMessage response)
        {
            string mediaType = response.Content?.Headers?.ContentType?.MediaType;
            return mediaType != null && mediaType.Contains("json", StringComparison.OrdinalIgnoreCase);
        }

        private static string OperationName(string path) =>
            path.StartsWith(PathPrefix, StringComparison.OrdinalIgnoreCase)
                ? path.Substring(PathPrefix.Length)
                : path.Trim('/');

        private string Sanitize(string text) => SaferpayTextSanitizer.Sanitize(text, _options);

        private static string Clip(string text) =>
            text == null || text.Length <= MaxMessageLength ? text : text.Substring(0, MaxMessageLength);
    }

    /// <summary>Saferpay error classification shared by the API client and the gateway.</summary>
    internal static class SaferpayErrorClassification
    {
        public const string BehaviorDoNotRetry = "DO_NOT_RETRY";
        public const string BehaviorRetry = "RETRY";
        public const string BehaviorRetryLater = "RETRY_LATER";
        public const string BehaviorOtherMeans = "OTHER_MEANS";

        /// <summary>Error names that indicate a temporary Saferpay, processor or acquirer problem.</summary>
        public static readonly IReadOnlySet<string> TransientErrorNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "COMMUNICATION_FAILED",
            "COMMUNICATION_TIMEOUT",
            "INTERNAL_ERROR",
            "UNEXPECTED_ERROR_BY_ACQUIRER"
        };

        /// <summary>
        /// Error names caused by our own credentials, configuration or request (never by the payer).
        /// They are raised as exceptions, not mapped to a payment outcome.
        /// </summary>
        public static readonly IReadOnlySet<string> MerchantErrorNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AUTHENTICATION_FAILED",
            "PERMISSION_DENIED",
            "VALIDATION_FAILED",
            "ACTION_NOT_SUPPORTED",
            "TRANSACTION_NOT_FOUND",
            "TRANSACTION_ALREADY_CAPTURED"
        };

        /// <summary>Error names that describe a final, payer-side outcome of the payment attempt (declines, failed authentication).</summary>
        public static readonly IReadOnlySet<string> PaymentDeclineErrorNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "TRANSACTION_DECLINED",
            "3DS_AUTHENTICATION_FAILED",
            "CONDITION_NOT_SATISFIED",
            "BLOCKED_BY_RISK_MANAGEMENT",
            "GENERAL_DECLINED",
            "NO_CREDITS_AVAILABLE",
            "NO_CONTRACT",
            "PAYER_AUTHENTICATION_REQUIRED",
            "UPDATE_CARD_INFORMATION",
            "ALIAS_INVALID",
            "AMOUNT_INVALID",
            "CURRENCY_INVALID"
        };

        private static readonly string[] PaymentDeclineErrorPrefixes = { "CARD_", "PAYMENTMEANS_" };

        /// <summary>
        /// Maps a Saferpay Behavior to its current name. The pre-1.33 value ABORT and unknown values are
        /// treated as DO_NOT_RETRY, as the specification recommends.
        /// </summary>
        public static string NormalizeBehavior(string behavior)
        {
            string normalized = behavior?.Trim().ToUpperInvariant();
            return normalized switch
            {
                BehaviorRetry => BehaviorRetry,
                BehaviorRetryLater => BehaviorRetryLater,
                BehaviorOtherMeans => BehaviorOtherMeans,
                _ => BehaviorDoNotRetry
            };
        }

        public static bool IsTransientHttpStatus(int statusCode) =>
            statusCode == 408 || statusCode == 429 || statusCode >= 500;

        public static bool IsTransient(string normalizedBehavior, string errorName, int? statusCode)
        {
            if (normalizedBehavior == BehaviorRetry || normalizedBehavior == BehaviorRetryLater)
            {
                return true;
            }

            if (errorName != null && TransientErrorNames.Contains(errorName.Trim()))
            {
                return true;
            }

            return statusCode.HasValue && IsTransientHttpStatus(statusCode.Value);
        }

        /// <summary>
        /// True when an error returned for a payment attempt (PaymentPage Assert) is a final payer-side
        /// outcome. Merchant, configuration and temporary errors return false and must be raised.
        /// </summary>
        public static bool IsPaymentDecline(SaferpayApiException error)
        {
            string errorName = error?.ErrorName?.Trim();
            if (string.IsNullOrEmpty(errorName))
            {
                return false;
            }

            if (MerchantErrorNames.Contains(errorName) || TransientErrorNames.Contains(errorName))
            {
                return false;
            }

            if (PaymentDeclineErrorNames.Contains(errorName))
            {
                return true;
            }

            foreach (var prefix in PaymentDeclineErrorPrefixes)
            {
                if (errorName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            // An unknown error name on HTTP 402 ("requested action failed") is a final outcome of this payment
            // attempt unless Saferpay asks for an immediate retry of the request.
            return error.HttpStatusCode == 402 && error.Behavior != BehaviorRetry;
        }
    }

    /// <summary>A Saferpay JSON API call failed. Carries the parsed Saferpay error message when one was returned.</summary>
    internal sealed class SaferpayApiException : PaymentProviderException
    {
        public SaferpayApiException(
            string operation,
            string code,
            string message,
            bool isTransient,
            int? httpStatusCode = null,
            string providerMessage = null,
            Exception innerException = null)
            : base(PaymentProviderNames.Saferpay, code, message, isTransient, httpStatusCode, providerMessage, innerException)
        {
            Operation = operation;
        }

        /// <summary>API operation, e.g. <c>PaymentPage/Assert</c>.</summary>
        public string Operation { get; }

        /// <summary>Normalized Behavior (DO_NOT_RETRY, RETRY, RETRY_LATER, OTHER_MEANS); null without a Saferpay error message.</summary>
        public string Behavior { get; init; }

        public string RawBehavior { get; init; }

        /// <summary>Saferpay ErrorName; null when no Saferpay error message was returned (timeouts, non-JSON errors).</summary>
        public string ErrorName { get; init; }

        /// <summary>Raw Saferpay ErrorMessage. Not sanitized; use <see cref="Exception.Message"/> for logs.</summary>
        public string ErrorMessage { get; init; }

        public IReadOnlyList<string> ErrorDetail { get; init; } = Array.Empty<string>();
        public string TransactionId { get; init; }
        public string OrderId { get; init; }

        /// <summary>Issuer text that Saferpay marks as safe to show to the payer.</summary>
        public string PayerMessage { get; init; }

        public string ProcessorName { get; init; }
        public string ProcessorResult { get; init; }
        public string ProcessorMessage { get; init; }

        public bool HasErrorName(params string[] errorNames)
        {
            if (string.IsNullOrWhiteSpace(ErrorName) || errorNames == null)
            {
                return false;
            }

            foreach (var errorName in errorNames)
            {
                if (string.Equals(ErrorName.Trim(), errorName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
