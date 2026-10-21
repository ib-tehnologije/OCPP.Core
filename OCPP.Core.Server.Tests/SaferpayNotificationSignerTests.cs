using System;
using System.Security.Cryptography;
using System.Text;
using OCPP.Core.Server.Payments.Providers.Saferpay;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    public class SaferpayNotificationSignerTests
    {
        private static readonly Guid ReservationId = Guid.Parse("c52ad184-7235-4511-ab2c-33b59e796901");

        [Fact]
        public void Sign_IsDeterministicBase64UrlWithoutPadding()
        {
            var signer = new SaferpayNotificationSigner(SaferpayTestData.Options());

            string first = signer.Sign(ReservationId);
            string second = signer.Sign(ReservationId);

            Assert.Equal(first, second);
            Assert.Equal(43, first.Length);
            Assert.Matches("^[A-Za-z0-9_-]+$", first);
            Assert.NotEqual(first, signer.Sign(Guid.NewGuid()));
        }

        [Fact]
        public void Sign_WithoutNotifySecret_UsesKeyDerivedFromApiPasswordAndCustomerId()
        {
            var options = SaferpayTestData.Options(o => o.NotifySecret = null);
            var signer = new SaferpayNotificationSigner(options);

            byte[] key = SHA256.HashData(Encoding.UTF8.GetBytes(
                $"saferpay-notify-key:{SaferpayTestData.ApiPassword}:{SaferpayTestData.CustomerId}"));
            string expected = ExpectedSignature(key, ReservationId);

            Assert.Equal(expected, signer.Sign(ReservationId));
            Assert.NotEqual(
                expected,
                new SaferpayNotificationSigner(SaferpayTestData.Options(o => o.ApiPassword = "AnotherPassword123456")).Sign(ReservationId));
        }

        [Fact]
        public void Sign_WithNotifySecret_UsesTheSecretAsKey()
        {
            var options = SaferpayTestData.Options(o => o.NotifySecret = "configured-notify-secret");
            var signer = new SaferpayNotificationSigner(options);

            Assert.Equal(ExpectedSignature(Encoding.UTF8.GetBytes("configured-notify-secret"), ReservationId), signer.Sign(ReservationId));
            Assert.NotEqual(new SaferpayNotificationSigner(SaferpayTestData.Options()).Sign(ReservationId), signer.Sign(ReservationId));
        }

        [Fact]
        public void Verify_AcceptsOwnSignature_AndRejectsTamperedValues()
        {
            var signer = new SaferpayNotificationSigner(SaferpayTestData.Options());
            string signature = signer.Sign(ReservationId);

            Assert.True(signer.Verify(ReservationId, signature));
            Assert.True(signer.Verify(ReservationId, " " + signature + " "));
            Assert.False(signer.Verify(Guid.NewGuid(), signature));
            Assert.False(signer.Verify(Guid.Empty, signer.Sign(Guid.Empty)));
            Assert.False(signer.Verify(ReservationId, null!));
            Assert.False(signer.Verify(ReservationId, string.Empty));
            Assert.False(signer.Verify(ReservationId, signature + "="));
            Assert.False(signer.Verify(ReservationId, signature.Replace('-', '+').Replace('_', '/') + "A"));
            Assert.False(signer.Verify(ReservationId, (signature[0] == 'A' ? "B" : "A") + signature.Substring(1)));
            Assert.False(signer.Verify(ReservationId, "%%%"));
        }

        [Fact]
        public void BuildNotifyUrls_UseApiBaseAndSignature()
        {
            var signer = new SaferpayNotificationSigner(SaferpayTestData.Options(o => o.NotifyBaseUrl = " https://ocpp.example.com/API/ "));
            string signature = signer.Sign(ReservationId);

            Assert.True(signer.CanBuildNotifyUrls);
            Assert.Equal(
                $"https://ocpp.example.com/API/Payments/Webhook/Saferpay?reservationId={ReservationId:D}&sig={signature}",
                signer.BuildSuccessNotifyUrl(ReservationId));
            Assert.Equal(
                $"https://ocpp.example.com/API/Payments/Webhook/Saferpay?reservationId={ReservationId:D}&sig={signature}&outcome=fail",
                signer.BuildFailNotifyUrl(ReservationId));
        }

        [Fact]
        public void BuildNotifyUrls_ReturnNull_WithoutNotifyBaseUrl()
        {
            var signer = new SaferpayNotificationSigner(SaferpayTestData.Options(o => o.NotifyBaseUrl = "  "));

            Assert.False(signer.CanBuildNotifyUrls);
            Assert.Null(signer.BuildSuccessNotifyUrl(ReservationId));
            Assert.Null(signer.BuildFailNotifyUrl(ReservationId));
        }

        private static string ExpectedSignature(byte[] key, Guid reservationId)
        {
            using var hmac = new HMACSHA256(key);
            byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes("saferpay-notify:" + reservationId.ToString("D")));
            return Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
    }
}
