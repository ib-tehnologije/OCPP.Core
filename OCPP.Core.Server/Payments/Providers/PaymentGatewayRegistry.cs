/*
 * OCPP.Core - https://github.com/dallmann-consulting/OCPP.Core
 */

using System;
using System.Collections.Generic;
using System.Linq;

namespace OCPP.Core.Server.Payments.Providers
{
    /// <summary>
    /// Default <see cref="IPaymentGatewayRegistry"/>: the active gateway serves new checkouts, and each
    /// existing reservation is routed to the gateway named by its persisted provider. An unknown or
    /// unregistered provider resolves to null so callers fail closed instead of calling another PSP.
    /// </summary>
    public sealed class PaymentGatewayRegistry : IPaymentGatewayRegistry
    {
        private readonly Dictionary<string, IPaymentGateway> _gatewaysByName =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly List<IPaymentGateway> _gateways = new();

        public PaymentGatewayRegistry(IEnumerable<IPaymentGateway> gateways, string activeProvider)
        {
            foreach (var gateway in gateways ?? Enumerable.Empty<IPaymentGateway>())
            {
                if (gateway == null)
                {
                    continue;
                }

                var name = gateway.ProviderName?.Trim();
                if (string.IsNullOrWhiteSpace(name))
                {
                    throw new InvalidOperationException(
                        $"Payment gateway '{gateway.GetType().Name}' does not report a provider name.");
                }

                if (_gatewaysByName.ContainsKey(name))
                {
                    throw new InvalidOperationException(
                        $"More than one payment gateway is registered for provider '{name}'.");
                }

                _gatewaysByName[name] = gateway;
                _gateways.Add(gateway);
            }

            var activeName = string.IsNullOrWhiteSpace(activeProvider)
                ? PaymentProviderNames.Stripe
                : activeProvider.Trim();
            if (!_gatewaysByName.TryGetValue(activeName, out var active))
            {
                var registered = _gateways.Count == 0
                    ? "(none)"
                    : string.Join(", ", _gateways.Select(candidate => candidate.ProviderName));
                throw new InvalidOperationException(
                    $"Payments:Provider selects '{activeName}', but no '{activeName}' payment gateway is registered. Registered gateways: {registered}.");
            }

            Active = active;
        }

        public IPaymentGateway Active { get; }

        public IReadOnlyCollection<IPaymentGateway> All => _gateways;

        public IPaymentGateway Resolve(string providerName)
        {
            var name = string.IsNullOrWhiteSpace(providerName)
                ? PaymentProviderNames.Stripe
                : providerName.Trim();
            return _gatewaysByName.TryGetValue(name, out var gateway) ? gateway : null;
        }
    }
}
