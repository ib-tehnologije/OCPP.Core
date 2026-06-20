using System;

namespace OCPP.Core.Server
{
    internal static class OcppConnectorStatus
    {
        public const string Available = "Available";
        public const string Preparing = "Preparing";
        public const string Charging = "Charging";
        public const string SuspendedEv = "SuspendedEV";
        public const string SuspendedEvse = "SuspendedEVSE";
        public const string Finishing = "Finishing";
        public const string Reserved = "Reserved";
        public const string Occupied = "Occupied";
        public const string Unavailable = "Unavailable";
        public const string Faulted = "Faulted";

        public static string Normalize(string rawStatus)
        {
            return string.IsNullOrWhiteSpace(rawStatus) ? null : rawStatus.Trim();
        }

        public static ConnectorStatusEnum ToConnectorStatusEnum(string rawStatus)
        {
            switch (Normalize(rawStatus))
            {
                case Available:
                    return ConnectorStatusEnum.Available;
                case Preparing:
                    return ConnectorStatusEnum.Preparing;
                case Unavailable:
                    return ConnectorStatusEnum.Unavailable;
                case Faulted:
                    return ConnectorStatusEnum.Faulted;
                case null:
                    return ConnectorStatusEnum.Undefined;
                default:
                    return ConnectorStatusEnum.Occupied;
            }
        }

        public static bool IsStartable(string rawStatus)
        {
            string normalized = Normalize(rawStatus);
            return string.Equals(normalized, Available, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, Preparing, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsSuspendedEv(string rawStatus)
        {
            return string.Equals(Normalize(rawStatus), SuspendedEv, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsSuspendedEvse(string rawStatus)
        {
            return string.Equals(Normalize(rawStatus), SuspendedEvse, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsChargingLike(string rawStatus)
        {
            string normalized = Normalize(rawStatus);
            return string.Equals(normalized, Charging, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, SuspendedEv, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, SuspendedEvse, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, Finishing, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, Occupied, StringComparison.OrdinalIgnoreCase);
        }
    }
}
