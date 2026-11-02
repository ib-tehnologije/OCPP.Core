/*
 * OCPP.Core - https://github.com/dallmann-consulting/OCPP.Core
 */

using System;
using System.Linq;

namespace OCPP.Core.Server.Payments.Providers
{
    /// <summary>
    /// The single rule that picks the payment provider for a NEW checkout on a charge point:
    /// <c>ChargePoint.PaymentProvider ?? Payments:DefaultProvider</c>. Existing reservations never go
    /// through this rule; they use the provider stored on the reservation (<see cref="IPaymentGatewayRegistry.Resolve"/>),
    /// so changing a charge point's provider does not move reservations that are already in flight.
    /// </summary>
    public static class PaymentProviderResolver
    {
        /// <summary>
        /// Provider name for a new checkout: the trimmed <paramref name="chargePointProvider"/> override, or
        /// <paramref name="defaultProvider"/> when the override is null, empty or whitespace (Stripe when both are empty).
        /// The name is matched case-insensitively against the registered gateways by <see cref="ResolveCheckoutGateway"/>.
        /// </summary>
        public static string ResolveProviderName(string chargePointProvider, string defaultProvider)
        {
            if (!string.IsNullOrWhiteSpace(chargePointProvider))
            {
                return chargePointProvider.Trim();
            }

            return string.IsNullOrWhiteSpace(defaultProvider) ? PaymentProviderNames.Stripe : defaultProvider.Trim();
        }

        /// <summary>
        /// Gateway for a new checkout on a charge point with <paramref name="chargePointProvider"/> as its override.
        /// Throws <see cref="PaymentProviderUnavailableException"/> when the provider is unknown (no registered
        /// gateway) or not configured; it never falls back to another provider.
        /// </summary>
        public static IPaymentGateway ResolveCheckoutGateway(IPaymentGatewayRegistry registry, string chargePointProvider)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));

            var name = ResolveProviderName(chargePointProvider, registry.Default?.ProviderName);
            var gateway = registry.Resolve(name);
            if (gateway == null)
            {
                var registered = registry.All == null || registry.All.Count == 0
                    ? "(none)"
                    : string.Join(", ", registry.All.Select(candidate => candidate.ProviderName));
                throw new PaymentProviderUnavailableException(
                    name,
                    PaymentProviderUnavailableReason.Unknown,
                    $"Unknown payment provider '{name}'. Registered providers: {registered}.");
            }

            if (!gateway.IsConfigured)
            {
                throw new PaymentProviderUnavailableException(
                    gateway.ProviderName,
                    PaymentProviderUnavailableReason.NotConfigured,
                    $"Payment provider '{gateway.ProviderName}' is not configured (check Enabled and the credentials in its configuration section).");
            }

            return gateway;
        }
    }

    public enum PaymentProviderUnavailableReason
    {
        /// <summary>No gateway is registered under the provider name.</summary>
        Unknown,
        /// <summary>The gateway is registered but lacks its settings or credentials.</summary>
        NotConfigured
    }

    /// <summary>A new checkout cannot be created because the charge point's provider cannot be used.</summary>
    public sealed class PaymentProviderUnavailableException : InvalidOperationException
    {
        public PaymentProviderUnavailableException(string provider, PaymentProviderUnavailableReason reason, string message)
            : base(message)
        {
            Provider = provider;
            Reason = reason;
        }

        public string Provider { get; }

        public PaymentProviderUnavailableReason Reason { get; }
    }
}
