using System;
using System.Collections.Generic;
using System.Globalization;

namespace OCPP.Core.Management.Models
{
    public class PublicMapViewModel
    {
        public List<PublicMapChargePoint> ChargePoints { get; set; } = new List<PublicMapChargePoint>();
        public string IdleFeeExcludedWindow { get; set; }
        public bool HasIdleFeeExcludedWindow => !string.IsNullOrWhiteSpace(IdleFeeExcludedWindow);
        public string CurrencySymbol { get; set; } = "€";
    }

    public class PublicMapChargePoint
    {
        public string ChargePointId { get; set; }
        public string Name { get; set; }
        public int ConnectorCount { get; set; }
        public int AvailableConnectorCount { get; set; }
        public int OccupiedConnectorCount { get; set; }
        public int OfflineConnectorCount { get; set; }
        public bool HasMultipleConnectors => ConnectorCount > 1;
        public string Status { get; set; }
        public DateTime? StatusTime { get; set; }
        public string PublicDisplayCode { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public string NavigationUrl
        {
            get
            {
                if (!Latitude.HasValue || !Longitude.HasValue)
                {
                    return null;
                }

                double latitude = Latitude.Value;
                double longitude = Longitude.Value;
                if (!double.IsFinite(latitude)
                    || !double.IsFinite(longitude)
                    || latitude < -90
                    || latitude > 90
                    || longitude < -180
                    || longitude > 180)
                {
                    return null;
                }

                return $"https://www.google.com/maps/dir/?api=1&destination={latitude.ToString("R", CultureInfo.InvariantCulture)},{longitude.ToString("R", CultureInfo.InvariantCulture)}";
            }
        }
        public string LocationDescription { get; set; }
        public decimal PricePerKwh { get; set; }
        public decimal UserSessionFee { get; set; }
        public decimal ConnectorUsageFeePerMinute { get; set; }
        public int StartUsageFeeAfterMinutes { get; set; }
        public bool UsageFeeAfterChargingEnds { get; set; }
    }
}
