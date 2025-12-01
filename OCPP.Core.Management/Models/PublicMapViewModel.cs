using System;
using System.Collections.Generic;

namespace OCPP.Core.Management.Models
{
    public class PublicMapViewModel
    {
        public List<PublicMapChargePoint> ChargePoints { get; set; } = new List<PublicMapChargePoint>();
        public string CurrencySymbol { get; set; } = "€";
    }

    public class PublicMapChargePoint
    {
        public string ChargePointId { get; set; }
        public string Name { get; set; }
        public int ConnectorCount { get; set; }
        public string Status { get; set; }
        public DateTime? StatusTime { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public string LocationDescription { get; set; }
        public decimal PricePerKwh { get; set; }
        public decimal UserSessionFee { get; set; }
        public decimal ConnectorUsageFeePerMinute { get; set; }
        public int StartUsageFeeAfterMinutes { get; set; }
        public bool UsageFeeAfterChargingEnds { get; set; }
    }
}
