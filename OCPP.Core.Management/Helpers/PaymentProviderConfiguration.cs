/*
 * OCPP.Core - https://github.com/dallmann-consulting/OCPP.Core
 */

using System;
using Microsoft.Extensions.Configuration;

namespace OCPP.Core.Management.Helpers
{
    /// <summary>
    /// Management-side view of the payment provider selected with "Payments:Provider".
    /// The OCPP server owns the provider integration; the management app only reads this key to decide
    /// which operator configuration hints apply. An empty value means the default provider (Stripe).
    /// </summary>
    public static class PaymentProviderConfiguration
    {
        public const string ProviderKey = "Payments:Provider";
        public const string StripeProviderName = "Stripe";
        /// <summary>
        /// Stripe signing secret key. As an environment variable it is "Stripe__WebhookSecret", because
        /// configuration uses AddEnvironmentVariables() without a prefix.
        /// </summary>
        public const string StripeWebhookSecretKey = "Stripe:WebhookSecret";

        public static string GetConfiguredProvider(IConfiguration config)
        {
            string value = config?[ProviderKey];
            return string.IsNullOrWhiteSpace(value) ? StripeProviderName : value.Trim();
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
    }
}
