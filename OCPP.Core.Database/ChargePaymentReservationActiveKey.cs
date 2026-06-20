using System;
using System.Collections.Generic;

namespace OCPP.Core.Database
{
    public static class ChargePaymentReservationActiveKey
    {
        public const string ActiveValue = "ACTIVE";

        private static readonly HashSet<string> InactiveStatuses = new HashSet<string>(ChargePaymentReservationState.InactiveStatuses, StringComparer.OrdinalIgnoreCase);

        public static bool RequiresManualSync(string providerName)
        {
            return string.Equals(providerName, "Microsoft.EntityFrameworkCore.InMemory", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(providerName, "Microsoft.EntityFrameworkCore.Sqlite", StringComparison.OrdinalIgnoreCase);
        }

        public static string Compute(Guid reservationId, string status)
        {
            if (string.IsNullOrWhiteSpace(status) || !InactiveStatuses.Contains(status))
            {
                return ActiveValue;
            }

            return reservationId == Guid.Empty
                ? Guid.NewGuid().ToString().ToUpperInvariant()
                : reservationId.ToString().ToUpperInvariant();
        }
    }
}
