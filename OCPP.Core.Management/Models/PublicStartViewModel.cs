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
        public bool RequestR1Invoice { get; set; }
        public string BuyerCompanyName { get; set; }
        public string BuyerOib { get; set; }
        public double MaxSessionKwh { get; set; }
        public decimal PricePerKwh { get; set; }
        public decimal UserSessionFee { get; set; }
        public int StartUsageFeeAfterMinutes { get; set; }
        public int MaxUsageFeeMinutes { get; set; }
        public decimal ConnectorUsageFeePerMinute { get; set; }
        public bool UsageFeeAfterChargingEnds { get; set; }
        public bool FreeChargingEnabled { get; set; }
        public decimal EstimatedMaxHold { get; set; }
        public int MaxUsageFeeBillableMinutes { get; set; }
        public string CurrencySymbol { get; set; } = "€";
        public bool HasIdleFee => ConnectorUsageFeePerMinute > 0;
        public string ErrorMessage { get; set; }
        public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    }
}
