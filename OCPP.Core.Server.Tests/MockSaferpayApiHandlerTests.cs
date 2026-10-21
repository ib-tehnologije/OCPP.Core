using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using OCPP.Core.Server.Payments.Providers;
using OCPP.Core.Server.Payments.Providers.Saferpay;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    public class MockSaferpayApiHandlerTests
    {
        private const string ReturnUrl = "https://portal.example.com:8443/Payments/Success?reservationId={0}&origin=public";

        private static SaferpayOptions MockOptions(Action<SaferpayOptions>? configure = null)
        {
            var options = new SaferpayOptions
            {
                Enabled = true,
                UseMockServices = true,
                BaseUrl = null!,
                NotifyBaseUrl = "https://ocpp.example.com/API"
            };
            configure?.Invoke(options);
            return options;
        }

        private static (SaferpayPaymentGateway Gateway, MockSaferpayApiHandler Handler) CreateMock(
            Action<SaferpayOptions>? configure = null,
            Func<DateTime>? utcNow = null)
        {
            var options = MockOptions(configure);
            var handler = new MockSaferpayApiHandler(options, utcNow);
            return (SaferpayTestData.Gateway(handler, options), handler);
        }

        private static PaymentCheckoutRequest Request(Guid reservationId, long amount = 2500) =>
            new PaymentCheckoutRequest
            {
                ReservationId = reservationId,
                AmountMinor = amount,
                Currency = "eur",
                ProductName = "EV charging session",
                SuccessUrl = string.Format(ReturnUrl, reservationId),
                IdempotencyKey = $"checkout_create:{reservationId}"
            };

        [Fact]
        public void Initialize_ReturnsMockToken_AndMockCheckoutRedirect()
        {
            var now = new DateTime(2026, 10, 20, 10, 0, 0, DateTimeKind.Utc);
            var (gateway, _) = CreateMock(utcNow: () => now);
            var reservationId = Guid.NewGuid();
            var request = Request(reservationId);

            Assert.True(gateway.IsConfigured);
            var session = gateway.CreateCheckout(request);

            Assert.StartsWith("mock_tok_", session.SessionReference);
            Assert.Matches("^mock_tok_[0-9a-f]{32}$", session.SessionReference);
            Assert.Equal(now.AddHours(1), session.ExpiresAtUtc);
            Assert.Null(session.AuthorizationReference);

            var redirect = new Uri(session.RedirectUrl);
            Assert.Equal("https://portal.example.com:8443", redirect.GetLeftPart(UriPartial.Authority));
            Assert.Equal("/Payments/MockCheckout", redirect.AbsolutePath);
            var query = ParseQuery(redirect);
            Assert.Equal(reservationId.ToString("D"), query["reservationId"]);
            Assert.Equal(request.SuccessUrl, query["successUrl"]);
            Assert.Equal($"https://portal.example.com:8443/Payments/Cancel?reservationId={reservationId}&origin=public", query["cancelUrl"]);
            Assert.Equal("Saferpay", query["provider"]);
        }

        [Fact]
        public void Initialize_UsesConfiguredMockCheckoutBaseUrl()
        {
            var (gateway, _) = CreateMock(o => o.MockCheckoutBaseUrl = "http://127.0.0.1:8082/");

            var session = gateway.CreateCheckout(Request(Guid.NewGuid()));

            Assert.StartsWith("http://127.0.0.1:8082/Payments/MockCheckout?reservationId=", session.RedirectUrl);
        }

        [Fact]
        public void FullFlow_Initialize_Assert_PartialCapture_Inquire()
        {
            var (gateway, _) = CreateMock();
            var reservationId = Guid.NewGuid();
            var session = gateway.CreateCheckout(Request(reservationId, 2500));

            var checkout = gateway.GetCheckout(session.SessionReference);

            Assert.Equal(PaymentCheckoutState.Complete, checkout.State);
            Assert.True(checkout.IsPaid);
            Assert.Equal("AUTHORIZED", checkout.RawStatus);
            Assert.StartsWith("mock_tx_", checkout.AuthorizationReference);
            Assert.Equal(MockSaferpayApiHandler.DefaultPayerEmail, checkout.CustomerEmail);
            Assert.Equal("Mock Driver", checkout.CustomerName);
            Assert.Equal(reservationId, checkout.OwnerReservationId);
            Assert.Equal(PaymentAuthorizationState.RequiresCapture, checkout.Authorization.State);
            Assert.Equal(2500, checkout.Authorization.AmountCapturableMinor);
            Assert.Equal("eur", checkout.Authorization.Currency);

            var again = gateway.GetCheckout(session.SessionReference);
            Assert.Equal(checkout.AuthorizationReference, again.AuthorizationReference);

            var captured = gateway.Capture(checkout.AuthorizationReference, 1200, "eur", $"capture:{reservationId}:1200");

            Assert.Equal(PaymentAuthorizationState.Captured, captured.State);
            Assert.Equal(1200, captured.AmountReceivedMinor);
            Assert.Equal(checkout.AuthorizationReference + "_c", captured.CaptureReference);

            var inquired = gateway.GetAuthorization(checkout.AuthorizationReference);
            Assert.Equal(PaymentAuthorizationState.Captured, inquired.State);
            Assert.Equal("CAPTURED", inquired.RawStatus);
            Assert.Equal(1200, inquired.AmountMinor);
            Assert.Equal(1200, inquired.AmountReceivedMinor);
            Assert.Equal(reservationId, inquired.OwnerReservationId);

            var assertedAfterCapture = gateway.GetCheckout(session.SessionReference);
            Assert.Equal(PaymentAuthorizationState.Captured, assertedAfterCapture.Authorization.State);

            // A replayed capture hits TRANSACTION_ALREADY_CAPTURED in the mock; the gateway reports the capture.
            var replay = gateway.Capture(checkout.AuthorizationReference, 1200, "eur", $"capture:{reservationId}:1200");
            Assert.Equal(PaymentAuthorizationState.Captured, replay.State);
            Assert.Equal(1200, replay.AmountReceivedMinor);
        }

        [Fact]
        public void FullFlow_Initialize_Assert_Cancel_Inquire()
        {
            var (gateway, _) = CreateMock();
            var reservationId = Guid.NewGuid();
            var session = gateway.CreateCheckout(Request(reservationId));
            var checkout = gateway.GetCheckout(session.SessionReference);

            var canceled = gateway.Cancel(checkout.AuthorizationReference, $"cancel:{reservationId}");

            Assert.Equal(PaymentAuthorizationState.Canceled, canceled.State);
            Assert.Equal(reservationId, canceled.OwnerReservationId);
            Assert.Equal(checkout.AuthorizationReference, canceled.AuthorizationReference);

            var inquired = gateway.GetAuthorization(checkout.AuthorizationReference);
            Assert.Equal(PaymentAuthorizationState.Canceled, inquired.State);
            Assert.Equal("CANCELED", inquired.RawStatus);

            var asserted = gateway.GetCheckout(session.SessionReference);
            Assert.False(asserted.IsPaid);
            Assert.Equal(PaymentAuthorizationState.Canceled, asserted.Authorization.State);

            var repeated = gateway.Cancel(checkout.AuthorizationReference, $"cancel:{reservationId}");
            Assert.Equal(PaymentAuthorizationState.Canceled, repeated.State);

            var exception = Assert.Throws<SaferpayApiException>(() =>
                gateway.Capture(checkout.AuthorizationReference, 100, "eur", "capture:after-cancel"));
            Assert.Equal("TRANSACTION_IN_WRONG_STATE", exception.Code);
        }

        [Fact]
        public void Capture_AboveAuthorizedAmount_IsAmountInvalid()
        {
            var (gateway, _) = CreateMock();
            var session = gateway.CreateCheckout(Request(Guid.NewGuid(), 2500));
            var checkout = gateway.GetCheckout(session.SessionReference);

            var exception = Assert.Throws<SaferpayApiException>(() =>
                gateway.Capture(checkout.AuthorizationReference, 2501, "eur", "capture:too-much"));

            Assert.Equal("AMOUNT_INVALID", exception.Code);
            Assert.False(exception.IsTransient);
            Assert.Equal(PaymentAuthorizationState.RequiresCapture, gateway.GetAuthorization(checkout.AuthorizationReference).State);
        }

        [Fact]
        public void Capture_WithOtherCurrency_IsCurrencyInvalid()
        {
            var (gateway, _) = CreateMock();
            var session = gateway.CreateCheckout(Request(Guid.NewGuid(), 2500));
            var checkout = gateway.GetCheckout(session.SessionReference);

            var exception = Assert.Throws<SaferpayApiException>(() =>
                gateway.Capture(checkout.AuthorizationReference, 100, "chf", "capture:chf"));

            Assert.Equal("CURRENCY_INVALID", exception.Code);
        }

        [Fact]
        public void SecondCapture_ReturnsTransactionAlreadyCapturedError()
        {
            var (_, handler) = CreateMock();
            using var client = new HttpClient(handler, disposeHandler: false);
            var token = (string)Post(client, SaferpayApiClient.InitializePath, InitializeBody(2500))["Token"]!;
            var transactionId = (string)Post(client, SaferpayApiClient.AssertPath, new JObject { ["Token"] = token })["Transaction"]!["Id"]!;
            var captureBody = new JObject
            {
                ["TransactionReference"] = new JObject { ["TransactionId"] = transactionId },
                ["Amount"] = new JObject { ["Value"] = "1000", ["CurrencyCode"] = "EUR" }
            };

            var first = Post(client, SaferpayApiClient.CapturePath, captureBody);
            using var second = Send(client, SaferpayApiClient.CapturePath, captureBody);
            var secondBody = JObject.Parse(SaferpayTestData.ReadBody(second));

            Assert.Equal("CAPTURED", (string?)first["Status"]);
            Assert.Equal(transactionId + "_c", (string?)first["CaptureId"]);
            Assert.Equal(HttpStatusCode.PaymentRequired, second.StatusCode);
            Assert.Equal("application/json", second.Content.Headers.ContentType!.MediaType);
            Assert.Equal("TRANSACTION_ALREADY_CAPTURED", (string?)secondBody["ErrorName"]);
            Assert.Equal("DO_NOT_RETRY", (string?)secondBody["Behavior"]);
            Assert.NotNull(secondBody["ErrorMessage"]);
            Assert.NotNull(secondBody["ResponseHeader"]);
        }

        [Fact]
        public void Responses_EchoRequestHeader()
        {
            var (_, handler) = CreateMock();
            using var client = new HttpClient(handler, disposeHandler: false);

            var response = Post(client, SaferpayApiClient.InitializePath, InitializeBody(500));

            Assert.Equal("req-1", (string?)response["ResponseHeader"]!["RequestId"]);
            Assert.Equal("1.54", (string?)response["ResponseHeader"]!["SpecVersion"]);
        }

        [Fact]
        public void Assert_UnknownToken_IsTokenInvalid_AndGatewayReturnsNull()
        {
            var (gateway, _) = CreateMock();

            Assert.Null(gateway.GetCheckout("mock_tok_unknown"));

            // Inquire's TRANSACTION_NOT_FOUND is indeterminate for a transaction id (Inquire lags), so it is raised as transient.
            var exception = Assert.Throws<SaferpayApiException>(() => gateway.GetAuthorization("mock_tx_unknown"));
            Assert.Equal("inquire_not_found", exception.Code);
            Assert.True(exception.IsTransient);
            Assert.Null(gateway.FindAuthorizationByReservation(Guid.NewGuid()));
        }

        [Fact]
        public void Assert_AfterExpirationWithoutPayment_IsExpired()
        {
            var now = new DateTime(2026, 10, 20, 10, 0, 0, DateTimeKind.Utc);
            var (gateway, _) = CreateMock(utcNow: () => now);
            var session = gateway.CreateCheckout(Request(Guid.NewGuid()));

            now = now.AddHours(2);
            var status = gateway.GetCheckout(session.SessionReference);

            Assert.Equal(PaymentCheckoutState.Expired, status.State);
            Assert.Equal("TOKEN_EXPIRED", status.RawStatus);
        }

        [Fact]
        public void Assert_UsesConfiguredMockPayerEmail()
        {
            var (gateway, _) = CreateMock(o => o.MockPayerEmail = "mock.payer@example.com");
            var session = gateway.CreateCheckout(Request(Guid.NewGuid()));

            Assert.Equal("mock.payer@example.com", gateway.GetCheckout(session.SessionReference).CustomerEmail);
        }

        [Fact]
        public void Initialize_InvalidRequest_IsValidationFailed()
        {
            var (_, handler) = CreateMock();
            using var client = new HttpClient(handler, disposeHandler: false);
            var body = InitializeBody(0);

            using var response = Send(client, SaferpayApiClient.InitializePath, body);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("VALIDATION_FAILED", (string?)JObject.Parse(SaferpayTestData.ReadBody(response))["ErrorName"]);
        }

        [Fact]
        public void Inquire_ByOrderId_FindsTransaction()
        {
            var (_, handler) = CreateMock();
            using var client = new HttpClient(handler, disposeHandler: false);
            var token = (string)Post(client, SaferpayApiClient.InitializePath, InitializeBody(700, "order-42"))["Token"]!;
            Post(client, SaferpayApiClient.AssertPath, new JObject { ["Token"] = token });

            var inquired = Post(client, SaferpayApiClient.InquirePath, new JObject
            {
                ["TransactionReference"] = new JObject { ["OrderId"] = "order-42" }
            });

            Assert.Equal("AUTHORIZED", (string?)inquired["Transaction"]!["Status"]);
            Assert.Equal("700", (string?)inquired["Transaction"]!["Amount"]!["Value"]);
        }

        [Fact]
        public void FindAuthorizationByReservation_InquiresByOrderId()
        {
            var (gateway, handler) = CreateMock();
            var reservationId = Guid.NewGuid();
            var session = gateway.CreateCheckout(Request(reservationId, 1800));

            Assert.Null(gateway.FindAuthorizationByReservation(reservationId));

            var checkout = gateway.GetCheckout(session.SessionReference);
            var found = gateway.FindAuthorizationByReservation(reservationId);

            Assert.NotNull(found);
            Assert.Equal(checkout.AuthorizationReference, found!.AuthorizationReference);
            Assert.Equal(PaymentAuthorizationState.RequiresCapture, found.State);
            Assert.Equal(1800, found.AmountCapturableMinor);
            Assert.Equal(reservationId, found.OwnerReservationId);
            Assert.Null(gateway.FindAuthorizationByReservation(Guid.NewGuid()));

            gateway.Cancel(found.AuthorizationReference, $"cancel:{reservationId}");
            Assert.Equal(PaymentAuthorizationState.Canceled, gateway.FindAuthorizationByReservation(reservationId)!.State);
            Assert.Equal(7, handler.RequestCount);
        }

        [Fact]
        public void Assert_ErrorsCarryOrderId()
        {
            var now = new DateTime(2026, 10, 20, 10, 0, 0, DateTimeKind.Utc);
            var (_, handler) = CreateMock(o => o.MockAssertOutcome = SaferpayOptions.MockOutcomeDeclined, () => now);
            using var client = new HttpClient(handler, disposeHandler: false);
            var token = (string)Post(client, SaferpayApiClient.InitializePath, InitializeBody(700, "order-7"))["Token"]!;

            using var declined = Send(client, SaferpayApiClient.AssertPath, new JObject { ["Token"] = token });
            now = now.AddHours(2);
            using var expired = Send(client, SaferpayApiClient.AssertPath, new JObject { ["Token"] = token });

            var declinedBody = JObject.Parse(SaferpayTestData.ReadBody(declined));
            var expiredBody = JObject.Parse(SaferpayTestData.ReadBody(expired));
            Assert.Equal(HttpStatusCode.PaymentRequired, declined.StatusCode);
            Assert.Equal("TRANSACTION_DECLINED", (string?)declinedBody["ErrorName"]);
            Assert.Equal("order-7", (string?)declinedBody["OrderId"]);
            Assert.NotNull(declinedBody["PayerMessage"]);
            Assert.Equal("TOKEN_EXPIRED", (string?)expiredBody["ErrorName"]);
            Assert.Equal("order-7", (string?)expiredBody["OrderId"]);
        }

        [Fact]
        public void Assert_ExpiredResumedPage_FindsEarlierAuthorizationByOrderId()
        {
            // A reservation whose first payment page was paid and whose later page expired unpaid: TOKEN_EXPIRED alone
            // would read as "no authorization", but Inquire by OrderId reports the held transaction.
            var now = new DateTime(2026, 10, 20, 10, 0, 0, DateTimeKind.Utc);
            var (gateway, _) = CreateMock(utcNow: () => now);
            var reservationId = Guid.NewGuid();
            var paidPage = gateway.CreateCheckout(Request(reservationId, 2500));
            var paid = gateway.GetCheckout(paidPage.SessionReference);
            var unpaidPage = gateway.CreateCheckout(Request(reservationId, 2500));

            now = now.AddHours(2);
            var status = gateway.GetCheckout(unpaidPage.SessionReference);

            Assert.Equal(PaymentCheckoutState.Complete, status.State);
            Assert.True(status.IsPaid);
            Assert.Equal(unpaidPage.SessionReference, status.SessionReference);
            Assert.Equal(paid.AuthorizationReference, status.AuthorizationReference);
            Assert.Equal(PaymentAuthorizationState.RequiresCapture, status.Authorization.State);
            Assert.Equal(reservationId, status.OwnerReservationId);
        }

        [Fact]
        public void Assert_ExpiredUnpaidPage_IsExpired_AfterInquireByOrderId()
        {
            var now = new DateTime(2026, 10, 20, 10, 0, 0, DateTimeKind.Utc);
            var (gateway, handler) = CreateMock(utcNow: () => now);
            var reservationId = Guid.NewGuid();
            var session = gateway.CreateCheckout(Request(reservationId));

            now = now.AddHours(2);
            var status = gateway.GetCheckout(session.SessionReference);

            Assert.Equal(PaymentCheckoutState.Expired, status.State);
            Assert.Equal(reservationId, status.OwnerReservationId);
            Assert.Equal(3, handler.RequestCount);
        }

        [Fact]
        public void MockAssertOutcome_Captured_CreatesCapturedTransaction_ThatCannotBeCanceled()
        {
            var (gateway, _) = CreateMock(o => o.MockAssertOutcome = "captured");
            var reservationId = Guid.NewGuid();
            var session = gateway.CreateCheckout(Request(reservationId, 2500));

            var checkout = gateway.GetCheckout(session.SessionReference);

            Assert.Equal(PaymentCheckoutState.Complete, checkout.State);
            Assert.True(checkout.IsPaid);
            Assert.Equal("CAPTURED", checkout.RawStatus);
            Assert.Equal(PaymentAuthorizationState.Captured, checkout.Authorization.State);
            Assert.Equal(2500, checkout.Authorization.AmountReceivedMinor);
            Assert.Equal(checkout.AuthorizationReference + "_c", checkout.Authorization.CaptureReference);
            Assert.False(checkout.Authorization.IsCancelable);
            Assert.Equal(PaymentAuthorizationState.Captured, gateway.GetAuthorization(checkout.AuthorizationReference).State);

            var exception = Assert.Throws<SaferpayApiException>(() => gateway.Cancel(checkout.AuthorizationReference, $"cancel:{reservationId}"));
            Assert.Equal("transaction_already_captured", exception.Code);
            Assert.False(exception.IsTransient);
        }

        [Fact]
        public void MockAssertOutcome_Declined_IsFailed_WithoutTransaction()
        {
            var (gateway, _) = CreateMock(o => o.MockAssertOutcome = SaferpayOptions.MockOutcomeDeclined);
            var reservationId = Guid.NewGuid();
            var session = gateway.CreateCheckout(Request(reservationId));

            var status = gateway.GetCheckout(session.SessionReference);

            Assert.Equal(PaymentCheckoutState.Failed, status.State);
            Assert.False(status.IsPaid);
            Assert.Equal("TRANSACTION_DECLINED", status.FailureCode);
            Assert.Equal("Card declined (mock)", status.FailureMessage);
            Assert.Equal(reservationId, status.OwnerReservationId);
            Assert.Null(status.AuthorizationReference);
            Assert.Null(gateway.FindAuthorizationByReservation(reservationId));
        }

        [Fact]
        public void MockAssertOutcome_Aborted_IsCanceled()
        {
            var (gateway, _) = CreateMock(o => o.MockAssertOutcome = SaferpayOptions.MockOutcomeAborted);
            var reservationId = Guid.NewGuid();
            var session = gateway.CreateCheckout(Request(reservationId));

            var status = gateway.GetCheckout(session.SessionReference);

            Assert.Equal(PaymentCheckoutState.Canceled, status.State);
            Assert.Equal("TRANSACTION_ABORTED", status.RawStatus);
            Assert.Equal(reservationId, status.OwnerReservationId);
            Assert.Null(gateway.FindAuthorizationByReservation(reservationId));
        }

        [Fact]
        public void MockAssertOutcome_InProgress_IsOpen_UntilThePayerFinishes()
        {
            var options = MockOptions(o => o.MockAssertOutcome = SaferpayOptions.MockOutcomeInProgress);
            var handler = new MockSaferpayApiHandler(options, null);
            var gateway = SaferpayTestData.Gateway(handler, options);
            var reservationId = Guid.NewGuid();
            var session = gateway.CreateCheckout(Request(reservationId));

            var pending = gateway.GetCheckout(session.SessionReference);
            Assert.Equal(PaymentCheckoutState.Open, pending.State);
            Assert.Equal("TRANSACTION_IN_WRONG_STATE", pending.RawStatus);
            Assert.Null(pending.AuthorizationReference);

            options.MockAssertOutcome = null;
            var finished = gateway.GetCheckout(session.SessionReference);

            Assert.Equal(PaymentCheckoutState.Complete, finished.State);
            Assert.True(finished.IsPaid);
            Assert.Equal(PaymentAuthorizationState.RequiresCapture, finished.Authorization.State);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("Authorized")]
        [InlineData("not-a-known-outcome")]
        public void MockAssertOutcome_DefaultOrUnknown_Authorizes(string? outcome)
        {
            var (gateway, _) = CreateMock(o => o.MockAssertOutcome = outcome!);
            var session = gateway.CreateCheckout(Request(Guid.NewGuid()));

            var status = gateway.GetCheckout(session.SessionReference);

            Assert.Equal("AUTHORIZED", status.RawStatus);
            Assert.Equal(PaymentAuthorizationState.RequiresCapture, status.Authorization.State);
        }

        [Fact]
        public void MockInquireNotFoundSeconds_DelaysInquire_ButNotCaptureOrCancel()
        {
            var now = new DateTime(2026, 10, 20, 10, 0, 0, DateTimeKind.Utc);
            var (gateway, _) = CreateMock(o => o.MockInquireNotFoundSeconds = 30, () => now);
            var reservationId = Guid.NewGuid();
            var checkout = gateway.GetCheckout(gateway.CreateCheckout(Request(reservationId, 2500)).SessionReference);
            var otherReservationId = Guid.NewGuid();
            var other = gateway.GetCheckout(gateway.CreateCheckout(Request(otherReservationId, 2500)).SessionReference);

            now = now.AddSeconds(29);
            var lagging = Assert.Throws<SaferpayApiException>(() => gateway.GetAuthorization(checkout.AuthorizationReference));
            Assert.Equal("inquire_not_found", lagging.Code);
            Assert.True(lagging.IsTransient);
            Assert.Null(gateway.FindAuthorizationByReservation(reservationId));

            // Capture and Cancel act on live data even while Inquire lags.
            var captured = gateway.Capture(checkout.AuthorizationReference, 1000, "eur", $"capture:{reservationId}:1000");
            Assert.Equal(PaymentAuthorizationState.Captured, captured.State);
            Assert.Equal(PaymentAuthorizationState.Canceled, gateway.Cancel(other.AuthorizationReference, $"cancel:{otherReservationId}").State);

            now = now.AddSeconds(2);
            var inquired = gateway.GetAuthorization(checkout.AuthorizationReference);
            Assert.Equal(PaymentAuthorizationState.Captured, inquired.State);
            Assert.Equal(1000, inquired.AmountReceivedMinor);
            Assert.Equal(PaymentAuthorizationState.Captured, gateway.FindAuthorizationByReservation(reservationId)!.State);
        }

        [Fact]
        public void MockInquireNotFoundSeconds_LagMakesCaptureReplayStateUnknown()
        {
            // TRANSACTION_ALREADY_CAPTURED while Inquire cannot see the transaction yet: the gateway must not invent a capture.
            var now = new DateTime(2026, 10, 20, 10, 0, 0, DateTimeKind.Utc);
            var (gateway, _) = CreateMock(o => o.MockInquireNotFoundSeconds = 60, () => now);
            var reservationId = Guid.NewGuid();
            var checkout = gateway.GetCheckout(gateway.CreateCheckout(Request(reservationId, 2500)).SessionReference);
            gateway.Capture(checkout.AuthorizationReference, 1000, "eur", $"capture:{reservationId}:1000");

            var exception = Assert.Throws<SaferpayApiException>(() =>
                gateway.Capture(checkout.AuthorizationReference, 1000, "eur", $"capture:{reservationId}:1000"));

            Assert.Equal("capture_state_unknown", exception.Code);
            Assert.True(exception.IsTransient);
        }

        [Fact]
        public async Task Handler_IsThreadSafe()
        {
            var (gateway, handler) = CreateMock();

            var tasks = Enumerable.Range(0, 24).Select(index => Task.Run(() =>
            {
                var reservationId = Guid.NewGuid();
                var session = gateway.CreateCheckout(Request(reservationId, 1000 + index));
                var checkout = gateway.GetCheckout(session.SessionReference);
                var captured = gateway.Capture(checkout.AuthorizationReference, 500, "eur", $"capture:{reservationId}:500");
                return (session.SessionReference, checkout.AuthorizationReference, captured.State);
            })).ToList();
            var results = await Task.WhenAll(tasks);

            Assert.Equal(24, results.Select(result => result.SessionReference).Distinct().Count());
            Assert.Equal(24, results.Select(result => result.AuthorizationReference).Distinct().Count());
            Assert.All(results, result => Assert.Equal(PaymentAuthorizationState.Captured, result.State));
            Assert.Equal(24 * 3, handler.RequestCount);
        }

        [Fact]
        public void Dispose_KeepsMockState()
        {
            var (gateway, handler) = CreateMock();
            var session = gateway.CreateCheckout(Request(Guid.NewGuid()));

            handler.Dispose();

            Assert.Equal(PaymentCheckoutState.Complete, gateway.GetCheckout(session.SessionReference).State);
        }

        [Theory]
        [InlineData(
            "https://portal.example.com/Payments/Success?reservationId=abc&session_id=xyz&origin=public",
            "https://portal.example.com/Payments/Cancel?reservationId=abc&origin=public")]
        [InlineData(
            "https://portal.example.com/Payments/Success?session_id=xyz",
            "https://portal.example.com/Payments/Cancel")]
        [InlineData(
            "https://portal.example.com/payments/success?reservationId=abc#top",
            "https://portal.example.com/Payments/Cancel?reservationId=abc#top")]
        [InlineData(
            "https://portal.example.com/Payments/Return?reservationId=abc",
            "https://portal.example.com/Payments/Return?reservationId=abc")]
        [InlineData("", "")]
        public void DeriveMockCancelUrl_ReplacesSuccessPathAndDropsSessionId(string returnUrl, string expected)
        {
            Assert.Equal(expected, MockSaferpayApiHandler.DeriveMockCancelUrl(returnUrl));
        }

        [Fact]
        public void AddSaferpayPaymentGateway_InMockMode_ResolvesGatewayUsingMockHandler()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSaferpayPaymentGateway(Configuration(new Dictionary<string, string?>
            {
                ["Saferpay:Enabled"] = "true",
                ["Saferpay:UseMockServices"] = "true",
                ["Saferpay:TimeoutSeconds"] = "7",
                ["Saferpay:MockPayerEmail"] = "di.payer@example.com"
            }));
            using var provider = services.BuildServiceProvider();

            var gateway = Assert.Single(provider.GetServices<IPaymentGateway>());
            Assert.IsType<SaferpayPaymentGateway>(gateway);
            Assert.Equal(PaymentProviderNames.Saferpay, gateway.ProviderName);
            Assert.True(gateway.IsConfigured);

            var httpClient = provider.GetRequiredService<IHttpClientFactory>().CreateClient(SaferpayApiClient.HttpClientName);
            Assert.Equal(TimeSpan.FromSeconds(7), httpClient.Timeout);

            var reservationId = Guid.NewGuid();
            var session = gateway.CreateCheckout(Request(reservationId));
            var checkout = gateway.GetCheckout(session.SessionReference);
            var captured = gateway.Capture(checkout.AuthorizationReference, 1000, "eur", $"capture:{reservationId}:1000");

            Assert.StartsWith("mock_tok_", session.SessionReference);
            Assert.Equal("di.payer@example.com", checkout.CustomerEmail);
            Assert.Equal(PaymentAuthorizationState.Captured, captured.State);
            Assert.Equal(3, provider.GetRequiredService<MockSaferpayApiHandler>().RequestCount);
        }

        [Fact]
        public void AddSaferpayPaymentGateway_IsIdempotent_AndRegistersNothingForStripe()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            var configuration = Configuration(new Dictionary<string, string?>());

            services.AddSaferpayPaymentGateway(configuration);
            services.AddSaferpayPaymentGateway(configuration);
            using var provider = services.BuildServiceProvider();

            var gateway = Assert.Single(provider.GetServices<IPaymentGateway>());
            Assert.False(gateway.IsConfigured);
            Assert.Equal(SaferpayOptions.TestBaseUrl, provider.GetRequiredService<IOptions<SaferpayOptions>>().Value.BaseUrl);
            Assert.Equal(TimeSpan.FromSeconds(100), provider.GetRequiredService<IHttpClientFactory>().CreateClient(SaferpayApiClient.HttpClientName).Timeout);
            Assert.DoesNotContain(services, descriptor =>
                (descriptor.ServiceType.FullName ?? string.Empty).Contains("Stripe", StringComparison.OrdinalIgnoreCase) ||
                ((descriptor.IsKeyedService ? descriptor.KeyedImplementationType : descriptor.ImplementationType)?.FullName ?? string.Empty)
                    .Contains("Stripe", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void AddSaferpayPaymentGateway_WithoutMock_UsesNetworkHandler()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSaferpayPaymentGateway(Configuration(new Dictionary<string, string?>
            {
                ["Saferpay:Enabled"] = "true",
                // https: outside mock mode an http base URL leaves the gateway unconfigured (credentials are never sent unencrypted).
                ["Saferpay:BaseUrl"] = "https://127.0.0.1:9/api",
                ["Saferpay:CustomerId"] = "123456",
                ["Saferpay:TerminalId"] = "17654321",
                ["Saferpay:ApiUsername"] = "API_123456_87654321",
                ["Saferpay:ApiPassword"] = "TestOnlyApiPassword0123456789",
                ["Saferpay:TimeoutSeconds"] = "5"
            }));
            using var provider = services.BuildServiceProvider();
            var gateway = provider.GetServices<IPaymentGateway>().Single();

            var exception = Assert.Throws<SaferpayApiException>(() => gateway.GetAuthorization("723n4MAjMdhjSAhAKEUdA8jtl9jb"));

            Assert.True(exception.Code == "network_error" || exception.Code == "timeout", exception.Code);
            Assert.True(exception.IsTransient);
            Assert.Equal(0, provider.GetRequiredService<MockSaferpayApiHandler>().RequestCount);
        }

        private static IConfiguration Configuration(Dictionary<string, string?> values) =>
            new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        private static JObject InitializeBody(long amount, string orderId = "order-1") =>
            new JObject
            {
                ["RequestHeader"] = new JObject
                {
                    ["SpecVersion"] = "1.54",
                    ["CustomerId"] = "123456",
                    ["RequestId"] = "req-1",
                    ["RetryIndicator"] = 0
                },
                ["TerminalId"] = "17654321",
                ["Payment"] = new JObject
                {
                    ["Amount"] = new JObject { ["Value"] = amount.ToString(), ["CurrencyCode"] = "EUR" },
                    ["OrderId"] = orderId,
                    ["Description"] = "Test"
                },
                ["ReturnUrl"] = new JObject { ["Url"] = "https://portal.example.com/Payments/Success?reservationId=1" }
            };

        private static JObject Post(HttpClient client, string path, JObject body)
        {
            using var response = Send(client, path, body);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return JObject.Parse(SaferpayTestData.ReadBody(response));
        }

        private static HttpResponseMessage Send(HttpClient client, string path, JObject body)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "https://test.saferpay.com/api" + path)
            {
                Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json")
            };
            return client.Send(request);
        }

        private static Dictionary<string, string> ParseQuery(Uri uri)
        {
            var query = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                query[Uri.UnescapeDataString(parts[0])] = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
            }

            return query;
        }
    }
}
