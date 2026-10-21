using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using OCPP.Core.Server.Payments.Providers;
using OCPP.Core.Server.Payments.Providers.Saferpay;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    public class SaferpayApiClientTests
    {
        private static readonly Regex SaferpayIdFormat = new Regex(@"^[A-Za-z0-9.:_\-]{1,50}$");

        [Fact]
        public void Post_SendsSaferpayHeaders_BasicAuth_ConnectionClose_AndFixedLengthJsonBody()
        {
            var handler = new SaferpayRecordingHandler()
                .Enqueue(SaferpayTestData.Json(HttpStatusCode.OK, new JObject { ["ResponseHeader"] = SaferpayTestData.ResponseHeader(), ["Token"] = "abc" }));
            var client = SaferpayTestData.Client(handler);

            var response = client.Post(SaferpayApiClient.AssertPath, new JObject { ["Token"] = "abc" }, idempotencyKey: null);

            Assert.Equal("abc", (string?)response["Token"]);
            var request = handler.LastRequest;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://saferpay.example.com/api/Payment/v1/PaymentPage/Assert", request.Uri!.ToString());
            Assert.Equal("Basic", request.AuthorizationScheme);
            Assert.Equal(
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{SaferpayTestData.ApiUsername}:{SaferpayTestData.ApiPassword}")),
                request.AuthorizationParameter);
            Assert.True(request.ConnectionClose);
            Assert.Contains("application/json", request.Accept);
            Assert.Equal("application/json", request.ContentType);
            Assert.Equal("utf-8", request.CharSet);
            Assert.Equal(Encoding.UTF8.GetByteCount(request.Body), request.ContentLength);
            Assert.NotEqual(true, request.TransferEncodingChunked);
            Assert.False(handler.AsyncSendUsed);

            var header = (JObject)request.Json!["RequestHeader"]!;
            Assert.Equal("1.54", (string?)header["SpecVersion"]);
            Assert.Equal(SaferpayTestData.CustomerId, (string?)header["CustomerId"]);
            Assert.Equal(0, (int)header["RetryIndicator"]!);
            Assert.Matches(SaferpayIdFormat, (string)header["RequestId"]!);
            Assert.Equal("abc", (string?)request.Json["Token"]);
            Assert.Equal("RequestHeader", request.Json.Properties().First().Name);
        }

        [Fact]
        public void Post_UsesConfiguredSpecVersion_AndTrimsBaseUrlSlash()
        {
            var handler = new SaferpayRecordingHandler()
                .Enqueue(SaferpayTestData.Json(HttpStatusCode.OK, new JObject { ["Token"] = "abc" }));
            var client = SaferpayTestData.Client(handler, SaferpayTestData.Options(options =>
            {
                options.SpecVersion = "1.53";
                options.BaseUrl = "https://saferpay.example.com/api/";
            }));

            client.Post(SaferpayApiClient.InquirePath, new JObject(), null);

            Assert.Equal("https://saferpay.example.com/api/Payment/v1/Transaction/Inquire", handler.LastRequest.Uri!.ToString());
            Assert.Equal("1.53", (string?)handler.LastRequest.Json!["RequestHeader"]!["SpecVersion"]);
        }

        [Fact]
        public void BuildRequestId_IsDeterministicForIdempotencyKey_AndMatchesSaferpayIdFormat()
        {
            string first = SaferpayApiClient.BuildRequestId("capture:3f2504e0-4f89-11d3-9a0c-0305e82c3301:1245");
            string second = SaferpayApiClient.BuildRequestId("capture:3f2504e0-4f89-11d3-9a0c-0305e82c3301:1245");
            string other = SaferpayApiClient.BuildRequestId("cancel:3f2504e0-4f89-11d3-9a0c-0305e82c3301");

            Assert.Equal(first, second);
            Assert.NotEqual(first, other);
            Assert.Equal(40, first.Length);
            Assert.Matches("^[0-9a-f]{40}$", first);
            Assert.Matches(SaferpayIdFormat, first);
            Assert.True(first.Length <= SaferpayApiClient.MaxRequestIdLength);
        }

        [Fact]
        public void BuildRequestId_WithoutIdempotencyKey_IsRandomGuid()
        {
            string first = SaferpayApiClient.BuildRequestId(null);
            string second = SaferpayApiClient.BuildRequestId("  ");

            Assert.NotEqual(first, second);
            Assert.Matches("^[0-9a-f]{32}$", first);
            Assert.Matches(SaferpayIdFormat, second);
        }

        [Fact]
        public void Post_SendsRequestIdDerivedFromIdempotencyKey()
        {
            var handler = new SaferpayRecordingHandler()
                .Enqueue(SaferpayTestData.Json(HttpStatusCode.OK, new JObject { ["Status"] = "CAPTURED" }))
                .Enqueue(SaferpayTestData.Json(HttpStatusCode.OK, new JObject { ["Status"] = "CAPTURED" }));
            var client = SaferpayTestData.Client(handler);

            client.Post(SaferpayApiClient.CapturePath, new JObject(), "capture:key");
            client.Post(SaferpayApiClient.CapturePath, new JObject(), "capture:key");

            string expected = SaferpayApiClient.BuildRequestId("capture:key");
            Assert.All(handler.Requests, request => Assert.Equal(expected, (string?)request.Json!["RequestHeader"]!["RequestId"]));
        }

        [Theory]
        [InlineData(HttpStatusCode.PaymentRequired, "DO_NOT_RETRY", "TRANSACTION_DECLINED", false, "DO_NOT_RETRY")]
        [InlineData(HttpStatusCode.PaymentRequired, "RETRY_LATER", "TRANSACTION_DECLINED", true, "RETRY_LATER")]
        [InlineData(HttpStatusCode.BadRequest, "RETRY", "SOMETHING_TEMPORARY", true, "RETRY")]
        [InlineData(HttpStatusCode.PaymentRequired, "OTHER_MEANS", "PAYMENTMEANS_NOT_SUPPORTED", false, "OTHER_MEANS")]
        [InlineData(HttpStatusCode.PaymentRequired, "ABORT", "TRANSACTION_DECLINED", false, "DO_NOT_RETRY")]
        [InlineData(HttpStatusCode.PaymentRequired, "SOMETHING_NEW", "GENERAL_DECLINED", false, "DO_NOT_RETRY")]
        [InlineData(HttpStatusCode.PaymentRequired, "DO_NOT_RETRY", "COMMUNICATION_TIMEOUT", true, "DO_NOT_RETRY")]
        [InlineData(HttpStatusCode.PaymentRequired, "DO_NOT_RETRY", "COMMUNICATION_FAILED", true, "DO_NOT_RETRY")]
        [InlineData(HttpStatusCode.PaymentRequired, "DO_NOT_RETRY", "UNEXPECTED_ERROR_BY_ACQUIRER", true, "DO_NOT_RETRY")]
        [InlineData(HttpStatusCode.InternalServerError, "DO_NOT_RETRY", "INTERNAL_ERROR", true, "DO_NOT_RETRY")]
        [InlineData(HttpStatusCode.InternalServerError, "DO_NOT_RETRY", "VALIDATION_FAILED", true, "DO_NOT_RETRY")]
        [InlineData(HttpStatusCode.Unauthorized, "DO_NOT_RETRY", "AUTHENTICATION_FAILED", false, "DO_NOT_RETRY")]
        [InlineData(HttpStatusCode.Forbidden, "DO_NOT_RETRY", "PERMISSION_DENIED", false, "DO_NOT_RETRY")]
        [InlineData(HttpStatusCode.BadRequest, "DO_NOT_RETRY", "VALIDATION_FAILED", false, "DO_NOT_RETRY")]
        public void Post_ClassifiesSaferpayErrorBodies(
            HttpStatusCode statusCode,
            string behavior,
            string errorName,
            bool expectedTransient,
            string expectedBehavior)
        {
            var handler = new SaferpayRecordingHandler()
                .Enqueue(SaferpayTestData.Error(statusCode, errorName, behavior, "Something failed"));
            var client = SaferpayTestData.Client(handler);

            var exception = Assert.Throws<SaferpayApiException>(() =>
                client.Post(SaferpayApiClient.CapturePath, new JObject(), null));

            Assert.IsAssignableFrom<PaymentProviderException>(exception);
            Assert.Equal(PaymentProviderNames.Saferpay, exception.Provider);
            Assert.Equal(errorName, exception.Code);
            Assert.Equal(errorName, exception.ErrorName);
            Assert.Equal(expectedTransient, exception.IsTransient);
            Assert.Equal(expectedBehavior, exception.Behavior);
            Assert.Equal(behavior, exception.RawBehavior);
            Assert.Equal((int)statusCode, exception.HttpStatusCode);
            Assert.Equal("Transaction/Capture", exception.Operation);
        }

        [Theory]
        [InlineData(HttpStatusCode.InternalServerError, "text/html", true)]
        [InlineData(HttpStatusCode.ServiceUnavailable, "text/html", true)]
        [InlineData(HttpStatusCode.RequestTimeout, "text/plain", true)]
        [InlineData(HttpStatusCode.TooManyRequests, "text/plain", true)]
        [InlineData(HttpStatusCode.PaymentRequired, "text/plain", false)]
        [InlineData(HttpStatusCode.Unauthorized, "text/html", false)]
        [InlineData(HttpStatusCode.Forbidden, "text/html", false)]
        public void Post_NonJsonErrorBody_UsesHttpStatusCode(HttpStatusCode statusCode, string mediaType, bool expectedTransient)
        {
            var handler = new SaferpayRecordingHandler()
                .Enqueue(SaferpayTestData.Text(statusCode, "<html><body>Firewall says no " + SaferpayTestData.ApiPassword + "</body></html>", mediaType));
            var client = SaferpayTestData.Client(handler);

            var exception = Assert.Throws<SaferpayApiException>(() =>
                client.Post(SaferpayApiClient.AssertPath, new JObject(), null));

            Assert.Equal($"http_{(int)statusCode}", exception.Code);
            Assert.Null(exception.ErrorName);
            Assert.Equal(expectedTransient, exception.IsTransient);
            Assert.Equal((int)statusCode, exception.HttpStatusCode);
            Assert.DoesNotContain("Firewall", exception.Message);
            Assert.DoesNotContain(SaferpayTestData.ApiPassword, exception.Message);
        }

        [Fact]
        public void Post_JsonContentTypeWithInvalidBody_UsesHttpStatusCode()
        {
            var handler = new SaferpayRecordingHandler()
                .Enqueue(SaferpayTestData.Json(HttpStatusCode.BadGateway, "{not json"));
            var client = SaferpayTestData.Client(handler);

            var exception = Assert.Throws<SaferpayApiException>(() =>
                client.Post(SaferpayApiClient.AssertPath, new JObject(), null));

            Assert.Equal("http_502", exception.Code);
            Assert.True(exception.IsTransient);
        }

        [Fact]
        public void Post_SuccessWithoutJsonObject_IsInvalidResponse()
        {
            var handler = new SaferpayRecordingHandler()
                .Enqueue(SaferpayTestData.Text(HttpStatusCode.OK, "<html>maintenance</html>"));
            var client = SaferpayTestData.Client(handler);

            var exception = Assert.Throws<SaferpayApiException>(() =>
                client.Post(SaferpayApiClient.AssertPath, new JObject(), null));

            Assert.Equal("invalid_response", exception.Code);
            Assert.Equal(200, exception.HttpStatusCode);
        }

        [Fact]
        public void Post_Timeout_IsTransientTimeout()
        {
            var client = SaferpayTestData.Client(new SaferpayHangingHandler(), timeout: TimeSpan.FromMilliseconds(200));

            var exception = Assert.Throws<SaferpayApiException>(() =>
                client.Post(SaferpayApiClient.CapturePath, new JObject(), "capture:key"));

            Assert.Equal("timeout", exception.Code);
            Assert.True(exception.IsTransient);
            Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
        }

        [Fact]
        public void Post_HttpRequestException_IsTransientNetworkError()
        {
            var handler = new SaferpayRecordingHandler
            {
                ExceptionToThrow = new HttpRequestException("Connection refused (saferpay.example.com:443)")
            };
            var client = SaferpayTestData.Client(handler);

            var exception = Assert.Throws<SaferpayApiException>(() =>
                client.Post(SaferpayApiClient.CapturePath, new JObject(), null));

            Assert.Equal("network_error", exception.Code);
            Assert.True(exception.IsTransient);
            Assert.IsType<HttpRequestException>(exception.InnerException);
        }

        [Fact]
        public void Post_InvalidBaseUrl_IsPermanentConfigurationError()
        {
            var handler = new SaferpayRecordingHandler();
            var client = SaferpayTestData.Client(handler, SaferpayTestData.Options(options => options.BaseUrl = "not a url"));

            var exception = Assert.Throws<SaferpayApiException>(() =>
                client.Post(SaferpayApiClient.CapturePath, new JObject(), null));

            Assert.Equal("invalid_configuration", exception.Code);
            Assert.False(exception.IsTransient);
            Assert.Empty(handler.Requests);
        }

        [Fact]
        public void Post_ErrorBody_IsParsed_AndMessagesAreSanitized()
        {
            var body = new JObject
            {
                ["ResponseHeader"] = SaferpayTestData.ResponseHeader(),
                ["Behavior"] = "DO_NOT_RETRY",
                ["ErrorName"] = "TRANSACTION_DECLINED",
                ["ErrorMessage"] = $"Declined for {SaferpayTestData.ApiUsername} with {SaferpayTestData.ApiPassword} on {SaferpayTestData.TransactionId}",
                ["ErrorDetail"] = new JArray("Payment.Amount: secret detail"),
                ["TransactionId"] = SaferpayTestData.TransactionId,
                ["OrderId"] = "c52ad184-7235-4511-ab2c-33b59e796901",
                ["ProcessorName"] = "Saferpay Test",
                ["ProcessorResult"] = "05",
                ["ProcessorMessage"] = "Authorization declined",
                ["PayerMessage"] = "Card declined by issuer " + SaferpayTestData.ApiPassword
            };
            var handler = new SaferpayRecordingHandler().Enqueue(SaferpayTestData.Json(HttpStatusCode.PaymentRequired, body));
            var client = SaferpayTestData.Client(handler);

            var exception = Assert.Throws<SaferpayApiException>(() =>
                client.Post(SaferpayApiClient.CapturePath, new JObject(), null));

            Assert.Equal(SaferpayTestData.TransactionId, exception.TransactionId);
            Assert.Equal("c52ad184-7235-4511-ab2c-33b59e796901", exception.OrderId);
            Assert.Equal("05", exception.ProcessorResult);
            Assert.Equal("Saferpay Test", exception.ProcessorName);
            Assert.Equal("Authorization declined", exception.ProcessorMessage);
            Assert.Equal(new[] { "Payment.Amount: secret detail" }, exception.ErrorDetail);
            Assert.Contains("TRANSACTION_DECLINED", exception.Message);
            Assert.Contains("Authorization declined", exception.Message);
            foreach (var text in new[] { exception.Message, exception.ProviderMessage })
            {
                Assert.DoesNotContain(SaferpayTestData.ApiPassword, text);
                Assert.DoesNotContain(SaferpayTestData.ApiUsername, text);
                Assert.DoesNotContain(SaferpayTestData.TransactionId, text);
                Assert.DoesNotContain("secret detail", text);
            }

            Assert.StartsWith("Card declined by issuer", exception.ProviderMessage);
            Assert.Contains(SaferpayTextSanitizer.Redacted, exception.ProviderMessage);
        }

        [Fact]
        public void TryParseObject_KeepsDatesAsStrings()
        {
            var parsed = SaferpayApiClient.TryParseObject("{\"Expiration\":\"2015-01-30T12:45:22.258+01:00\"}");

            Assert.Equal(JTokenType.String, parsed!["Expiration"]!.Type);
            Assert.Equal("2015-01-30T12:45:22.258+01:00", SaferpayApiClient.ReadString(parsed, "Expiration"));
        }
    }
}
