using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using OCPP.Core.Management.Helpers;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    public class ManagementPaymentProviderNeutralityTests
    {
        [Fact]
        public void PublicPortalScript_HasNoProviderSpecificBranding()
        {
            var script = ReadProjectFile("OCPP.Core.Management", "wwwroot", "js", "public-portal.js");

            Assert.DoesNotContain("Stripe", script, StringComparison.OrdinalIgnoreCase);

            var secureInfoLines = script
                .Split('\n')
                .Where(line => line.Contains("\"start.secureInfo\"", StringComparison.Ordinal))
                .ToList();
            Assert.Equal(6, secureInfoLines.Count);
            Assert.Contains(secureInfoLines, line => line.Contains("Sigurno online plaćanje. Naplaćuje se samo stvarni trošak.", StringComparison.Ordinal));
            Assert.Contains(secureInfoLines, line => line.Contains("Secure online payment. Only the actual cost is charged.", StringComparison.Ordinal));
            Assert.All(secureInfoLines, line => Assert.DoesNotContain(line, ch => ch >= 0x0400 && ch <= 0x04FF));
        }

        [Fact]
        public void PublicStartView_SecurePaymentFallbackIsProviderNeutral()
        {
            var view = ReadProjectFile("OCPP.Core.Management", "Views", "Public", "Start.cshtml");

            Assert.DoesNotContain("Stripe", view, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Secure online payment. Only the actual cost is charged.", view, StringComparison.Ordinal);
            Assert.Contains("start.secureInfo", view, StringComparison.Ordinal);
        }

        [Fact]
        public void MockCheckoutView_IsProviderNeutralAndKeepsSimulatorHooks()
        {
            var view = ReadProjectFile("OCPP.Core.Management", "Views", "Payments", "MockCheckout.cshtml");

            Assert.DoesNotContain("Stripe", view, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("id=\"mock-pay-now\" href=\"@Model.SuccessUrl\"", view, StringComparison.Ordinal);
            Assert.Contains("id=\"mock-cancel-checkout\" href=\"@Model.CancelUrl\"", view, StringComparison.Ordinal);
            Assert.Contains("@Model.Title", view, StringComparison.Ordinal);
            Assert.Contains("@if (Model.HasSessionId)", view, StringComparison.Ordinal);
            Assert.Contains("This page replaces the payment provider during local simulator and browser runs.", view, StringComparison.Ordinal);
        }

        [Fact]
        public void LayoutWebhookBanner_UsesProviderAwareCheckAndRealEnvironmentVariableName()
        {
            var layout = ReadProjectFile("OCPP.Core.Management", "Views", "Shared", "_Layout.cshtml");

            Assert.Contains("PaymentProviderConfiguration.ShouldWarnMissingStripeWebhookSecret(Config)", layout, StringComparison.Ordinal);
            Assert.Contains("<code>Stripe__WebhookSecret</code>", layout, StringComparison.Ordinal);
            Assert.DoesNotContain("STRIPE_WEBHOOK_SECRET", layout, StringComparison.Ordinal);
            Assert.DoesNotContain("Config?[\"Stripe:WebhookSecret\"]", layout, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("  ")]
        [InlineData("Stripe")]
        [InlineData("stripe")]
        [InlineData(" STRIPE ")]
        public void WebhookSecretWarning_IsShownForStripeWithoutSecret(string? provider)
        {
            var config = BuildConfig(provider, webhookSecret: null);

            Assert.True(PaymentProviderConfiguration.IsStripeProvider(config));
            Assert.True(PaymentProviderConfiguration.ShouldWarnMissingStripeWebhookSecret(config));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("Stripe")]
        public void WebhookSecretWarning_IsHiddenWhenStripeSecretIsConfigured(string? provider)
        {
            var config = BuildConfig(provider, webhookSecret: "whsec_example");

            Assert.False(PaymentProviderConfiguration.ShouldWarnMissingStripeWebhookSecret(config));
        }

        [Theory]
        [InlineData("Saferpay")]
        [InlineData("saferpay")]
        public void WebhookSecretWarning_IsHiddenForOtherProviders(string provider)
        {
            var config = BuildConfig(provider, webhookSecret: null);

            Assert.False(PaymentProviderConfiguration.IsStripeProvider(config));
            Assert.False(PaymentProviderConfiguration.ShouldWarnMissingStripeWebhookSecret(config));
            Assert.Equal("Saferpay", PaymentProviderConfiguration.GetConfiguredProvider(config));
        }

        [Fact]
        public void WebhookSecretWarning_KeepsLegacyBehaviourWithoutConfiguration()
        {
            Assert.Equal("Stripe", PaymentProviderConfiguration.GetConfiguredProvider(null));
            Assert.True(PaymentProviderConfiguration.ShouldWarnMissingStripeWebhookSecret(null));
        }

        [Fact]
        public void ManagementAppSettings_DefaultsToStripeProvider()
        {
            var config = new ConfigurationBuilder()
                .AddJsonFile(FindProjectFile("OCPP.Core.Management", "appsettings.json"), optional: false, reloadOnChange: false)
                .Build();

            Assert.Equal("Stripe", config["Payments:DefaultProvider"]);
            Assert.Null(config["Payments:Provider"]);
            Assert.True(PaymentProviderConfiguration.IsStripeProvider(config));
        }

        [Fact]
        public void GetConfiguredProvider_PrefersDefaultProviderOverLegacyKey()
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Payments:DefaultProvider"] = "saferpay",
                    ["Payments:Provider"] = "Stripe"
                })
                .Build();

            Assert.Equal("Saferpay", PaymentProviderConfiguration.GetConfiguredProvider(config));
            Assert.False(PaymentProviderConfiguration.ShouldWarnMissingStripeWebhookSecret(config));
        }

        [Theory]
        [InlineData(null, null)]
        [InlineData("", null)]
        [InlineData("  ", null)]
        [InlineData("stripe", "Stripe")]
        [InlineData(" Saferpay ", "Saferpay")]
        public void TryNormalizeChargePointProvider_MapsDefaultAndKnownProviders(string? value, string? expected)
        {
            Assert.True(PaymentProviderConfiguration.TryNormalizeChargePointProvider(value, out var normalized));
            Assert.Equal(expected, normalized);
        }

        [Theory]
        [InlineData("Worldline")]
        [InlineData("PayPal")]
        public void TryNormalizeChargePointProvider_RejectsUnknownProviders(string value)
        {
            Assert.False(PaymentProviderConfiguration.TryNormalizeChargePointProvider(value, out var normalized));
            Assert.Null(normalized);
        }

        private static IConfiguration BuildConfig(string? provider, string? webhookSecret)
        {
            return new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Payments:Provider"] = provider,
                    ["Stripe:WebhookSecret"] = webhookSecret
                })
                .Build();
        }

        private static string ReadProjectFile(params string[] parts)
        {
            return File.ReadAllText(FindProjectFile(parts));
        }

        private static string FindProjectFile(params string[] parts)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                var path = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
                if (File.Exists(path)) return path;
                directory = directory.Parent;
            }
            throw new FileNotFoundException($"Could not locate {string.Join('/', parts)}.");
        }
    }
}
