/*
 * OCPP.Core - https://github.com/dallmann-consulting/OCPP.Core
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OCPP.Core.Server.Payments.Providers.Saferpay
{
    /// <summary>
    /// In-process fake of the Saferpay JSON API (PaymentPage Initialize/Assert, Transaction Capture/Cancel/Inquire)
    /// used when <see cref="SaferpayOptions.UseMockServices"/> is true, for local simulator and browser runs.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Initialize returns a <c>mock_tok_*</c> token whose RedirectUrl points at the management app's
    /// <c>/Payments/MockCheckout</c> page with <c>provider=Saferpay</c>.</item>
    /// <item>There is no payer step: the first Assert of a known token creates an AUTHORIZED <c>mock_tx_*</c>
    /// transaction for the initialized amount; later Asserts return the same transaction. The opt-in
    /// <see cref="SaferpayOptions.MockAssertOutcome"/> makes Assert create a CAPTURED transaction instead, or report
    /// TRANSACTION_DECLINED, TRANSACTION_ABORTED or TRANSACTION_IN_WRONG_STATE without creating one.</item>
    /// <item>Inquire finds transactions by TransactionId or OrderId at once, unless the opt-in
    /// <see cref="SaferpayOptions.MockInquireNotFoundSeconds"/> delays them like Saferpay's Inquire update lag.</item>
    /// <item>Capture (once, up to the authorized amount) and Cancel change the transaction, and Assert/Inquire
    /// reflect it. Errors use Saferpay-shaped bodies (TOKEN_INVALID, TRANSACTION_ALREADY_CAPTURED, AMOUNT_INVALID, ...).</item>
    /// <item>Saferpay has a single ReturnUrl. The mock checkout page also needs a cancel link, so the mock derives
    /// one from the ReturnUrl by replacing <c>/Payments/Success</c> with <c>/Payments/Cancel</c> and dropping any
    /// <c>session_id</c> parameter. This heuristic exists only in the mock.</item>
    /// </list>
    /// The handler is registered as a singleton so its state survives handler rotation in
    /// <see cref="IHttpClientFactory"/>; disposing it does not clear that state.
    /// </remarks>
    internal sealed class MockSaferpayApiHandler : HttpMessageHandler
    {
        internal const string DefaultPayerEmail = "driver@example.test";
        internal const string FallbackManagementBaseUrl = "http://127.0.0.1:8082";

        private readonly object _sync = new object();
        private readonly Dictionary<string, MockPaymentPage> _pages = new(StringComparer.Ordinal);
        private readonly Dictionary<string, MockTransaction> _transactions = new(StringComparer.Ordinal);
        private readonly SaferpayOptions _options;
        private readonly Func<DateTime> _utcNow;

        public MockSaferpayApiHandler(IOptions<SaferpayOptions> options)
            : this(options?.Value, null)
        {
        }

        internal MockSaferpayApiHandler(SaferpayOptions options, Func<DateTime> utcNow)
        {
            _options = options ?? new SaferpayOptions();
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        /// <summary>Number of HTTP requests served (for tests and diagnostics).</summary>
        internal int RequestCount { get; private set; }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Handle(request);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Handle(request));
        }

        protected override void Dispose(bool disposing)
        {
            // IHttpClientFactory disposes expired handler chains; keep the singleton usable and its state intact.
        }

        private HttpResponseMessage Handle(HttpRequestMessage request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            JObject body = SaferpayApiClient.TryParseObject(ReadBody(request));
            string requestId = SaferpayApiClient.ReadString(body, "RequestHeader", "RequestId");
            string specVersion = SaferpayApiClient.ReadString(body, "RequestHeader", "SpecVersion") ?? SaferpayApiClient.DefaultSpecVersion;

            lock (_sync)
            {
                RequestCount++;

                if (request.Method != HttpMethod.Post || body == null)
                {
                    return Error(HttpStatusCode.BadRequest, specVersion, requestId, "VALIDATION_FAILED", "Request validation failed", "A JSON POST body is required.");
                }

                string path = request.RequestUri?.AbsolutePath ?? string.Empty;
                if (path.EndsWith(SaferpayApiClient.InitializePath, StringComparison.OrdinalIgnoreCase))
                {
                    return Initialize(body, specVersion, requestId);
                }

                if (path.EndsWith(SaferpayApiClient.AssertPath, StringComparison.OrdinalIgnoreCase))
                {
                    return Assert(body, specVersion, requestId);
                }

                if (path.EndsWith(SaferpayApiClient.CapturePath, StringComparison.OrdinalIgnoreCase))
                {
                    return Capture(body, specVersion, requestId);
                }

                if (path.EndsWith(SaferpayApiClient.CancelPath, StringComparison.OrdinalIgnoreCase))
                {
                    return Cancel(body, specVersion, requestId);
                }

                if (path.EndsWith(SaferpayApiClient.InquirePath, StringComparison.OrdinalIgnoreCase))
                {
                    return Inquire(body, specVersion, requestId);
                }

                return Error(HttpStatusCode.BadRequest, specVersion, requestId, "ACTION_NOT_SUPPORTED", "The requested action is not supported by the Saferpay mock");
            }
        }

        private HttpResponseMessage Initialize(JObject body, string specVersion, string requestId)
        {
            string amountValue = SaferpayApiClient.ReadString(body, "Payment", "Amount", "Value");
            string currency = SaferpayApiClient.ReadString(body, "Payment", "Amount", "CurrencyCode");
            string description = SaferpayApiClient.ReadString(body, "Payment", "Description");
            string returnUrl = SaferpayApiClient.ReadString(body, "ReturnUrl", "Url");

            if (!TryParsePositiveAmount(amountValue, out long amount))
            {
                return Error(HttpStatusCode.BadRequest, specVersion, requestId, "VALIDATION_FAILED", "Request validation failed", "Payment.Amount.Value: a positive integer is required.");
            }

            if (currency == null || currency.Length != 3)
            {
                return Error(HttpStatusCode.BadRequest, specVersion, requestId, "VALIDATION_FAILED", "Request validation failed", "Payment.Amount.CurrencyCode: a 3-letter currency code is required.");
            }

            if (description == null || returnUrl == null)
            {
                return Error(HttpStatusCode.BadRequest, specVersion, requestId, "VALIDATION_FAILED", "Request validation failed", "Payment.Description and ReturnUrl.Url are required.");
            }

            string orderId = SaferpayApiClient.ReadString(body, "Payment", "OrderId") ?? string.Empty;
            var page = new MockPaymentPage
            {
                Token = $"mock_tok_{Guid.NewGuid():N}",
                OrderId = orderId,
                AmountMinor = amount,
                Currency = currency.ToUpperInvariant(),
                ReturnUrl = returnUrl,
                ExpiresAtUtc = _utcNow().AddHours(1)
            };
            _pages[page.Token] = page;

            string cancelUrl = DeriveMockCancelUrl(returnUrl);
            string redirectUrl =
                $"{ResolveManagementBaseUrl(returnUrl)}/Payments/MockCheckout" +
                $"?reservationId={Uri.EscapeDataString(orderId)}" +
                $"&successUrl={Uri.EscapeDataString(returnUrl)}" +
                $"&cancelUrl={Uri.EscapeDataString(cancelUrl)}" +
                $"&provider={PaymentProviderNames.Saferpay}";

            return Ok(specVersion, requestId, new JObject
            {
                ["Token"] = page.Token,
                ["Expiration"] = FormatDate(page.ExpiresAtUtc),
                ["RedirectUrl"] = redirectUrl
            });
        }

        private HttpResponseMessage Assert(JObject body, string specVersion, string requestId)
        {
            string token = SaferpayApiClient.ReadString(body, "Token");
            if (token == null)
            {
                return Error(HttpStatusCode.BadRequest, specVersion, requestId, "VALIDATION_FAILED", "Request validation failed", "Token is required.");
            }

            if (!_pages.TryGetValue(token, out var page))
            {
                return Error(HttpStatusCode.BadRequest, specVersion, requestId, "TOKEN_INVALID", "Invalid token");
            }

            MockTransaction transaction = null;
            if (page.TransactionId != null)
            {
                _transactions.TryGetValue(page.TransactionId, out transaction);
            }

            if (transaction == null)
            {
                string orderId = string.IsNullOrEmpty(page.OrderId) ? null : page.OrderId;
                if (_utcNow() > page.ExpiresAtUtc)
                {
                    return Error(HttpStatusCode.BadRequest, specVersion, requestId, "TOKEN_EXPIRED", "Token expired", orderId: orderId);
                }

                string outcome = ResolveAssertOutcome();
                switch (outcome)
                {
                    case SaferpayOptions.MockOutcomeDeclined:
                        return Error(HttpStatusCode.PaymentRequired, specVersion, requestId, "TRANSACTION_DECLINED", "Transaction declined by the Saferpay mock", orderId: orderId, payerMessage: "Card declined (mock)");
                    case SaferpayOptions.MockOutcomeAborted:
                        return Error(HttpStatusCode.PaymentRequired, specVersion, requestId, "TRANSACTION_ABORTED", "Transaction aborted by the payer", orderId: orderId);
                    case SaferpayOptions.MockOutcomeInProgress:
                        return Error(HttpStatusCode.PaymentRequired, specVersion, requestId, "TRANSACTION_IN_WRONG_STATE", "Payment page not completed yet", orderId: orderId);
                }

                bool captured = outcome == SaferpayOptions.MockOutcomeCaptured;
                DateTime now = _utcNow();
                transaction = new MockTransaction
                {
                    Id = $"mock_tx_{Guid.NewGuid():N}",
                    OrderId = page.OrderId,
                    AmountMinor = page.AmountMinor,
                    Currency = page.Currency,
                    Status = captured ? "CAPTURED" : "AUTHORIZED",
                    AuthorizedAtUtc = now
                };
                if (captured)
                {
                    // A payment method without deferred capture: Saferpay reports CAPTURED for the full amount at once.
                    transaction.CapturedAmountMinor = transaction.AmountMinor;
                    transaction.CaptureId = $"{transaction.Id}_c";
                    transaction.ChangedAtUtc = now;
                }

                _transactions[transaction.Id] = transaction;
                page.TransactionId = transaction.Id;
            }

            return Ok(specVersion, requestId, TransactionDetails(transaction));
        }

        private HttpResponseMessage Capture(JObject body, string specVersion, string requestId)
        {
            if (!TryFindTransaction(body, specVersion, requestId, out var transaction, out var errorResponse))
            {
                return errorResponse;
            }

            if (transaction.Status == "CAPTURED")
            {
                return Error(HttpStatusCode.PaymentRequired, specVersion, requestId, "TRANSACTION_ALREADY_CAPTURED", "Transaction already captured", transactionId: transaction.Id);
            }

            if (transaction.Status != "AUTHORIZED")
            {
                return Error(HttpStatusCode.PaymentRequired, specVersion, requestId, "TRANSACTION_IN_WRONG_STATE", "Transaction in wrong state for capture", transactionId: transaction.Id);
            }

            long amount = transaction.AmountMinor;
            if (body.GetValue("Amount", StringComparison.OrdinalIgnoreCase) is JObject)
            {
                if (!TryParsePositiveAmount(SaferpayApiClient.ReadString(body, "Amount", "Value"), out amount))
                {
                    return Error(HttpStatusCode.BadRequest, specVersion, requestId, "VALIDATION_FAILED", "Request validation failed", "Amount.Value: a positive integer is required.");
                }

                string currency = SaferpayApiClient.ReadString(body, "Amount", "CurrencyCode");
                if (!string.Equals(currency, transaction.Currency, StringComparison.OrdinalIgnoreCase))
                {
                    return Error(HttpStatusCode.PaymentRequired, specVersion, requestId, "CURRENCY_INVALID", "Currency does not match the transaction currency", transactionId: transaction.Id);
                }

                if (amount > transaction.AmountMinor)
                {
                    return Error(HttpStatusCode.PaymentRequired, specVersion, requestId, "AMOUNT_INVALID", "Capture amount exceeds the authorized amount", transactionId: transaction.Id);
                }
            }

            transaction.Status = "CAPTURED";
            transaction.CapturedAmountMinor = amount;
            transaction.CaptureId = $"{transaction.Id}_c";
            transaction.ChangedAtUtc = _utcNow();

            return Ok(specVersion, requestId, new JObject
            {
                ["CaptureId"] = transaction.CaptureId,
                ["Status"] = "CAPTURED",
                ["Date"] = FormatDate(transaction.ChangedAtUtc.Value)
            });
        }

        private HttpResponseMessage Cancel(JObject body, string specVersion, string requestId)
        {
            if (!TryFindTransaction(body, specVersion, requestId, out var transaction, out var errorResponse))
            {
                return errorResponse;
            }

            if (transaction.Status != "AUTHORIZED")
            {
                return Error(HttpStatusCode.PaymentRequired, specVersion, requestId, "TRANSACTION_IN_WRONG_STATE", "Transaction in wrong state for cancel", transactionId: transaction.Id);
            }

            transaction.Status = "CANCELED";
            transaction.ChangedAtUtc = _utcNow();

            var response = new JObject { ["TransactionId"] = transaction.Id };
            if (!string.IsNullOrEmpty(transaction.OrderId))
            {
                response["OrderId"] = transaction.OrderId;
            }

            response["Date"] = FormatDate(transaction.ChangedAtUtc.Value);
            return Ok(specVersion, requestId, response);
        }

        private HttpResponseMessage Inquire(JObject body, string specVersion, string requestId)
        {
            if (!TryFindTransaction(body, specVersion, requestId, out var transaction, out var errorResponse))
            {
                return errorResponse;
            }

            // Saferpay's Inquire data can lag a few minutes behind the authorization; the opt-in delay models that.
            int delaySeconds = _options.MockInquireNotFoundSeconds;
            if (delaySeconds > 0 && _utcNow() < transaction.AuthorizedAtUtc.AddSeconds(delaySeconds))
            {
                return Error(HttpStatusCode.PaymentRequired, specVersion, requestId, "TRANSACTION_NOT_FOUND", "Transaction not found");
            }

            return Ok(specVersion, requestId, TransactionDetails(transaction));
        }

        /// <summary>Normalized <see cref="SaferpayOptions.MockAssertOutcome"/>; empty or unknown values mean Authorized.</summary>
        private string ResolveAssertOutcome()
        {
            string configured = _options.MockAssertOutcome?.Trim();
            foreach (var outcome in new[]
                     {
                         SaferpayOptions.MockOutcomeCaptured,
                         SaferpayOptions.MockOutcomeDeclined,
                         SaferpayOptions.MockOutcomeAborted,
                         SaferpayOptions.MockOutcomeInProgress
                     })
            {
                if (string.Equals(configured, outcome, StringComparison.OrdinalIgnoreCase))
                {
                    return outcome;
                }
            }

            return SaferpayOptions.MockOutcomeAuthorized;
        }

        private bool TryFindTransaction(
            JObject body,
            string specVersion,
            string requestId,
            out MockTransaction transaction,
            out HttpResponseMessage errorResponse)
        {
            transaction = null;
            errorResponse = null;
            string transactionId = SaferpayApiClient.ReadString(body, "TransactionReference", "TransactionId");
            string orderId = SaferpayApiClient.ReadString(body, "TransactionReference", "OrderId");
            if ((transactionId == null) == (orderId == null))
            {
                errorResponse = Error(HttpStatusCode.BadRequest, specVersion, requestId, "VALIDATION_FAILED", "Request validation failed", "TransactionReference: exactly one of TransactionId or OrderId must be set.");
                return false;
            }

            if (transactionId != null)
            {
                _transactions.TryGetValue(transactionId, out transaction);
            }
            else
            {
                transaction = _transactions.Values
                    .Where(candidate => string.Equals(candidate.OrderId, orderId, StringComparison.Ordinal))
                    .OrderByDescending(candidate => candidate.AuthorizedAtUtc)
                    .FirstOrDefault();
            }

            if (transaction == null)
            {
                errorResponse = Error(HttpStatusCode.PaymentRequired, specVersion, requestId, "TRANSACTION_NOT_FOUND", "Transaction not found");
                return false;
            }

            return true;
        }

        private JObject TransactionDetails(MockTransaction transaction)
        {
            // Like Saferpay Inquire, a captured transaction reports the captured amount.
            long amount = transaction.Status == "CAPTURED" && transaction.CapturedAmountMinor.HasValue
                ? transaction.CapturedAmountMinor.Value
                : transaction.AmountMinor;

            var details = new JObject
            {
                ["Type"] = "PAYMENT",
                ["Status"] = transaction.Status,
                ["Id"] = transaction.Id
            };
            if (transaction.CaptureId != null)
            {
                details["CaptureId"] = transaction.CaptureId;
            }

            details["Date"] = FormatDate(transaction.AuthorizedAtUtc);
            details["Amount"] = new JObject
            {
                ["Value"] = amount.ToString(CultureInfo.InvariantCulture),
                ["CurrencyCode"] = transaction.Currency
            };
            if (!string.IsNullOrEmpty(transaction.OrderId))
            {
                details["OrderId"] = transaction.OrderId;
            }

            details["AcquirerName"] = "Saferpay Mock";
            details["AcquirerReference"] = "000000";
            details["SixTransactionReference"] = $"0:0:3:{transaction.Id}";
            details["ApprovalCode"] = "012345";

            string payerEmail = string.IsNullOrWhiteSpace(_options.MockPayerEmail)
                ? DefaultPayerEmail
                : _options.MockPayerEmail.Trim();

            return new JObject
            {
                ["Transaction"] = details,
                ["PaymentMeans"] = new JObject
                {
                    ["Brand"] = new JObject { ["PaymentMethod"] = "VISA", ["Name"] = "VISA Saferpay Mock" },
                    ["DisplayText"] = "xxxx xxxx xxxx 0001",
                    ["Card"] = new JObject
                    {
                        ["MaskedNumber"] = "901000xxxxxx0001",
                        ["ExpYear"] = transaction.AuthorizedAtUtc.Year + 3,
                        ["ExpMonth"] = 12,
                        ["HolderName"] = "Mock Driver",
                        ["FundingSource"] = "CREDIT"
                    }
                },
                ["Payer"] = new JObject
                {
                    ["BillingAddress"] = new JObject
                    {
                        ["FirstName"] = "Mock",
                        ["LastName"] = "Driver",
                        ["Email"] = payerEmail
                    }
                },
                ["Liability"] = new JObject
                {
                    ["LiabilityShift"] = true,
                    ["LiableEntity"] = "THREEDS",
                    ["ThreeDs"] = new JObject { ["Authenticated"] = true }
                }
            };
        }

        private string ResolveManagementBaseUrl(string returnUrl)
        {
            if (!string.IsNullOrWhiteSpace(_options.MockCheckoutBaseUrl))
            {
                return _options.MockCheckoutBaseUrl.Trim().TrimEnd('/');
            }

            if (Uri.TryCreate(returnUrl, UriKind.Absolute, out var returnUri))
            {
                return returnUri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
            }

            return FallbackManagementBaseUrl;
        }

        /// <summary>
        /// Mock-only heuristic: the cancel link for the mock checkout page is the return URL with
        /// <c>/Payments/Success</c> replaced by <c>/Payments/Cancel</c> and any <c>session_id</c> parameter removed.
        /// </summary>
        internal static string DeriveMockCancelUrl(string returnUrl)
        {
            if (string.IsNullOrWhiteSpace(returnUrl))
            {
                return string.Empty;
            }

            string url = returnUrl.Trim();
            string fragment = string.Empty;
            int fragmentIndex = url.IndexOf('#');
            if (fragmentIndex >= 0)
            {
                fragment = url.Substring(fragmentIndex);
                url = url.Substring(0, fragmentIndex);
            }

            string query = string.Empty;
            int queryIndex = url.IndexOf('?');
            if (queryIndex >= 0)
            {
                query = url.Substring(queryIndex + 1);
                url = url.Substring(0, queryIndex);
            }

            url = url.Replace("/Payments/Success", "/Payments/Cancel", StringComparison.OrdinalIgnoreCase);

            var kept = query
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Where(parameter =>
                {
                    string name = parameter.Split('=', 2)[0];
                    return !string.Equals(Uri.UnescapeDataString(name), "session_id", StringComparison.OrdinalIgnoreCase);
                })
                .ToList();

            return kept.Count > 0
                ? $"{url}?{string.Join("&", kept)}{fragment}"
                : $"{url}{fragment}";
        }

        private static bool TryParsePositiveAmount(string value, out long amount) =>
            long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out amount) && amount > 0;

        private static string ReadBody(HttpRequestMessage request)
        {
            if (request.Content == null)
            {
                return null;
            }

            using var stream = request.Content.ReadAsStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        private static string FormatDate(DateTime utc) =>
            DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        private static HttpResponseMessage Ok(string specVersion, string requestId, JObject payload)
        {
            var body = new JObject { ["ResponseHeader"] = ResponseHeader(specVersion, requestId) };
            foreach (var property in payload.Properties())
            {
                body[property.Name] = property.Value;
            }

            return Json(HttpStatusCode.OK, body);
        }

        private static HttpResponseMessage Error(
            HttpStatusCode statusCode,
            string specVersion,
            string requestId,
            string errorName,
            string errorMessage,
            string errorDetail = null,
            string transactionId = null,
            string orderId = null,
            string payerMessage = null)
        {
            var body = new JObject
            {
                ["ResponseHeader"] = ResponseHeader(specVersion, requestId),
                ["Behavior"] = SaferpayErrorClassification.BehaviorDoNotRetry,
                ["ErrorName"] = errorName,
                ["ErrorMessage"] = errorMessage
            };
            if (errorDetail != null)
            {
                body["ErrorDetail"] = new JArray(errorDetail);
            }

            if (transactionId != null)
            {
                body["TransactionId"] = transactionId;
            }

            if (orderId != null)
            {
                // Saferpay returns the OrderId in PaymentPage/Assert error messages.
                body["OrderId"] = orderId;
            }

            if (payerMessage != null)
            {
                body["PayerMessage"] = payerMessage;
            }

            return Json(statusCode, body);
        }

        private static JObject ResponseHeader(string specVersion, string requestId) =>
            new JObject
            {
                ["SpecVersion"] = specVersion,
                ["RequestId"] = requestId ?? string.Empty
            };

        private static HttpResponseMessage Json(HttpStatusCode statusCode, JObject body) =>
            new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json")
            };

        private sealed class MockPaymentPage
        {
            public string Token { get; set; }
            public string OrderId { get; set; }
            public long AmountMinor { get; set; }
            public string Currency { get; set; }
            public string ReturnUrl { get; set; }
            public DateTime ExpiresAtUtc { get; set; }
            public string TransactionId { get; set; }
        }

        private sealed class MockTransaction
        {
            public string Id { get; set; }
            public string OrderId { get; set; }
            public long AmountMinor { get; set; }
            public long? CapturedAmountMinor { get; set; }
            public string Currency { get; set; }
            public string Status { get; set; }
            public string CaptureId { get; set; }
            public DateTime AuthorizedAtUtc { get; set; }
            public DateTime? ChangedAtUtc { get; set; }
        }
    }
}
