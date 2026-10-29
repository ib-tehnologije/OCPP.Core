/*
 * OCPP.Core - https://github.com/dallmann-consulting/OCPP.Core
 */

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;

namespace OCPP.Core.Management.Helpers
{
    /// <summary>
    /// Management-side view of the payment provider settings. The OCPP server owns the provider integration;
    /// the management app reads "Payments:DefaultProvider" (legacy "Payments:Provider") only to decide which
    /// operator configuration hints apply and to label the "Default" choice of a charge point. An empty value
    /// means the default provider (Stripe).
    /// </summary>
    public static class PaymentProviderConfiguration
    {
        public const string DefaultProviderKey = "Payments:DefaultProvider";
        public const string ProviderKey = "Payments:Provider";
        public const string StripeProviderName = "Stripe";
        public const string SaferpayProviderName = "Saferpay";

        /// <summary>Provider names a charge point may override to, in display order.</summary>
        public static readonly IReadOnlyList<string> ChargePointProviders = new[] { StripeProviderName, SaferpayProviderName };

        /// <summary>
        /// Stripe signing secret key. As an environment variable it is "Stripe__WebhookSecret", because
        /// configuration uses AddEnvironmentVariables() without a prefix.
        /// </summary>
        public const string StripeWebhookSecretKey = "Stripe:WebhookSecret";

        public static string GetConfiguredProvider(IConfiguration config)
        {
            string value = config?[DefaultProviderKey];
            if (string.IsNullOrWhiteSpace(value))
            {
                value = config?[ProviderKey];
            }
            if (string.IsNullOrWhiteSpace(value))
            {
                return StripeProviderName;
            }

            return TryNormalizeChargePointProvider(value, out var normalized) && normalized != null
                ? normalized
                : value.Trim();
        }

        public static bool IsStripeProvider(IConfiguration config)
        {
            return string.Equals(GetConfiguredProvider(config), StripeProviderName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True when Stripe is the configured provider and no Stripe webhook signing secret is configured.
        /// </summary>
        public static bool ShouldWarnMissingStripeWebhookSecret(IConfiguration config)
        {
            return IsStripeProvider(config) && string.IsNullOrWhiteSpace(config?[StripeWebhookSecretKey]);
        }

        /// <summary>
        /// Normalizes a charge point's provider choice. Null, empty or whitespace means "use the default provider"
        /// and yields null; a known provider yields its canonical name. Returns false for an unknown provider.
        /// </summary>
        public static bool TryNormalizeChargePointProvider(string value, out string normalized)
        {
            normalized = null;
            if (string.IsNullOrWhiteSpace(value))
            {
                return true;
            }

            var trimmed = value.Trim();
            foreach (var provider in ChargePointProviders)
            {
                if (string.Equals(provider, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    normalized = provider;
                    return true;
                }
            }

            return false;
        }

        /// <summary>Operator-facing provider name ("Saferpay" is shown as "Worldline Saferpay").</summary>
        public static string DisplayName(string provider)
        {
            return string.Equals(provider, SaferpayProviderName, StringComparison.OrdinalIgnoreCase)
                ? "Worldline Saferpay"
                : provider;
        }
    }
}
