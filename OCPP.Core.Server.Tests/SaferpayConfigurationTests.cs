using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using OCPP.Core.Server.Payments.Providers;
using OCPP.Core.Server.Payments.Providers.Saferpay;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    /// <summary>Saferpay settings that guard credentials and the deferred-capture flow, and the gateway capability flags.</summary>
    public class SaferpayConfigurationTests
    {
        private static readonly Guid ReservationId = Guid.Parse("3f2504e0-4f89-11d3-9a0c-0305e82c3301");

        private static PaymentCheckoutRequest CheckoutRequest() =>
            new PaymentCheckoutRequest
            {
                ReservationId = ReservationId,
                AmountMinor = 2500,
                Currency = "eur",
                ProductName = "Charging session",
                SuccessUrl = SaferpayTestData.ReturnUrl,
                IdempotencyKey = $"checkout_create:{ReservationId}"
            };

        private static System.Net.Http.HttpResponseMessage InitializeResponse() =>
            SaferpayTestData.Json(HttpStatusCode.OK, new JObject
            {
                ["ResponseHeader"] = SaferpayTestData.ResponseHeader(),
                ["Token"] = SaferpayTestData.Token,
                ["Expiration"] = "2026-10-20T12:45:22.258+01:00",
                ["RedirectUrl"] = "https://saferpay.example.com/vt2/api/PaymentPage/123456/17654321/token"
            });

        private static int Warnings(RecordingLogger<SaferpayPaymentGateway> logger, string text) =>
            logger.Entries.Count(entry => entry.Level == LogLevel.Warning && entry.Message.Contains(text, StringComparison.Ordinal));

        [Fact]
        public void CapabilityFlags_AreImplementedThroughTheContract()
        {
            IPaymentGateway gateway = SaferpayTestData.Gateway(new SaferpayRecordingHandler());

            Assert.True(gateway.CapturedCheckoutRequiresRefund);
            Assert.Equal(TimeSpan.FromHours(1), gateway.CheckoutLifetime);
        }

        [Theory]
        [InlineData(0, 60)]
        [InlineData(-5, 60)]
        [InlineData(20, 20)]
        [InlineData(90, 90)]
        public void CheckoutLifetime_UsesConfiguredMinutes_OrOneHour(int configuredMinutes, int expectedMinutes)
        {
            IPaymentGateway gateway = SaferpayTestData.Gateway(
                new SaferpayRecordingHandler(),
                SaferpayTestData.Options(o => o.CheckoutLifetimeMinutes = configuredMinutes));

            Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), gateway.CheckoutLifetime);
        }

        [Theory]
        [InlineData("https://www.saferpay.com/api", true)]
        [InlineData("HTTPS://test.saferpay.com/api/", true)]
        [InlineData("http://www.saferpay.com/api", false)]
        [InlineData("http://127.0.0.1:8080/api", false)]
        [InlineData("ftp://www.saferpay.com/api", false)]
        [InlineData("www.saferpay.com/api", false)]
        [InlineData("/api", false)]
        public void IsConfigured_OutsideMockMode_RequiresAbsoluteHttpsBaseUrl(string baseUrl, bool expected)
        {
            var handler = new SaferpayRecordingHandler();
            var gateway = SaferpayTestData.Gateway(handler, SaferpayTestData.Options(o => o.BaseUrl = baseUrl));

            Assert.Equal(expected, gateway.IsConfigured);
            if (!expected)
            {
                var exception = Assert.Throws<SaferpayApiException>(() => gateway.GetCheckout(SaferpayTestData.Token));
                Assert.Equal("not_configured", exception.Code);
                Assert.Contains("https", exception.Message);
                Assert.Empty(handler.Requests);
            }
        }

        [Fact]
        public void IsConfigured_InMockMode_AllowsHttpBaseUrl()
        {
            var gateway = SaferpayTestData.Gateway(
                new SaferpayRecordingHandler(),
                SaferpayTestData.Options(o =>
                {
                    o.UseMockServices = true;
                    o.BaseUrl = "http://127.0.0.1:8080/api";
                }));

            Assert.True(gateway.IsConfigured);
        }

        [Fact]
        public void IsConfigured_HttpBaseUrl_LogsWarningOnce()
        {
            var logger = new RecordingLogger<SaferpayPaymentGateway>();
            var gateway = SaferpayTestData.Gateway(
                new SaferpayRecordingHandler(),
                SaferpayTestData.Options(o => o.BaseUrl = "http://www.saferpay.com/api"),
                logger: logger);

            Assert.False(gateway.IsConfigured);
            Assert.False(gateway.IsConfigured);
            Assert.Throws<SaferpayApiException>(() => gateway.CreateCheckout(CheckoutRequest()));

            Assert.Equal(1, Warnings(logger, "Saferpay:BaseUrl is not an absolute https URL"));
        }

        [Fact]
        public void IsConfigured_HttpsBaseUrl_LogsNoBaseUrlWarning()
        {
            var logger = new RecordingLogger<SaferpayPaymentGateway>();
            var gateway = SaferpayTestData.Gateway(new SaferpayRecordingHandler(), logger: logger);

            Assert.True(gateway.IsConfigured);
            Assert.DoesNotContain(logger.Entries, entry => entry.Level >= LogLevel.Warning);
        }

        [Fact]
        public void HttpNotifyBaseUrl_IsStillSent_ButLogsWarningOnce()
        {
            var logger = new RecordingLogger<SaferpayPaymentGateway>();
            var handler = new SaferpayRecordingHandler().Enqueue(InitializeResponse());
            var gateway = SaferpayTestData.Gateway(
                handler,
                SaferpayTestData.Options(o => o.NotifyBaseUrl = "http://ocpp.example.com/API"),
                logger: logger);

            Assert.True(gateway.IsConfigured);
            gateway.CreateCheckout(CheckoutRequest());
            Assert.True(gateway.IsConfigured);

            Assert.StartsWith(
                "http://ocpp.example.com/API/Payments/Webhook/Saferpay?reservationId=",
                (string?)handler.LastRequest.Json!["Notification"]!["SuccessNotifyUrl"]);
            Assert.Equal(1, Warnings(logger, "Saferpay:NotifyBaseUrl is not an absolute https URL"));
        }

        [Fact]
        public void HttpNotifyBaseUrl_InMockMode_LogsNoWarning()
        {
            var logger = new RecordingLogger<SaferpayPaymentGateway>();
            var gateway = SaferpayTestData.Gateway(
                new SaferpayRecordingHandler(),
                SaferpayTestData.Options(o =>
                {
                    o.UseMockServices = true;
                    o.NotifyBaseUrl = "http://127.0.0.1:8081/API";
                }),
                logger: logger);

            Assert.True(gateway.IsConfigured);
            Assert.DoesNotContain(logger.Entries, entry => entry.Level >= LogLevel.Warning);
        }

        [Fact]
        public void ApiClient_HttpBaseUrlOutsideMockMode_IsRefusedWithoutSendingCredentials()
        {
            var handler = new SaferpayRecordingHandler();
            var client = SaferpayTestData.Client(handler, SaferpayTestData.Options(o => o.BaseUrl = "http://www.saferpay.com/api"));

            var exception = Assert.Throws<SaferpayApiException>(() => client.Post(SaferpayApiClient.AssertPath, new JObject { ["Token"] = "abc" }, null));

            Assert.Equal("invalid_configuration", exception.Code);
            Assert.False(exception.IsTransient);
            Assert.Contains("https", exception.Message);
            Assert.Empty(handler.Requests);
        }

        [Fact]
        public void ApiClient_HttpBaseUrlInMockMode_IsAllowed()
        {
            var handler = new SaferpayRecordingHandler()
                .Enqueue(SaferpayTestData.Json(HttpStatusCode.OK, new JObject { ["Token"] = "abc" }));
            var client = SaferpayTestData.Client(handler, SaferpayTestData.Options(o =>
            {
                o.UseMockServices = true;
                o.BaseUrl = "http://127.0.0.1:8080/api";
            }));

            client.Post(SaferpayApiClient.AssertPath, new JObject { ["Token"] = "abc" }, null);

            Assert.Equal("http://127.0.0.1:8080/api/Payment/v1/PaymentPage/Assert", handler.LastRequest.Uri!.ToString());
        }

        [Fact]
        public void DefaultPaymentMethods_AreDeferredCaptureCardBrands()
        {
            Assert.Equal(new[] { "VISA", "MASTERCARD", "MAESTRO" }, SaferpayOptions.DefaultPaymentMethods);
            Assert.Null(new SaferpayOptions().PaymentMethods);
        }

        public static IEnumerable<object?[]> UnsetPaymentMethods()
        {
            yield return new object?[] { null };
            yield return new object?[] { Array.Empty<string>() };
            yield return new object?[] { new[] { " ", "" } };
        }

        [Theory]
        [MemberData(nameof(UnsetPaymentMethods))]
        public void CreateCheckout_WithoutConfiguredPaymentMethods_SendsDefaultCardBrands_AndConfiguredWallets(string[]? paymentMethods)
        {
            var handler = new SaferpayRecordingHandler().Enqueue(InitializeResponse());
            var gateway = SaferpayTestData.Gateway(handler, SaferpayTestData.Options(o =>
            {
                o.PaymentMethods = paymentMethods!;
                o.Wallets = new[] { "googlepay", "APPLEPAY" };
            }));

            gateway.CreateCheckout(CheckoutRequest());

            var json = handler.LastRequest.Json!;
            Assert.Equal(new[] { "VISA", "MASTERCARD", "MAESTRO" }, json["PaymentMethods"]!.Values<string>());
            Assert.Equal(new[] { "GOOGLEPAY", "APPLEPAY" }, json["Wallets"]!.Values<string>());
        }

        [Fact]
        public void CreateCheckout_ExplicitPaymentMethods_ReplaceTheDefault()
        {
            var handler = new SaferpayRecordingHandler().Enqueue(InitializeResponse());
            var gateway = SaferpayTestData.Gateway(handler, SaferpayTestData.Options(o => o.PaymentMethods = new[] { "amex", " visa " }));

            gateway.CreateCheckout(CheckoutRequest());

            Assert.Equal(new[] { "AMEX", "VISA" }, handler.LastRequest.Json!["PaymentMethods"]!.Values<string>());
        }

        [Theory]
        [InlineData(null, new[] { "VISA", "MASTERCARD", "MAESTRO" })]
        [InlineData("AMEX", new[] { "AMEX" })]
        public void BoundConfiguration_ExplicitPaymentMethodsReplaceDefault_InsteadOfBeingAppended(string? configured, string[] expected)
        {
            var values = new Dictionary<string, string?>
            {
                ["Saferpay:Enabled"] = "true",
                ["Saferpay:UseMockServices"] = "true"
            };
            if (configured != null)
            {
                values["Saferpay:PaymentMethods:0"] = configured;
            }

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSaferpayPaymentGateway(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
            using var provider = services.BuildServiceProvider();

            var gateway = Assert.IsType<SaferpayPaymentGateway>(Assert.Single(provider.GetServices<IPaymentGateway>()));

            Assert.Equal(expected, gateway.ResolvePaymentMethods());
        }
    }
}
