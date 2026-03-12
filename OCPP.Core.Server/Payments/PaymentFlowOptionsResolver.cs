using System;
using System.Linq;
using Microsoft.Extensions.Configuration;
using OCPP.Core.Database;

namespace OCPP.Core.Server.Payments
{
    internal static class PaymentFlowOptionsResolver
    {
        private const string DefaultIdleFeeExcludedTimeZoneId = "Europe/Zagreb";

        public static PaymentFlowOptions Resolve(IConfiguration configuration, OCPPCoreContext dbContext, PaymentFlowOptions fallback = null)
        {
            var options = new PaymentFlowOptions
            {
                StartWindowMinutes = fallback?.StartWindowMinutes ?? configuration?.GetValue<int?>("Payments:StartWindowMinutes") ?? 2,
                EnableReservationProfile = fallback?.EnableReservationProfile ?? configuration?.GetValue<bool?>("Payments:EnableReservationProfile") ?? false,
                IdleFeeExcludedWindow = fallback?.IdleFeeExcludedWindow ?? configuration?.GetValue<string>("Payments:IdleFeeExcludedWindow"),
                IdleFeeExcludedTimeZoneId = fallback?.IdleFeeExcludedTimeZoneId ?? configuration?.GetValue<string>("Payments:IdleFeeExcludedTimeZoneId") ?? DefaultIdleFeeExcludedTimeZoneId,
                IdleAutoStopMinutes = fallback?.IdleAutoStopMinutes ?? configuration?.GetValue<int?>("Payments:IdleAutoStopMinutes") ?? 0
            };

            if (dbContext == null)
            {
                return options;
            }

            var dbSettings = dbContext.PublicPortalSettings
                .OrderByDescending(x => x.UpdatedAtUtc)
                .FirstOrDefault();

            if (dbSettings?.IdleFeeExcludedWindowEnabled.HasValue == true)
            {
                if (dbSettings.IdleFeeExcludedWindowEnabled.Value)
                {
                    if (!string.IsNullOrWhiteSpace(dbSettings.IdleFeeExcludedWindow))
                    {
                        options.IdleFeeExcludedWindow = dbSettings.IdleFeeExcludedWindow.Trim();
                    }
                }
                else
                {
                    options.IdleFeeExcludedWindow = null;
                }
            }

            if (string.IsNullOrWhiteSpace(options.IdleFeeExcludedTimeZoneId))
            {
                options.IdleFeeExcludedTimeZoneId = DefaultIdleFeeExcludedTimeZoneId;
            }

            return options;
        }
    }
}
