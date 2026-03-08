using System;
using System.Collections.Generic;

namespace OCPP.Core.Database
{
    public static class ChargePaymentReservationState
    {
        public const string Pending = "PendingPayment";
        public const string Authorized = "Authorized";
        public const string StartRequested = "StartRequested";
        public const string StartRejected = "StartRejected";
        public const string StartTimeout = "StartTimeout";
        public const string Abandoned = "Abandoned";
        public const string Charging = "Charging";
        public const string Completed = "Completed";
        public const string Cancelled = "Cancelled";
        public const string Failed = "Failed";

        public static readonly string[] InactiveStatuses =
        {
            Completed,
            Cancelled,
            Failed,
            StartRejected,
            StartTimeout,
            Abandoned
        };

        public static readonly string[] ConnectorLockStatuses =
        {
            Pending,
            Authorized,
            StartRequested,
            Charging
        };

        private static readonly HashSet<string> InactiveStatusSet = new HashSet<string>(InactiveStatuses, StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> ConnectorLockStatusSet = new HashSet<string>(ConnectorLockStatuses, StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> CancelableStatusSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Pending,
            Authorized,
            StartRequested
        };

        public static bool IsActive(string status)
        {
            return !string.IsNullOrWhiteSpace(status) && !InactiveStatusSet.Contains(status);
        }

        public static bool IsCancelable(string status)
        {
            return !string.IsNullOrWhiteSpace(status) && CancelableStatusSet.Contains(status);
        }

        public static bool IsTerminal(string status)
        {
            return !string.IsNullOrWhiteSpace(status) && InactiveStatusSet.Contains(status);
        }

        public static bool LocksConnector(string status)
        {
            return !string.IsNullOrWhiteSpace(status) && ConnectorLockStatusSet.Contains(status);
        }
    }
}
