using System;

namespace OCPP.Core.Management.Models
{
    public class PublicStartViewModel
    {
        public string ChargePointId { get; set; }
        public string ChargePointName { get; set; }
        public int ConnectorId { get; set; }
        public string ConnectorName { get; set; }
        public string LastStatus { get; set; }
        public DateTime? LastStatusTime { get; set; }
        public string ChargeTagId { get; set; }
        public double MaxSessionKwh { get; set; }
        public decimal PricePerKwh { get; set; }
        public int StartUsageFeeAfterMinutes { get; set; }
        public int MaxUsageFeeMinutes { get; set; }
        public decimal ConnectorUsageFeePerMinute { get; set; }
        public string ErrorMessage { get; set; }
        public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    }
}
