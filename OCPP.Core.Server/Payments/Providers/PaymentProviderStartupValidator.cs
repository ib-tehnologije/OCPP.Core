/*
 * OCPP.Core - https://github.com/dallmann-consulting/OCPP.Core
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using OCPP.Core.Database;

namespace OCPP.Core.Server.Payments.Providers
{
    /// <summary>
    /// Startup check of per-charge-point payment provider overrides. A charge point whose
    /// <c>PaymentProvider</c> names an unknown provider, or a provider that is not configured on this server,
    /// stops startup, so an override can never silently lose its payments or fall back to another provider.
    /// </summary>
    public static class PaymentProviderStartupValidator
    {
        /// <summary>
        /// Returns one message per charge point whose provider override cannot be used; empty when all are usable.
        /// Charge points without an override use <c>Payments:DefaultProvider</c> and are not checked here.
        /// </summary>
        public static IReadOnlyList<string> FindInvalidOverrides(OCPPCoreContext dbContext, IPaymentGatewayRegistry registry)
        {
            if (dbContext == null) throw new ArgumentNullException(nameof(dbContext));
            if (registry == null) throw new ArgumentNullException(nameof(registry));

            var overrides = dbContext.ChargePoints
                .Where(chargePoint => chargePoint.PaymentProvider != null && chargePoint.PaymentProvider != "")
                .Select(chargePoint => new { chargePoint.ChargePointId, chargePoint.PaymentProvider })
                .ToList();

            var errors = new List<string>();
            foreach (var chargePoint in overrides.Where(cp => !string.IsNullOrWhiteSpace(cp.PaymentProvider)).OrderBy(cp => cp.ChargePointId, StringComparer.Ordinal))
            {
                try
                {
                    PaymentProviderResolver.ResolveCheckoutGateway(registry, chargePoint.PaymentProvider);
                }
                catch (PaymentProviderUnavailableException unavailable)
                {
                    errors.Add($"Charge point '{chargePoint.ChargePointId}' uses payment provider '{chargePoint.PaymentProvider.Trim()}': {unavailable.Message}");
                }
            }

            return errors;
        }

        /// <summary>Throws <see cref="InvalidOperationException"/> listing every unusable charge point override.</summary>
        public static void Validate(OCPPCoreContext dbContext, IPaymentGatewayRegistry registry, ILogger logger = null)
        {
            var errors = FindInvalidOverrides(dbContext, registry);
            if (errors.Count == 0)
            {
                return;
            }

            foreach (var error in errors)
            {
                logger?.LogCritical("Payments => {Error}", error);
            }

            throw new InvalidOperationException(
                "Payment provider configuration is missing for charge point override(s). Configure the provider, or set the charge point back to the default provider. " +
                string.Join(" ", errors));
        }
    }
}
