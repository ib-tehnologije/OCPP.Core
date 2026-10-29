/*
 * OCPP.Core - https://github.com/dallmann-consulting/OCPP.Core
 */

using System;

namespace OCPP.Core.Server.Payments.Providers
{
    /// <summary>
    /// Provider selection and provider-neutral checkout settings, bound from the <c>Payments</c> section.
    /// Kept separate from <see cref="PaymentFlowOptions"/> because <see cref="PaymentFlowOptionsResolver"/>
    /// copies only the flow fields it knows about.
    /// </summary>
    public class PaymentProviderOptions
    {
        /// <summary>
        /// PSP used for new checkouts on charge points without their own <c>ChargePoint.PaymentProvider</c>:
        /// <c>Stripe</c> (default when empty) or <c>Saferpay</c>. Existing reservations keep using the
        /// provider that created them.
        /// </summary>
        public string DefaultProvider { get; set; }

        /// <summary>
        /// Legacy name of <see cref="DefaultProvider"/>, used when <see cref="DefaultProvider"/> is empty.
        /// Setting both to different providers is a configuration error.
        /// </summary>
        public string Provider { get; set; }

        /// <summary>Public base URL of the management app used for payer return links. Falls back to <c>Stripe:ReturnBaseUrl</c>.</summary>
        public string ReturnBaseUrl { get; set; }

        /// <summary>Reservation currency (ISO 4217). Falls back to <c>Stripe:Currency</c>, then <c>eur</c>.</summary>
        public string Currency { get; set; }

        /// <summary>Line item / payment description. Falls back to <c>Stripe:ProductName</c>.</summary>
        public string ProductName { get; set; }
    }

    /// <summary>Provider-neutral checkout settings after applying the legacy <c>Stripe:*</c> fallbacks.</summary>
    public sealed class PaymentCheckoutSettings
    {
        public const string DefaultCurrency = "eur";

        /// <summary>Provider for new checkouts on charge points without a provider override.</summary>
        public string DefaultProvider { get; init; } = PaymentProviderNames.Stripe;
        public string ReturnBaseUrl { get; init; }
        public string Currency { get; init; } = DefaultCurrency;
        public string ProductName { get; init; }

        /// <summary>
        /// Resolves settings. Throws <see cref="InvalidOperationException"/> for an unknown
        /// <see cref="PaymentProviderOptions.DefaultProvider"/> (or legacy <see cref="PaymentProviderOptions.Provider"/>)
        /// so a typo never silently falls back to Stripe, and when both keys name different providers.
        /// </summary>
        public static PaymentCheckoutSettings Resolve(PaymentProviderOptions providerOptions, StripeOptions stripeOptions)
        {
            var configuredDefault = providerOptions?.DefaultProvider;
            var configuredLegacy = providerOptions?.Provider;
            var defaultProvider = NormalizeConfigured(configuredDefault, "Payments:DefaultProvider");
            var legacyProvider = NormalizeConfigured(configuredLegacy, "Payments:Provider");
            if (!string.IsNullOrWhiteSpace(configuredDefault) &&
                !string.IsNullOrWhiteSpace(configuredLegacy) &&
                !string.Equals(defaultProvider, legacyProvider, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Payments:DefaultProvider ('{defaultProvider}') and the legacy Payments:Provider ('{legacyProvider}') select different payment providers. Set only Payments:DefaultProvider.");
            }

            return new PaymentCheckoutSettings
            {
                DefaultProvider = string.IsNullOrWhiteSpace(configuredDefault) ? legacyProvider : defaultProvider,
                ReturnBaseUrl = FirstNonEmpty(providerOptions?.ReturnBaseUrl, stripeOptions?.ReturnBaseUrl),
                Currency = (FirstNonEmpty(providerOptions?.Currency, stripeOptions?.Currency) ?? DefaultCurrency).ToLowerInvariant(),
                ProductName = FirstNonEmpty(providerOptions?.ProductName, stripeOptions?.ProductName)
            };
        }

        private static string NormalizeConfigured(string configured, string key)
        {
            var provider = PaymentProviderNames.Normalize(configured);
            if (provider == null)
            {
                throw new InvalidOperationException(
                    $"Unknown payment provider '{configured}' in {key}. Supported values: {string.Join(", ", PaymentProviderNames.Known)}.");
            }

            return provider;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return null;
        }
    }
}
