using System;
using System.Linq;
using System.Net;
using Newtonsoft.Json.Linq;
using OCPP.Core.Server.Payments.Providers;
using OCPP.Core.Server.Payments.Providers.Saferpay;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    /// <summary>
    /// Saferpay paths that fall back to Transaction/Inquire: lookup by reservation (OrderId), expired payment page
    /// tokens, and captures or cancels that Saferpay refused because the transaction already changed state.
    /// </summary>
    public class SaferpayPaymentGatewayRecoveryTests
    {
        private const string InquirePath = "/api/Payment/v1/Transaction/Inquire";
        private const string AssertPath = "/api/Payment/v1/PaymentPage/Assert";

        private static readonly Guid ReservationId = Guid.Parse("3f2504e0-4f89-11d3-9a0c-0305e82c3301");

        [Fact]
        public void FindAuthorizationByReservation_PostsInquireByOrderId_WithExactRequestBody()
        {
            var handler = new SaferpayRecordingHandler()
                .Enqueue(SaferpayTestData.TransactionResponse("AUTHORIZED", 3000, ReservationId.ToString("D")));
            IPaymentGateway gateway = SaferpayTestData.Gateway(handler);

            var snapshot = gateway.FindAuthorizationByReservation(ReservationId);

            var request = Assert.Single(handler.Requests);
            Assert.Equal(InquirePath, request.Path);
            var json = request.Json!;
            Assert.Equal(new[] { "RequestHeader", "TransactionReference" }, json.Properties().Select(p => p.Name));
            Assert.True(JToken.DeepEquals(
                new JObject { ["OrderId"] = "3f2504e0-4f89-11d3-9a0c-0305e82c3301" },
                json["TransactionReference"]));
            var header = (JObject)json["RequestHeader"]!;
            Assert.Equal(new[] { "SpecVersion", "CustomerId", "RequestId", "RetryIndicator" }, header.Properties().Select(p => p.Name));
            Assert.Equal("1.54", (string?)header["SpecVersion"]);
            Assert.Equal(SaferpayTestData.CustomerId, (string?)header["CustomerId"]);
            Assert.Equal(0, (int)header["RetryIndicator"]!);

            Assert.NotNull(snapshot);
            Assert.Equal(SaferpayTestData.TransactionId, snapshot!.AuthorizationReference);
            Assert.Equal(PaymentAuthorizationState.RequiresCapture, snapshot.State);
            Assert.Equal("AUTHORIZED", snapshot.RawStatus);
            Assert.Equal(3000, snapshot.AmountMinor);
            Assert.Equal(3000, snapshot.AmountCapturableMinor);
            Assert.True(snapshot.IsCancelable);
            Assert.Equal("eur", snapshot.Currency);
            Assert.Equal(ReservationId, snapshot.OwnerReservationId);
            Assert.Empty(snapshot.Metadata);
        }

        [Theory]
        [InlineData("CAPTURED", PaymentAuthorizationState.Captured)]
        [InlineData("CANCELED", PaymentAuthorizationState.Canceled)]
        public void FindAuthorizationByReservation_MapsLikeGetAuthorization(string status, PaymentAuthorizationState expected)
        {
            var handler = new SaferpayRecordingHandler()
                .Enqueue(SaferpayTestData.TransactionResponse(status, 1800, ReservationId.ToString("D"), captureId: status == "CAPTURED" ? "cap_1" : null))
                .Enqueue(SaferpayTestData.TransactionResponse(status, 1800, ReservationId.ToString("D"), captureId: status == "CAPTURED" ? "cap_1" : null));
            var gateway = SaferpayTestData.Gateway(handler);

            var byReservation = gateway.FindAuthorizationByReservation(ReservationId)!;
            var byTransaction = gateway.GetAuthorization(SaferpayTestData.TransactionId);

            Assert.Equal(expected, byReservation.State);
            Assert.Equal(byTransaction.State, byReservation.State);
            Assert.Equal(byTransaction.RawStatus, byReservation.RawStatus);
            Assert.Equal(byTransaction.AmountMinor, byReservation.AmountMinor);
            Assert.Equal(byTransaction.AmountReceivedMinor, byReservation.AmountReceivedMinor);
            Assert.Equal(byTransaction.AmountCapturableMinor, byReservation.AmountCapturableMinor);
            Assert.Equal(byTransaction.IsCancelable, byReservation.IsCancelable);
            Assert.Equal(byTransaction.CaptureReference, byReservation.CaptureReference);
        }

        [Fact]
        public void FindAuthorizationByReservation_TransactionNotFound_ReturnsNull()
        {
            var handler = new SaferpayRecordingHandler()
                .Enqueue(SaferpayTestData.Error(HttpStatusCode.PaymentRequired, "TRANSACTION_NOT_FOUND"));
            IPaymentGateway gateway = SaferpayTestData.Gateway(handler);

            Assert.Null(gateway.FindAuthorizationByReservation(ReservationId));
            Assert.Single(handler.Requests);
        }

        [Fact]
        public void FindAuthorizationByReservation_WithoutOrderIdInResponse_ReportsRequestedReservation()
        {
            var handler = new SaferpayRecordingHandler()
                .Enqueue(SaferpayTestData.TransactionResponse("AUTHORIZED", 3000, orderId: null));
            var gateway = SaferpayTestData.Gateway(handler);

            var snapshot = gateway.FindAuthorizationByReservation(ReservationId);

            Assert.Equal(ReservationId, snapshot!.OwnerReservationId);
        }

        [Fact]
        public void FindAuthorizationByReservation_OtherOrderIdInResponse_IsInvalidResponse()
        {
            var handler = new SaferpayRecordingHandler()
                .Enqueue(SaferpayTestData.TransactionResponse("AUTHORIZED", 3000, Guid.NewGuid().ToString("D")));
            var gateway = SaferpayTestData.Gateway(handler);

            var exception = Assert.Throws<SaferpayApiException>(() => gateway.FindAuthorizationByReservation(ReservationId));

            Assert.Equal("invalid_response", exception.Code);
        }

        [Fact]
        public void FindAuthorizationByReservation_TransientError_Propagates()
        {
            var handler = new SaferpayRecordingHandler()
                .Enqueue(SaferpayTestData.Error(HttpStatusCode.InternalServerError, "INTERNAL_ERROR", "RETRY"));
            var gateway = SaferpayTestData.Gateway(handler);

            var exception = Assert.Throws<SaferpayApiException>(() => gateway.FindAuthorizationByReservation(ReservationId));

            Assert.Equal("INTERNAL_ERROR", exception.Code);
            Assert.True(exception.IsTransient);
        }

        [Fact]
        public void FindAuthorizationByReservation_RejectsEmptyReservation_AndRequiresConfiguration()
        {
            var handler = new SaferpayRecordingHandler();

            Assert.Throws<ArgumentException>(() => SaferpayTestData.Gateway(handler).FindAuthorizationByReservation(Guid.Empty));
            var exception = Assert.Throws<SaferpayApiException>(() =>
                SaferpayTestData.Gateway(handler, SaferpayTestData.Options(o => o.Enabled = false)).FindAuthorizationByReservation(ReservationId));

            Assert.Equal("not_configured", exception.Code);
            Assert.Empty(handler.Requests);
        }

        [Fact]
        public void GetCheckout_TokenExpired_WithoutReferences_IsExpiredWithoutInquire()
        {
            var handler = new SaferpayRecordingHandler()
                .Enqueue(SaferpayTestData.Error(HttpStatusCode.BadRequest, "TOKEN_EXPIRED"));
            var gateway = SaferpayTestData.Gateway(handler);

            var status = gateway.GetCheckout(SaferpayTestData.Token);

            Assert.Equal(PaymentCheckoutState.Expired, status.State);
            Assert.Equal("TOKEN_EXPIRED", status.RawStatus);
            Assert.False(status.IsPaid);
            Assert.Null(status.Authorization);
            Assert.Null(status.OwnerReservationId);
            Assert.Equal(AssertPath, Assert.Single(handler.Requests).Path);
        }

        [Fact]
        public void GetCheckout_TokenExpired_InquiresByOrderId_AndIsExpiredWhenNothingWasAuthorized()
        {
            var handler = new SaferpayRecordingHandler()
                .Enqueue(SaferpayTestData.Error(HttpStatusCode.PaymentRequired, "TOKEN_EXPIRED", orderId: ReservationId.ToString("D"), transactionId: SaferpayTestData.TransactionId))
                .Enqueue(SaferpayTestData.Error(HttpStatusCode.PaymentRequired, "TRANSACTION_NOT_FOUND"));
            var gateway = SaferpayTestData.Gateway(handler);

            var status = gateway.GetCheckout(SaferpayTestData.Token);

            Assert.Equal(new[] { AssertPath, InquirePath }, handler.Requests.Select(r => r.Path));
            Assert.True(JToken.DeepEquals(
                new JObject { ["OrderId"] = ReservationId.ToString("D") },
                handler.LastRequest.Json!["TransactionReference"]));
            Assert.Equal(PaymentCheckoutState.Expired, status.State);
            Assert.Equal("TOKEN_EXPIRED", status.RawStatus);
            Assert.False(status.IsPaid);
            Assert.Null(status.AuthorizationReference);
            Assert.Null(status.Authorization);
            Assert.Null(status.FailureCode);
            Assert.Equal(SaferpayTestData.Token, status.SessionReference);
            Assert.Equal(ReservationId, status.OwnerReservationId);
        }

        [Fact]
        public void GetCheckout_TokenExpired_WithOnlyTransactionId_InquiresByTransactionId()
        {
            var handler = new SaferpayRecordingHandler()
                .Enqueue(SaferpayTestData.Error(HttpStatusCode.PaymentRequired, "TOKEN_EXPIRED", transactionId: SaferpayTestData.TransactionId))
                .Enqueue(SaferpayTestData.Error(HttpStatusCode.PaymentRequired, "TRANSACTION_NOT_FOUND"));
            var gateway = SaferpayTestData.Gateway(handler);

            var status = gateway.GetCheckout(SaferpayTestData.Token);

            Assert.True(JToken.DeepEquals(
                new JObject { ["TransactionId"] = SaferpayTestData.TransactionId },
                handler.LastRequest.Json!["TransactionReference"]));
            Assert.Equal(PaymentCheckoutState.Expired, status.State);
        }

        [Fact]
        public void GetCheckout_TokenExpired_WithAuthorizedTransaction_IsCompleteWithThatAuthorization()
        {
            var payer = new JObject
            {
                ["BillingAddress"] = new JObject { ["FirstName"] = "Ana", ["LastName"] = "Horvat", ["Email"] = "ana@example.com" }
            };
            var handler = new SaferpayRecordingHandler()
                .Enqueue(SaferpayTestData.Error(HttpStatusCode.PaymentRequired, "TOKEN_EXPIRED", orderId: ReservationId.ToString("D")))
                .Enqueue(SaferpayTestData.TransactionResponse("AUTHORIZED", 5000, ReservationId.ToString("D"), payer: payer));
            var gateway = SaferpayTestData.Gateway(handler);

            var status = gateway.GetCheckout(SaferpayTestData.Token);

            Assert.Equal(PaymentCheckoutState.Complete, status.State);
            Assert.True(status.IsPaid);
            Assert.Equal("AUTHORIZED", status.RawStatus);
            Assert.Equal(SaferpayTestData.Token, status.SessionReference);
            Assert.Equal(SaferpayTestData.TransactionId, status.AuthorizationReference);
            Assert.Equal(ReservationId, status.OwnerReservationId);
            Assert.Equal("ana@example.com", status.CustomerEmail);
            Assert.Equal("Ana Horvat", status.CustomerName);
            Assert.NotNull(status.Authorization);
            Assert.Equal(PaymentAuthorizationState.RequiresCapture, status.Authorization.State);
            Assert.Equal(5000, status.Authorization.AmountCapturableMinor);
            Assert.True(status.Authorization.IsCancelable);
        }

        [Fact]
        public void GetCheckout_TokenExpired_WithCapturedTransaction_IsCompleteAndCaptured()
        {
            var handler = new SaferpayRecordingHandler()
                .Enqueue(SaferpayTestData.Error(HttpStatusCode.PaymentRequired, "TOKEN_EXPIRED", orderId: ReservationId.ToString("D")))
                .Enqueue(SaferpayTestData.TransactionResponse("CAPTURED", 5000, ReservationId.ToString("D"), captureId: "cap_1"));
            var gateway = SaferpayTestData.Gateway(handler);

            var status = gateway.GetCheckout(SaferpayTestData.Token);

            Assert.Equal(PaymentCheckoutState.Complete, status.State);
            Assert.True(status.IsPaid);
            Assert.Equal(PaymentAuthorizationState.Captured, status.Authorization.State);
            Assert.Equal(5000, status.Authorization.AmountReceivedMinor);
        }

        [Fact]
        public void GetCheckout_TokenExpired_InquireTransientError_Propagates()
        {
            // Reporting Expired here could leave a hold unreleased, so the caller must retry instead.
            var handler = new SaferpayRecordingHandler()
                .Enqueue(SaferpayTestData.Error(HttpStatusCode.PaymentRequired, "TOKEN_EXPIRED", orderId: ReservationId.ToString("D")))
                .Enqueue(SaferpayTestData.Text(HttpStatusCode.ServiceUnavailable, "<html/>"));
            var gateway = SaferpayTestData.Gateway(handler);

            var exception = Assert.Throws<SaferpayApiException>(() => gateway.GetCheckout(SaferpayTestData.Token));

            Assert.Equal("http_503", exception.Code);
            Assert.True(exception.IsTransient);
        }

        [Fact]
        public void Capture_AlreadyCaptured_WithLaggingInquire_CapsUnverifiedAmountAtAuthorization()
        {
            var handler = new SaferpayRecordingHandler()
                .Enqueue(SaferpayTestData.Error(HttpStatusCode.PaymentRequired, "TRANSACTION_ALREADY_CAPTURED"))
                .Enqueue(SaferpayTestData.TransactionResponse("AUTHORIZED", 2500, ReservationId.ToString("D")));
            var gateway = SaferpayTestData.Gateway(handler);

            var snapshot = gateway.Capture(SaferpayTestData.TransactionId, 3000, "EUR", "capture:key");

            Assert.Equal(PaymentAuthorizationState.Captured, snapshot.State);
            Assert.Equal(SaferpayPaymentGateway.UnverifiedCaptureStatus, snapshot.RawStatus);
            Assert.Equal(2500, snapshot.AmountReceivedMinor);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("CANCELED")]
        [InlineData("PENDING")]
        [InlineData("SOMETHING_ELSE")]
        public void Capture_AlreadyCaptured_WhenInquireCannotConfirm_ThrowsTransientCaptureStateUnknown(string? inquiredStatus)
        {
            var handler = new SaferpayRecordingHandler()
                .Enqueue(SaferpayTestData.Error(HttpStatusCode.PaymentRequired, "TRANSACTION_ALREADY_CAPTURED"))
                .Enqueue(inquiredStatus == null
                    ? SaferpayTestData.Error(HttpStatusCode.PaymentRequired, "TRANSACTION_NOT_FOUND")
                    : SaferpayTestData.TransactionResponse(inquiredStatus, 2500, ReservationId.ToString("D")));
            var gateway = SaferpayTestData.Gateway(handler);

            var exception = Assert.Throws<SaferpayApiException>(() => gateway.Capture(SaferpayTestData.TransactionId, 1245, "eur", "capture:key"));

            Assert.Equal("capture_state_unknown", exception.Code);
            Assert.True(exception.IsTransient);
            Assert.Equal(PaymentProviderNames.Saferpay, exception.Provider);
            Assert.Contains(inquiredStatus ?? "not found", exception.Message);
            Assert.DoesNotContain(SaferpayTestData.TransactionId, exception.Message);
            Assert.Equal("TRANSACTION_ALREADY_CAPTURED", Assert.IsType<SaferpayApiException>(exception.InnerException).ErrorName);
            Assert.Equal(new[] { "/api/Payment/v1/Transaction/Capture", InquirePath }, handler.Requests.Select(r => r.Path));
        }

        [Fact]
        public void Capture_AlreadyCaptured_WithLaggingInquireInOtherCurrency_ThrowsTransientCaptureStateUnknown()
        {
            var handler = new SaferpayRecordingHandler()
                .Enqueue(SaferpayTestData.Error(HttpStatusCode.PaymentRequired, "TRANSACTION_ALREADY_CAPTURED"))
                .Enqueue(SaferpayTestData.TransactionResponse("AUTHORIZED", 2500, ReservationId.ToString("D"), currency: "CHF"));
            var gateway = SaferpayTestData.Gateway(handler);

            var exception = Assert.Throws<SaferpayApiException>(() => gateway.Capture(SaferpayTestData.TransactionId, 1245, "eur", "capture:key"));

            Assert.Equal("capture_state_unknown", exception.Code);
            Assert.True(exception.IsTransient);
            Assert.Contains("AUTHORIZED in CHF", exception.Message);
        }
    }
}
