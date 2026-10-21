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
        /// PSP used for new checkouts: <c>Stripe</c> (default when empty) or <c>Saferpay</c>.
        /// Existing reservations keep using the provider that created them.
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

        public string ActiveProvider { get; init; } = PaymentProviderNames.Stripe;
        public string ReturnBaseUrl { get; init; }
        public string Currency { get; init; } = DefaultCurrency;
        public string ProductName { get; init; }

        /// <summary>
        /// Resolves settings. Throws <see cref="InvalidOperationException"/> for an unknown
        /// <see cref="PaymentProviderOptions.Provider"/> so a typo never silently falls back to Stripe.
        /// </summary>
        public static PaymentCheckoutSettings Resolve(PaymentProviderOptions providerOptions, StripeOptions stripeOptions)
        {
            var configuredProvider = providerOptions?.Provider;
            var provider = PaymentProviderNames.Normalize(configuredProvider);
            if (provider == null)
            {
                throw new InvalidOperationException(
                    $"Unknown payment provider '{configuredProvider}' in Payments:Provider. Supported values: {string.Join(", ", PaymentProviderNames.Known)}.");
            }

            return new PaymentCheckoutSettings
            {
                ActiveProvider = provider,
                ReturnBaseUrl = FirstNonEmpty(providerOptions?.ReturnBaseUrl, stripeOptions?.ReturnBaseUrl),
                Currency = (FirstNonEmpty(providerOptions?.Currency, stripeOptions?.Currency) ?? DefaultCurrency).ToLowerInvariant(),
                ProductName = FirstNonEmpty(providerOptions?.ProductName, stripeOptions?.ProductName)
            };
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
