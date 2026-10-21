using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Newtonsoft.Json.Linq;
using OCPP.Core.Server.Payments.Providers;
using OCPP.Core.Server.Payments.Providers.Saferpay;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    public class SaferpayPaymentGatewayTests
    {
        private static readonly Guid ReservationId = Guid.Parse("3f2504e0-4f89-11d3-9a0c-0305e82c3301");

        private static PaymentCheckoutRequest CheckoutRequest(Action<PaymentCheckoutRequest>? configure = null)
        {
            var request = new PaymentCheckoutRequest
            {
                ReservationId = ReservationId,
                ChargePointId = "CP-01",
                ConnectorId = 2,
                AmountMinor = 2500,
                Currency = "eur",
                ProductName = "Charging session CP-01",
                SuccessUrl = SaferpayTestData.ReturnUrl,
                CancelUrl = "https://portal.example.com/Payments/Cancel",
                CustomerEmail = "payer@example.com",
                Metadata = new Dictionary<string, string> { ["reservation_id"] = ReservationId.ToString() },
                IdempotencyKey = $"checkout_create:{ReservationId}"
            };
            configure?.Invoke(request);
            return request;
        }

        private static HttpResponseMessageFactory InitializeResponse(
            string token = SaferpayTestData.Token,
            string redirectUrl = "https://saferpay.example.com/vt2/api/PaymentPage/123456/17654321/token",
            string expiration = "2026-10-20T12:45:22.258+01:00") =>
            new HttpResponseMessageFactory(new JObject
            {
                ["ResponseHeader"] = SaferpayTestData.ResponseHeader(),
                ["Token"] = token,
                ["Expiration"] = expiration,
                ["RedirectUrl"] = redirectUrl
            });

        [Fact]
        public void Properties_DescribeSaferpayCapabilities()
        {
            var gateway = SaferpayTestData.Gateway(new SaferpayRecordingHandler());

            Assert.Equal(PaymentProviderNames.Saferpay, gateway.ProviderName);
            Assert.Null(gateway.SuccessUrlSessionPlaceholder);
            Assert.False(gateway.SupportsMetadata);
            Assert.False(gateway.SupportsDeferredCheckoutLookup);
            Assert.True(gateway.IsConfigured);
            Assert.Throws<NotSupportedException>(() => gateway.MirrorMetadata("token", null, _ => { }));
        }

        [Theory]
        [InlineData(true, false, true, true, true, true, true, true)]
        [InlineData(false, false, true, true, true, true, true, false)]
        [InlineData(false, true, true, true, true, true, true, false)]
        [InlineData(true, true, false, false, false, false, false, true)]
        [InlineData(true, false, false, true, true, true, true, false)]
        [InlineData(true, false, true, false, true, true, true, false)]
        [InlineData(true, false, true, true, false, true, true, false)]
        [InlineData(true, false, true, true, true, false, true, false)]
        [InlineData(true, false, true, true, true, true, false, false)]
        public void IsConfigured_RequiresEnabledAndCredentialsUnlessMocked(
            bool enabled,
            bool useMock,
            bool hasBaseUrl,
            bool hasCustomerId,
            bool hasTerminalId,
            bool hasUsername,
            bool hasPassword,
            bool expected)
        {
            var options = SaferpayTestData.Options(o =>
            {
                o.Enabled = enabled;
                o.UseMockServices = useMock;
                o.BaseUrl = hasBaseUrl ? SaferpayTestData.BaseUrl : " ";
                o.CustomerId = hasCustomerId ? SaferpayTestData.CustomerId : null;
                o.TerminalId = hasTerminalId ? SaferpayTestData.TerminalId : "";
                o.ApiUsername = hasUsername ? SaferpayTestData.ApiUsername : null;
                o.ApiPassword = hasPassword ? SaferpayTestData.ApiPassword : null;
            });

            var gateway = SaferpayTestData.Gateway(new SaferpayRecordingHandler(), options);

            Assert.Equal(expected, gateway.IsConfigured);
        }

        [Fact]
        public void CreateCheckout_PostsPaymentPageInitialize_WithAllConfiguredFields()
        {
            var handler = new SaferpayRecordingHandler().Enqueue(InitializeResponse().Create());
            var options = SaferpayTestData.Options(o =>
            {
                o.PaymentMethods = new[] { "visa", " MASTERCARD ", "", "maestro" };
                o.Wallets = new[] { "applepay" };
                o.LanguageCode = "hr";
                o.ConfigSet = "ev-charging";
                o.Condition = "three_ds_authentication_successful_or_attempted";
            });
            var gateway = SaferpayTestData.Gateway(handler, options);

            var session = gateway.CreateCheckout(CheckoutRequest());

            Assert.Equal(SaferpayTestData.Token, session.SessionReference);
            Assert.Equal("https://saferpay.example.com/vt2/api/PaymentPage/123456/17654321/token", session.RedirectUrl);
            Assert.Null(session.AuthorizationReference);
            Assert.Equal(new DateTime(2026, 10, 20, 11, 45, 22, 258, DateTimeKind.Utc), session.ExpiresAtUtc);
            Assert.Equal(DateTimeKind.Utc, session.ExpiresAtUtc!.Value.Kind);

            var request = Assert.Single(handler.Requests);
            Assert.Equal("/api/Payment/v1/PaymentPage/Initialize", request.Path);
            var json = request.Json!;
            Assert.Equal(SaferpayApiClient.BuildRequestId($"checkout_create:{ReservationId}"), (string?)json["RequestHeader"]!["RequestId"]);
            Assert.Equal(SaferpayTestData.CustomerId, (string?)json["RequestHeader"]!["CustomerId"]);
            Assert.Equal(SaferpayTestData.TerminalId, (string?)json["TerminalId"]);

            var amountValue = json["Payment"]!["Amount"]!["Value"]!;
            Assert.Equal(JTokenType.String, amountValue.Type);
            Assert.Equal("2500", (string?)amountValue);
            Assert.Equal("EUR", (string?)json["Payment"]!["Amount"]!["CurrencyCode"]);
            Assert.Equal(ReservationId.ToString("D"), (string?)json["Payment"]!["OrderId"]);
            Assert.Equal("Charging session CP-01", (string?)json["Payment"]!["Description"]);
            Assert.True((bool)json["Payment"]!["Options"]!["PreAuth"]!);

            Assert.Equal(new[] { "VISA", "MASTERCARD", "MAESTRO" }, json["PaymentMethods"]!.Values<string>());
            Assert.Equal(new[] { "APPLEPAY" }, json["Wallets"]!.Values<string>());
            Assert.Equal("ev-charging", (string?)json["ConfigSet"]);
            Assert.Equal("THREE_DS_AUTHENTICATION_SUCCESSFUL_OR_ATTEMPTED", (string?)json["Condition"]);
            Assert.Equal("hr", (string?)json["Payer"]!["LanguageCode"]);
            Assert.Equal("payer@example.com", (string?)json["Payer"]!["BillingAddress"]!["Email"]);
            Assert.Equal(SaferpayTestData.ReturnUrl, (string?)json["ReturnUrl"]!["Url"]);

            Assert.Equal("SAFERPAY", (string?)json["BillingAddressForm"]!["AddressSource"]);
            Assert.Equal(new[] { "EMAIL" }, json["BillingAddressForm"]!["MandatoryFields"]!.Values<string>());

            Assert.Null(json["Metadata"]);
            Assert.DoesNotContain("reservation_id", request.Body);
        }

        [Fact]
        public void CreateCheckout_SendsSignedNotificationUrls_ThatParseWebhookAccepts()
        {
            var handler = new SaferpayRecordingHandler().Enqueue(InitializeResponse().Create());
            var gateway = SaferpayTestData.Gateway(handler);

            gateway.CreateCheckout(CheckoutRequest());

            var notification = (JObject)handler.LastRequest.Json!["Notification"]!;
            string successUrl = (string)notification["SuccessNotifyUrl"]!;
            string failUrl = (string)notification["FailNotifyUrl"]!;
            var signer = new SaferpayNotificationSigner(SaferpayTestData.Options());
            string signature = signer.Sign(ReservationId);

            Assert.Equal(
                $"https://ocpp.example.com/API/Payments/Webhook/Saferpay?reservationId={ReservationId:D}&sig={signature}",
                successUrl);
            Assert.Equal(successUrl + "&outcome=fail", failUrl);

            var success = gateway.ParseWebhook(WebhookFromUrl(successUrl));
            var fail = gateway.ParseWebhook(WebhookFromUrl(failUrl));

            Assert.True(success.Accepted);
            Assert.Equal("saferpay.notify.success", Assert.Single(success.Events).EventType);
            Assert.True(fail.Accepted);
            Assert.Equal("saferpay.notify.fail", Assert.Single(fail.Events).EventType);
        }

        [Fact]
        public void CreateCheckout_OmitsOptionalBlocks_WhenNotConfigured()
        {
            var handler = new SaferpayRecordingHandler().Enqueue(InitializeResponse().Create());
            var options = SaferpayTestData.Options(o =>
            {
                o.PreAuth = false;
                o.NotifyBaseUrl = null;
                o.CollectPayerEmail = false;
            });
            var gateway = SaferpayTestData.Gateway(handler, options);

            gateway.CreateCheckout(CheckoutRequest(r =>
            {
                r.ProductName = null;
                r.CustomerEmail = null;
                r.IdempotencyKey = null;
            }));

            var json = handler.LastRequest.Json!;
            Assert.Null(json["Payment"]!["Options"]);
            Assert.Equal(SaferpayPaymentGateway.DefaultDescription, (string?)json["Payment"]!["Description"]);
            Assert.Null(json["Notification"]);
            Assert.Null(json["BillingAddressForm"]);
            Assert.Null(json["Payer"]);
            // Without a configured filter only the deferred-capture card brands are offered (never every terminal method).
            Assert.Equal(new[] { "VISA", "MASTERCARD", "MAESTRO" }, json["PaymentMethods"]!.Values<string>());
            Assert.Null(json["Wallets"]);
            Assert.Null(json["ConfigSet"]);
            Assert.Null(json["Condition"]);
            Assert.Matches("^[0-9a-f]{32}$", (string)json["RequestHeader"]!["RequestId"]!);
        }

        [Theory]
        [InlineData("not-an-email")]
        [InlineData("two@@example.com")]
        [InlineData("with space@example.com")]
        [InlineData("@example.com")]
        public void CreateCheckout_DoesNotPrefillImplausibleEmail(string email)
        {
            var handler = new SaferpayRecordingHandler().Enqueue(InitializeResponse().Create());
            var gateway = SaferpayTestData.Gateway(handler);

            gateway.CreateCheckout(CheckoutRequest(r => r.CustomerEmail = email));

            Assert.Null(handler.LastRequest.Json!["Payer"]);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void CreateCheckout_RejectsNonPositiveAmount_WithoutCallingSaferpay(long amount)
        {
            var handler = new SaferpayRecordingHandler();
            var gateway = SaferpayTestData.Gateway(handler);

            Assert.Throws<ArgumentOutOfRangeException>(() => gateway.CreateCheckout(CheckoutRequest(r => r.AmountMinor = amount)));
            Assert.Empty(handler.Requests);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("eu")]
        [InlineData("e1r")]
        public void CreateCheckout_RejectsInvalidCurrency(string? currency)
        {
            var handler = new SaferpayRecordingHandler();
            var gateway = SaferpayTestData.Gateway(handler);

            Assert.Throws<ArgumentException>(() => gateway.CreateCheckout(CheckoutRequest(r => r.Currency = currency!)));
            Assert.Empty(handler.Requests);
        }

        [Fact]
        public void CreateCheckout_WhenNotConfigured_ThrowsProviderException_WithoutCallingSaferpay()
        {
            var handler = new SaferpayRecordingHandler();
            var gateway = SaferpayTestData.Gateway(handler, SaferpayTestData.Options(o => o.Enabled = false));

            var exception = Assert.Throws<SaferpayApiException>(() => gateway.CreateCheckout(CheckoutRequest()));

            Assert.Equal("not_configured", exception.Code);
            Assert.False(exception.IsTransient);
            Assert.Empty(handler.Requests);
        }

        [Fact]
        public void CreateCheckout_ResponseWithoutToken_IsInvalidResponse()
        {
            var handler = new SaferpayRecordingHandler()
                .Enqueue(SaferpayTestData.Json(HttpStatusCode.OK, new JObject { ["RedirectUrl"] = "https://saferpay.example.com/vt2" }));
            var gateway = SaferpayTestData.Gateway(handler);

            var exception = Assert.Throws<SaferpayApiException>(() => gateway.CreateCheckout(CheckoutRequest()));

            Assert.Equal("invalid_response", exception.Code);
        }

        [Fact]
        public void GetCheckout_Authorized_IsCompleteAndPaid_WithInlineAuthorizationAndPayer()
        {
            var payer = new JObject
            {
                ["BillingAddress"] = new JObject
                {
                    ["FirstName"] = "Max",
                    ["LastName"] = "Mustermann",
                    ["Email"] = "max@example.com"
                }
            };
            var handler = new SaferpayRecordingHandler()
                .Enqueue(SaferpayTestData.TransactionResponse("AUTHORIZED", 2500, ReservationId.ToString("D"), payer: payer));
            var gateway = SaferpayTestData.Gateway(handler);

            var status = gateway.GetCheckout(SaferpayTestData.Token);

            var request = Assert.Single(handler.Requests);
            Assert.Equal("/api/Payment/v1/PaymentPage/Assert", request.Path);
            Assert.Equal(SaferpayTestData.Token, (string?)request.Json!["Token"]);

            Assert.Equal(SaferpayTestData.Token, status.SessionReference);
            Assert.Equal(PaymentCheckoutState.Complete, status.State);
            Assert.True(status.IsPaid);
            Assert.Equal("AUTHORIZED", status.RawStatus);
            Assert.Equal(SaferpayTestData.TransactionId, status.AuthorizationReference);
            Assert.Equal("max@example.com", status.CustomerEmail);
            Assert.Equal("Max Mustermann", status.CustomerName);
            Assert.Equal(ReservationId, status.OwnerReservationId);
            Assert.NotNull(status.Metadata);
            Assert.Empty(status.Metadata);
            Assert.Null(status.ResumeUrl);
            Assert.Null(status.FailureCode);

            var authorization = status.Authorization;
            Assert.NotNull(authorization);
            Assert.Equal(PaymentAuthorizationState.RequiresCapture, authorization.State);
            Assert.Equal(SaferpayTestData.TransactionId, authorization.AuthorizationReference);
            Assert.Equal(2500, authorization.AmountMinor);
            Assert.Equal(2500, authorization.AmountCapturableMinor);
            Assert.Equal(0, authorization.AmountReceivedMinor);
            Assert.Equal("eur", authorization.Currency);
            Assert.True(authorization.IsCancelable);
            Assert.Equal(ReservationId, authorization.OwnerReservationId);
        }

        [Fact]
        public void GetCheckout_Captured_IsCompleteAndPaid()
        {
            var handler = new SaferpayRecordingHandler()
                .Enqueue(SaferpayTestData.TransactionResponse("CAPTURED", 1800, ReservationId.ToString("D"), captureId: SaferpayTestData.TransactionId + "_c"));
            var gateway = SaferpayTestData.Gateway(handler);

            var status = gateway.GetCheckout(SaferpayTestData.Token);

            Assert.Equal(PaymentCheckoutState.Complete, status.State);
            Assert.True(status.IsPaid);
            Assert.Equal(PaymentAuthorizationState.Captured, status.Authorization.State);
            Assert.Equal(1800, status.Authorization.AmountReceivedMinor);
            Assert.Equal(0, status.Authorization.AmountCapturableMinor);
            Assert.False(status.Authorization.IsCancelable);
            Assert.Equal(SaferpayTestData.TransactionId + "_c", status.Authorization.CaptureReference);
        }

        [Fact]
        public void GetCheckout_Pending_IsOpen_WithPendingAuthorization()
        {
            var handler = new SaferpayRecordingHandler().Enqueue(SaferpayTestData.TransactionResponse("PENDING"));
            var gateway = SaferpayTestData.Gateway(handler);

            var status = gateway.GetCheckout(SaferpayTestData.Token);

            Assert.Equal(PaymentCheckoutState.Open, status.State);
            Assert.False(status.IsPaid);
            Assert.Equal(PaymentAuthorizationState.Pending, status.Authorization.State);
            Assert.Equal(SaferpayTestData.TransactionId, status.AuthorizationReference);
        }

        [Fact]
        public void GetCheckout_CanceledTransaction_IsCompleteButNotPaid()
        {
            var handler = new SaferpayRecordingHandler().Enqueue(SaferpayTestData.TransactionResponse("CANCELED"));
            var gateway = SaferpayTestData.Gateway(handler);

            var status = gateway.GetCheckout(SaferpayTestData.Token);

            Assert.Equal(PaymentCheckoutState.Complete, status.State);
            Assert.False(status.IsPaid);
            Assert.Equal(PaymentAuthorizationState.Canceled, status.Authorization.State);
        }

        [Fact]
        public void GetCheckout_WithoutBillingName_FallsBackToCardHolder_AndPayPalEmail()
        {
            var body = JObject.Parse(SaferpayTestData.ReadBody(SaferpayTestData.TransactionResponse("AUTHORIZED", holderName: "Ana Horvat")));
            body["PaymentMeans"]!["PayPal"] = new JObject { ["Email"] = "paypal@example.com" };
            var handler = new SaferpayRecordingHandler().Enqueue(SaferpayTestData.Json(HttpStatusCode.OK, body));
            var gateway = SaferpayTestData.Gateway(handler);

            var status = gateway.GetCheckout(SaferpayTestData.Token);

            Assert.Equal("Ana Horvat", status.CustomerName);
            Assert.Equal("paypal@example.com", status.CustomerEmail);
            Assert.Null(status.OwnerReservationId);
        }

        [Fact]
        public void GetCheckout_TransactionWithoutAmount_IsInvalidResponse()
        {
            var body = JObject.Parse(SaferpayTestData.ReadBody(SaferpayTestData.TransactionResponse("AUTHORIZED")));
            ((JObject)body["Transaction"]!).Remove("Amount");
            var handler = new SaferpayRecordingHandler().Enqueue(SaferpayTestData.Json(HttpStatusCode.OK, body));
            var gateway = SaferpayTestData.Gateway(handler);

            var exception = Assert.Throws<SaferpayApiException>(() => gateway.GetCheckout(SaferpayTestData.Token));

            Assert.Equal("invalid_response", exception.Code);
        }

        [Theory]
        [InlineData("TRANSACTION_ABORTED", PaymentCheckoutState.Canceled)]
        [InlineData("TRANSACTION_IN_WRONG_STATE", PaymentCheckoutState.Open)]
        [InlineData("TRANSACTION_NOT_STARTED", PaymentCheckoutState.Open)]
        public void GetCheckout_MapsNonPaymentAssertErrors(string errorName, PaymentCheckoutState expectedState)
        {
            var handler = new SaferpayRecordingHandler()
                .Enqueue(SaferpayTestData.Error(HttpStatusCode.PaymentRequired, errorName, orderId: ReservationId.ToString("D"), transactionId: SaferpayTestData.TransactionId));
            var gateway = SaferpayTestData.Gateway(handler);

            var status = gateway.GetCheckout(SaferpayTestData.Token);

            Assert.Equal(expectedState, status.State);
            Assert.False(status.IsPaid);
            Assert.Equal(errorName, status.RawStatus);
            Assert.Equal(SaferpayTestData.Token, status.SessionReference);
            Assert.Null(status.AuthorizationReference);
            Assert.Null(status.Authorization);
            Assert.Null(status.FailureCode);
            Assert.Equal(ReservationId, status.OwnerReservationId);
        }

        [Fact]
        public void GetCheckout_TokenInvalid_ReturnsNull()
        {
            var handler = new SaferpayRecordingHandler()
                .Enqueue(SaferpayTestData.Error(HttpStatusCode.BadRequest, "TOKEN_INVALID"));
            var gateway = SaferpayTestData.Gateway(handler);

            Assert.Null(gateway.GetCheckout("unknown-token"));
        }

        [Theory]
        [InlineData("TRANSACTION_DECLINED", "DO_NOT_RETRY", "Insufficient funds")]
        [InlineData("TRANSACTION_DECLINED", "RETRY_LATER", null)]
        [InlineData("3DS_AUTHENTICATION_FAILED", "DO_NOT_RETRY", "Card holder information -> Failed")]
        [InlineData("CONDITION_NOT_SATISFIED", "DO_NOT_RETRY", null)]
        [InlineData("BLOCKED_BY_RISK_MANAGEMENT", "DO_NOT_RETRY", null)]
        [InlineData("CARD_CVC_INVALID", "OTHER_MEANS", null)]
        [InlineData("CARD_EXPIRED", "DO_NOT_RETRY", null)]
        [InlineData("PAYMENTMEANS_INVALID", "OTHER_MEANS", null)]
        [InlineData("PAYMENTMEANS_NOT_SUPPORTED", "OTHER_MEANS", null)]
        [InlineData("GENERAL_DECLINED", "DO_NOT_RETRY", null)]
        [InlineData("NO_CREDITS_AVAILABLE", "DO_NOT_RETRY", null)]
        [InlineData("PAYER_AUTHENTICATION_REQUIRED", "DO_NOT_RETRY", null)]
        [InlineData("SOME_FUTURE_DECLINE", "DO_NOT_RETRY", null)]
        public void GetCheckout_PaymentDeclines_AreFailed(string errorName, string behavior, string? payerMessage)
        {
            var handler = new SaferpayRecordingHandler()
                .Enqueue(SaferpayTestData.Error(
                    HttpStatusCode.PaymentRequired,
                    errorName,
                    behavior,
                    errorMessage: "Transaction declined by acquirer",
                    payerMessage: payerMessage,
                    orderId: ReservationId.ToString("D"),
                    transactionId: SaferpayTestData.TransactionId));
            var gateway = SaferpayTestData.Gateway(handler);

            var status = gateway.GetCheckout(SaferpayTestData.Token);

            Assert.Equal(PaymentCheckoutState.Failed, status.State);
            Assert.False(status.IsPaid);
            Assert.Equal(errorName, status.FailureCode);
            Assert.Equal(errorName, status.RawStatus);
            Assert.Equal(payerMessage ?? "Transaction declined by acquirer", status.FailureMessage);
            Assert.Equal(ReservationId, status.OwnerReservationId);
            Assert.Null(status.AuthorizationReference);
        }

        [Theory]
        [InlineData(HttpStatusCode.Unauthorized, "AUTHENTICATION_FAILED", "DO_NOT_RETRY", false)]
        [InlineData(HttpStatusCode.Forbidden, "PERMISSION_DENIED", "DO_NOT_RETRY", false)]
        [InlineData(HttpStatusCode.BadRequest, "VALIDATION_FAILED", "DO_NOT_RETRY", false)]
        [InlineData(HttpStatusCode.BadRequest, "ACTION_NOT_SUPPORTED", "DO_NOT_RETRY", false)]
        [InlineData(HttpStatusCode.PaymentRequired, "COMMUNICATION_FAILED", "RETRY", true)]
        [InlineData(HttpStatusCode.PaymentRequired, "COMMUNICATION_TIMEOUT", "DO_NOT_RETRY", true)]
        [InlineData(HttpStatusCode.InternalServerError, "INTERNAL_ERROR", "RETRY", true)]
        [InlineData(HttpStatusCode.PaymentRequired, "SOME_NEW_ERROR", "RETRY", true)]
        [InlineData(HttpStatusCode.BadRequest, "SOME_NEW_ERROR", "DO_NOT_RETRY", false)]
        public void GetCheckout_MerchantConfigAndTransientErrors_Throw(
            HttpStatusCode statusCode,
            string errorName,
            string behavior,
            bool expectedTransient)
        {
            var handler = new SaferpayRecordingHandler().Enqueue(SaferpayTestData.Error(statusCode, errorName, behavior));
            var gateway = SaferpayTestData.Gateway(handler);

            var exception = Assert.Throws<SaferpayApiException>(() => gateway.GetCheckout(SaferpayTestData.Token));

            Assert.Equal(errorName, exception.Code);
            Assert.Equal(expectedTransient, exception.IsTransient);
        }

        [Fact]
        public void GetCheckout_NonJsonServerError_ThrowsTransient()
        {
            var handler = new SaferpayRecordingHandler().Enqueue(SaferpayTestData.Text(HttpStatusCode.ServiceUnavailable, "<html/>"));
            var gateway = SaferpayTestData.Gateway(handler);

            var exception = Assert.Throws<SaferpayApiException>(() => gateway.GetCheckout(SaferpayTestData.Token));

            Assert.Equal("http_503", exception.Code);
            Assert.True(exception.IsTransient);
        }

        [Fact]
        public void GetAuthorization_PostsInquire_AndMapsAuthorizedTransaction()
        {
            var handler = new SaferpayRecordingHandler()
                .Enqueue(SaferpayTestData.TransactionResponse("AUTHORIZED", 3000, ReservationId.ToString("D")));
            var gateway = SaferpayTestData.Gateway(handler);

            var snapshot = gateway.GetAuthorization(SaferpayTestData.TransactionId);

            var request = Assert.Single(handler.Requests);
            Assert.Equal("/api/Payment/v1/Transaction/Inquire", request.Path);
            Assert.Equal(SaferpayTestData.TransactionId, (string?)request.Json!["TransactionReference"]!["TransactionId"]);
            Assert.Null(request.Json["TransactionReference"]!["OrderId"]);
            Assert.Equal(PaymentAuthorizationState.RequiresCapture, snapshot.State);
            Assert.Equal(3000, snapshot.AmountMinor);
            Assert.Equal(3000, snapshot.AmountCapturableMinor);
            Assert.Equal("eur", snapshot.Currency);
            Assert.Equal(ReservationId, snapshot.OwnerReservationId);
            Assert.Empty(snapshot.Metadata);
        }

        [Theory]
        [InlineData("AUTHORIZED", PaymentAuthorizationState.RequiresCapture, 2500, 0, true)]
        [InlineData("CAPTURED", PaymentAuthorizationState.Captured, 0, 2500, false)]
        [InlineData("CANCELED", PaymentAuthorizationState.Canceled, 0, 0, false)]
        [InlineData("CANCELLED", PaymentAuthorizationState.Canceled, 0, 0, false)]
        [InlineData("PENDING", PaymentAuthorizationState.Pending, 0, 0, false)]
        [InlineData("SOMETHING_ELSE", PaymentAuthorizationState.Unknown, 0, 0, false)]
        public void GetAuthorization_MapsTransactionStatus(
            string status,
            PaymentAuthorizationState expectedState,
            long expectedCapturable,
            long expectedReceived,
            bool expectedCancelable)
        {
            var handler = new SaferpayRecordingHandler()
                .Enqueue(SaferpayTestData.TransactionResponse(status, 2500, captureId: status == "CAPTURED" ? "cap_1" : null));
            var gateway = SaferpayTestData.Gateway(handler);

            var snapshot = gateway.GetAuthorization(SaferpayTestData.TransactionId);

            Assert.Equal(expectedState, snapshot.State);
            Assert.Equal(status, snapshot.RawStatus);
            Assert.Equal(2500, snapshot.AmountMinor);
            Assert.Equal(expectedCapturable, snapshot.AmountCapturableMinor);
            Assert.Equal(expectedReceived, snapshot.AmountReceivedMinor);
            Assert.Equal(expectedCancelable, snapshot.IsCancelable);
            Assert.Equal(status == "CAPTURED" ? "cap_1" : null, snapshot.CaptureReference);
        }

        [Fact]
        public void GetAuthorization_TransactionNotFound_IsTransientInquireNotFound()
        {
            // Inquire lists only successful transactions and lags behind Assert, so NOT_FOUND for a transaction id we
            // got from Assert is indeterminate; returning null would let callers skip releasing a real hold.
            var handler = new SaferpayRecordingHandler()
                .Enqueue(SaferpayTestData.Error(HttpStatusCode.PaymentRequired, "TRANSACTION_NOT_FOUND"));
            var gateway = SaferpayTestData.Gateway(handler);

            var exception = Assert.Throws<SaferpayApiException>(() => gateway.GetAuthorization(SaferpayTestData.TransactionId));

            Assert.Equal("inquire_not_found", exception.Code);
            Assert.True(exception.IsTransient);
            Assert.Equal(PaymentProviderNames.Saferpay, exception.Provider);
            Assert.Equal(402, exception.HttpStatusCode);
            Assert.Equal("TRANSACTION_NOT_FOUND", Assert.IsType<SaferpayApiException>(exception.InnerException).ErrorName);
            Assert.DoesNotContain(SaferpayTestData.TransactionId, exception.Message);
            Assert.Single(handler.Requests);
        }

        [Fact]
        public void Capture_PostsTransactionCapture_WithPartialAmount()
        {
            var handler = new SaferpayRecordingHandler().Enqueue(SaferpayTestData.Json(HttpStatusCode.OK, new JObject
            {
                ["ResponseHeader"] = SaferpayTestData.ResponseHeader(),
                ["CaptureId"] = SaferpayTestData.TransactionId + "_c",
                ["Status"] = "CAPTURED",
                ["Date"] = "2026-10-20T12:45:22.258+01:00"
            }));
            var gateway = SaferpayTestData.Gateway(handler);
            string key = $"capture:{ReservationId}:1245";

            var snapshot = gateway.Capture(SaferpayTestData.TransactionId, 1245, "eur", key);

            var request = Assert.Single(handler.Requests);
            Assert.Equal("/api/Payment/v1/Transaction/Capture", request.Path);
            Assert.Equal(SaferpayTestData.TransactionId, (string?)request.Json!["TransactionReference"]!["TransactionId"]);
            Assert.Equal(JTokenType.String, request.Json["Amount"]!["Value"]!.Type);
            Assert.Equal("1245", (string?)request.Json["Amount"]!["Value"]);
            Assert.Equal("EUR", (string?)request.Json["Amount"]!["CurrencyCode"]);
            Assert.Equal(SaferpayApiClient.BuildRequestId(key), (string?)request.Json["RequestHeader"]!["RequestId"]);

            Assert.Equal(PaymentAuthorizationState.Captured, snapshot.State);
            Assert.Equal(SaferpayTestData.TransactionId, snapshot.AuthorizationReference);
            Assert.Equal(1245, snapshot.AmountReceivedMinor);
            Assert.Equal(0, snapshot.AmountCapturableMinor);
            Assert.Equal("eur", snapshot.Currency);
            Assert.Equal(SaferpayTestData.TransactionId + "_c", snapshot.CaptureReference);
            Assert.False(snapshot.IsCancelable);
        }

        [Fact]
        public void Capture_PendingStatus_IsPending()
        {
            var handler = new SaferpayRecordingHandler().Enqueue(SaferpayTestData.Json(HttpStatusCode.OK, new JObject
            {
                ["CaptureId"] = "cap_pending",
                ["Status"] = "PENDING"
            }));
            var gateway = SaferpayTestData.Gateway(handler);

            var snapshot = gateway.Capture(SaferpayTestData.TransactionId, 500, "EUR", "capture:key");

            Assert.Equal(PaymentAuthorizationState.Pending, snapshot.State);
            Assert.Equal(0, snapshot.AmountReceivedMinor);
            Assert.Equal("cap_pending", snapshot.CaptureReference);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-10)]
        public void Capture_RejectsNonPositiveAmount_WithoutCallingSaferpay(long amount)
        {
            var handler = new SaferpayRecordingHandler();
            var gateway = SaferpayTestData.Gateway(handler);

            Assert.Throws<ArgumentOutOfRangeException>(() => gateway.Capture(SaferpayTestData.TransactionId, amount, "eur", "capture:key"));
            Assert.Empty(handler.Requests);
        }

        [Fact]
        public void Capture_AlreadyCaptured_ReturnsInquiredSnapshot()
        {
            var handler = new SaferpayRecordingHandler()
                .Enqueue(SaferpayTestData.Error(HttpStatusCode.PaymentRequired, "TRANSACTION_ALREADY_CAPTURED", errorMessage: "Transaction already captured"))
                .Enqueue(SaferpayTestData.TransactionResponse("CAPTURED", 1245, ReservationId.ToString("D"), captureId: "cap_1"));
            var gateway = SaferpayTestData.Gateway(handler);

            var snapshot = gateway.Capture(SaferpayTestData.TransactionId, 1245, "eur", "capture:key");

            Assert.Equal(new[] { "/api/Payment/v1/Transaction/Capture", "/api/Payment/v1/Transaction/Inquire" }, handler.Requests.Select(r => r.Path));
            Assert.Equal(PaymentAuthorizationState.Captured, snapshot.State);
            Assert.Equal(1245, snapshot.AmountReceivedMinor);
            Assert.Equal("cap_1", snapshot.CaptureReference);
            Assert.Equal(ReservationId, snapshot.OwnerReservationId);
        }

        [Fact]
        public void Capture_AlreadyCaptured_WithLaggingInquire_ReportsRequestedAmountAsUnverifiedCapture()
        {
            var handler = new SaferpayRecordingHandler()
                .Enqueue(SaferpayTestData.Error(HttpStatusCode.PaymentRequired, "TRANSACTION_ALREADY_CAPTURED"))
                .Enqueue(SaferpayTestData.TransactionResponse("AUTHORIZED", 2500, ReservationId.ToString("D")));
            var gateway = SaferpayTestData.Gateway(handler);

            var snapshot = gateway.Capture(SaferpayTestData.TransactionId, 1245, "eur", "capture:key");

            Assert.Equal(PaymentAuthorizationState.Captured, snapshot.State);
            Assert.Equal(SaferpayPaymentGateway.UnverifiedCaptureStatus, snapshot.RawStatus);
            Assert.Equal("CAPTURED_UNVERIFIED", snapshot.RawStatus);
            Assert.Equal(1245, snapshot.AmountReceivedMinor);
            Assert.Equal(2500, snapshot.AmountMinor);
            Assert.Equal(0, snapshot.AmountCapturableMinor);
            Assert.Equal("eur", snapshot.Currency);
            Assert.Equal(ReservationId, snapshot.OwnerReservationId);
            Assert.Null(snapshot.CaptureReference);
            Assert.False(snapshot.IsCancelable);
        }

        [Fact]
        public void Capture_AmountInvalid_ThrowsPermanentError()
        {
            var handler = new SaferpayRecordingHandler()
                .Enqueue(SaferpayTestData.Error(HttpStatusCode.PaymentRequired, "AMOUNT_INVALID", errorMessage: "Capture amount exceeds the authorized amount"));
            var gateway = SaferpayTestData.Gateway(handler);

            var exception = Assert.Throws<SaferpayApiException>(() => gateway.Capture(SaferpayTestData.TransactionId, 9999, "eur", "capture:key"));

            Assert.Equal("AMOUNT_INVALID", exception.Code);
            Assert.False(exception.IsTransient);
            Assert.Single(handler.Requests);
        }

        [Fact]
        public void Cancel_PostsTransactionCancel_AndReturnsCanceledSnapshot()
        {
            var handler = new SaferpayRecordingHandler().Enqueue(SaferpayTestData.Json(HttpStatusCode.OK, new JObject
            {
                ["ResponseHeader"] = SaferpayTestData.ResponseHeader(),
                ["TransactionId"] = SaferpayTestData.TransactionId,
                ["OrderId"] = ReservationId.ToString("D"),
                ["Date"] = "2026-10-20T12:45:22.258+01:00"
            }));
            var gateway = SaferpayTestData.Gateway(handler);
            string key = $"cancel:{ReservationId}";

            var snapshot = gateway.Cancel(SaferpayTestData.TransactionId, key);

            var request = Assert.Single(handler.Requests);
            Assert.Equal("/api/Payment/v1/Transaction/Cancel", request.Path);
            Assert.Equal(SaferpayTestData.TransactionId, (string?)request.Json!["TransactionReference"]!["TransactionId"]);
            Assert.Equal(SaferpayApiClient.BuildRequestId(key), (string?)request.Json["RequestHeader"]!["RequestId"]);
            Assert.Equal(PaymentAuthorizationState.Canceled, snapshot.State);
            Assert.Equal("CANCELED", snapshot.RawStatus);
            Assert.Equal(SaferpayTestData.TransactionId, snapshot.AuthorizationReference);
            Assert.Equal(0, snapshot.AmountCapturableMinor);
            Assert.False(snapshot.IsCancelable);
            Assert.Equal(ReservationId, snapshot.OwnerReservationId);
        }

        [Theory]
        [InlineData("TRANSACTION_IN_WRONG_STATE", "CANCELED")]
        [InlineData("TRANSACTION_IN_WRONG_STATE", "CANCELLED")]
        public void Cancel_AlreadyCanceledTransaction_ReturnsInquiredState(string errorName, string actualStatus)
        {
            var handler = new SaferpayRecordingHandler()
                .Enqueue(SaferpayTestData.Error(HttpStatusCode.PaymentRequired, errorName))
                .Enqueue(SaferpayTestData.TransactionResponse(actualStatus, 2500, ReservationId.ToString("D")));
            var gateway = SaferpayTestData.Gateway(handler);

            var snapshot = gateway.Cancel(SaferpayTestData.TransactionId, "cancel:key");

            Assert.Equal(new[] { "/api/Payment/v1/Transaction/Cancel", "/api/Payment/v1/Transaction/Inquire" }, handler.Requests.Select(r => r.Path));
            Assert.Equal(PaymentAuthorizationState.Canceled, snapshot.State);
            Assert.Equal(actualStatus, snapshot.RawStatus);
            Assert.False(snapshot.IsCancelable);
        }

        [Theory]
        [InlineData("TRANSACTION_ALREADY_CAPTURED")]
        [InlineData("TRANSACTION_IN_WRONG_STATE")]
        public void Cancel_RefusedForCapturedTransaction_ThrowsPermanentAlreadyCaptured(string errorName)
        {
            var handler = new SaferpayRecordingHandler()
                .Enqueue(SaferpayTestData.Error(HttpStatusCode.PaymentRequired, errorName))
                .Enqueue(SaferpayTestData.TransactionResponse("CAPTURED", 2500, ReservationId.ToString("D"), captureId: "cap_1"));
            var gateway = SaferpayTestData.Gateway(handler);

            var exception = Assert.Throws<SaferpayApiException>(() => gateway.Cancel(SaferpayTestData.TransactionId, "cancel:key"));

            Assert.Equal("transaction_already_captured", exception.Code);
            Assert.False(exception.IsTransient);
            Assert.Equal(PaymentProviderNames.Saferpay, exception.Provider);
            Assert.Equal(errorName, Assert.IsType<SaferpayApiException>(exception.InnerException).ErrorName);
            Assert.Equal(new[] { "/api/Payment/v1/Transaction/Cancel", "/api/Payment/v1/Transaction/Inquire" }, handler.Requests.Select(r => r.Path));
        }

        [Theory]
        [InlineData("TRANSACTION_IN_WRONG_STATE", "AUTHORIZED")]
        [InlineData("TRANSACTION_ALREADY_CAPTURED", "AUTHORIZED")]
        [InlineData("TRANSACTION_IN_WRONG_STATE", "PENDING")]
        [InlineData("TRANSACTION_IN_WRONG_STATE", "SOMETHING_ELSE")]
        [InlineData("TRANSACTION_IN_WRONG_STATE", null)]
        [InlineData("TRANSACTION_ALREADY_CAPTURED", null)]
        public void Cancel_RefusedAndInquireDoesNotShowCanceled_ThrowsTransientCancelStateUnknown(string errorName, string? inquiredStatus)
        {
            // A lagging Inquire (still AUTHORIZED), an unknown status or NOT_FOUND does not prove the hold was released.
            var handler = new SaferpayRecordingHandler()
                .Enqueue(SaferpayTestData.Error(HttpStatusCode.PaymentRequired, errorName))
                .Enqueue(inquiredStatus == null
                    ? SaferpayTestData.Error(HttpStatusCode.PaymentRequired, "TRANSACTION_NOT_FOUND")
                    : SaferpayTestData.TransactionResponse(inquiredStatus, 2500, ReservationId.ToString("D")));
            var gateway = SaferpayTestData.Gateway(handler);

            var exception = Assert.Throws<SaferpayApiException>(() => gateway.Cancel(SaferpayTestData.TransactionId, "cancel:key"));

            Assert.Equal("cancel_state_unknown", exception.Code);
            Assert.True(exception.IsTransient);
            Assert.Contains(errorName, exception.Message);
            Assert.Contains(inquiredStatus ?? "not found", exception.Message);
            Assert.DoesNotContain(SaferpayTestData.TransactionId, exception.Message);
            Assert.Equal(errorName, Assert.IsType<SaferpayApiException>(exception.InnerException).ErrorName);
            Assert.Equal(2, handler.Requests.Count);
        }

        [Fact]
        public void Cancel_OtherErrors_Throw()
        {
            var handler = new SaferpayRecordingHandler()
                .Enqueue(SaferpayTestData.Error(HttpStatusCode.InternalServerError, "INTERNAL_ERROR", "RETRY"));
            var gateway = SaferpayTestData.Gateway(handler);

            var exception = Assert.Throws<SaferpayApiException>(() => gateway.Cancel(SaferpayTestData.TransactionId, "cancel:key"));

            Assert.Equal("INTERNAL_ERROR", exception.Code);
            Assert.True(exception.IsTransient);
            Assert.Single(handler.Requests);
        }

        [Theory]
        [InlineData("GET")]
        [InlineData("get")]
        [InlineData("POST")]
        [InlineData(null)]
        public void ParseWebhook_AcceptsSignedNotification(string? method)
        {
            var gateway = SaferpayTestData.Gateway(new SaferpayRecordingHandler());
            var signer = new SaferpayNotificationSigner(SaferpayTestData.Options());

            var result = gateway.ParseWebhook(Webhook(method, ReservationId.ToString("D"), signer.Sign(ReservationId)));

            Assert.True(result.Accepted);
            Assert.Equal(200, result.StatusCode);
            var webhookEvent = Assert.Single(result.Events);
            Assert.Equal(PaymentWebhookEventKind.CheckoutNotification, webhookEvent.Kind);
            Assert.Equal(ReservationId, webhookEvent.ReservationId);
            Assert.Null(webhookEvent.EventId);
            Assert.Equal("saferpay.notify.success", webhookEvent.EventType);
        }

        [Fact]
        public void ParseWebhook_QueryKeysAreCaseInsensitive()
        {
            var gateway = SaferpayTestData.Gateway(new SaferpayRecordingHandler());
            var signer = new SaferpayNotificationSigner(SaferpayTestData.Options());
            var request = new PaymentWebhookRequest
            {
                Provider = PaymentProviderNames.Saferpay,
                Method = "GET",
                Query = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["ReservationID"] = ReservationId.ToString("D"),
                    ["SIG"] = signer.Sign(ReservationId),
                    ["Outcome"] = "FAIL"
                }
            };

            var result = gateway.ParseWebhook(request);

            Assert.True(result.Accepted);
            Assert.Equal("saferpay.notify.fail", Assert.Single(result.Events).EventType);
        }

        [Fact]
        public void ParseWebhook_RejectsOtherMethods_With405()
        {
            var gateway = SaferpayTestData.Gateway(new SaferpayRecordingHandler());
            var signer = new SaferpayNotificationSigner(SaferpayTestData.Options());

            var result = gateway.ParseWebhook(Webhook("PUT", ReservationId.ToString("D"), signer.Sign(ReservationId)));

            Assert.False(result.Accepted);
            Assert.Equal(405, result.StatusCode);
            Assert.Empty(result.Events);
        }

        [Fact]
        public void ParseWebhook_RejectsWrongOrMissingSignature_With400()
        {
            var gateway = SaferpayTestData.Gateway(new SaferpayRecordingHandler());
            var signer = new SaferpayNotificationSigner(SaferpayTestData.Options());
            var otherReservation = Guid.NewGuid();
            var differentKeySigner = new SaferpayNotificationSigner(SaferpayTestData.Options(o => o.NotifySecret = "another-notify-secret"));

            var cases = new[]
            {
                Webhook("GET", ReservationId.ToString("D"), signer.Sign(otherReservation)),
                Webhook("GET", ReservationId.ToString("D"), differentKeySigner.Sign(ReservationId)),
                Webhook("GET", ReservationId.ToString("D"), signer.Sign(ReservationId).Substring(1)),
                Webhook("GET", ReservationId.ToString("D"), "not*base64"),
                Webhook("GET", ReservationId.ToString("D"), null),
                Webhook("GET", null, signer.Sign(ReservationId)),
                Webhook("GET", "not-a-guid", signer.Sign(ReservationId)),
                Webhook("GET", Guid.Empty.ToString("D"), signer.Sign(Guid.Empty)),
                Webhook("GET", ReservationId.ToString("D"), signer.Sign(ReservationId), outcome: "maybe")
            };

            foreach (var request in cases)
            {
                var result = gateway.ParseWebhook(request);
                Assert.False(result.Accepted);
                Assert.Equal(400, result.StatusCode);
                Assert.Empty(result.Events);
                Assert.False(string.IsNullOrWhiteSpace(result.RejectReason));
            }

            Assert.False(gateway.ParseWebhook(null!).Accepted);
        }

        [Fact]
        public void SanitizeProviderText_RemovesCredentialsAndSaferpayIdentifiers()
        {
            var options = SaferpayTestData.Options(o => o.NotifySecret = "notify-secret-value");
            var gateway = SaferpayTestData.Gateway(new SaferpayRecordingHandler(), options);
            string text = $"user {SaferpayTestData.ApiUsername} pass {SaferpayTestData.ApiPassword} secret notify-secret-value " +
                          $"token {SaferpayTestData.Token} tx {SaferpayTestData.TransactionId} capture {SaferpayTestData.TransactionId}_c " +
                          $"mock mock_tx_0123456789abcdef0123456789abcdef reservation {ReservationId:D} short ABC123";

            string sanitized = gateway.SanitizeProviderText(text);

            Assert.DoesNotContain(SaferpayTestData.ApiUsername, sanitized);
            Assert.DoesNotContain(SaferpayTestData.ApiPassword, sanitized);
            Assert.DoesNotContain("notify-secret-value", sanitized);
            Assert.DoesNotContain(SaferpayTestData.Token, sanitized);
            Assert.DoesNotContain(SaferpayTestData.TransactionId, sanitized);
            Assert.DoesNotContain("_c ", sanitized);
            Assert.DoesNotContain("0123456789abcdef0123456789abcdef", sanitized);
            Assert.Contains(ReservationId.ToString("D"), sanitized);
            Assert.Contains("short ABC123", sanitized);
            Assert.Contains(SaferpayTextSanitizer.Redacted, sanitized);
            Assert.Null(gateway.SanitizeProviderText(null!));
            Assert.Equal(string.Empty, gateway.SanitizeProviderText(string.Empty));
        }

        private static PaymentWebhookRequest Webhook(string? method, string? reservationId, string? signature, string? outcome = null)
        {
            var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (reservationId != null) query["reservationId"] = reservationId;
            if (signature != null) query["sig"] = signature;
            if (outcome != null) query["outcome"] = outcome;
            return new PaymentWebhookRequest
            {
                Provider = PaymentProviderNames.Saferpay,
                Method = method!,
                Query = query
            };
        }

        internal static PaymentWebhookRequest WebhookFromUrl(string url)
        {
            var uri = new Uri(url);
            var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                query[Uri.UnescapeDataString(parts[0])] = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
            }

            return new PaymentWebhookRequest
            {
                Provider = PaymentProviderNames.Saferpay,
                Method = "GET",
                Query = query
            };
        }

        private sealed class HttpResponseMessageFactory
        {
            private readonly JObject _body;

            public HttpResponseMessageFactory(JObject body)
            {
                _body = body;
            }

            public System.Net.Http.HttpResponseMessage Create() => SaferpayTestData.Json(HttpStatusCode.OK, _body);
        }
    }
}
