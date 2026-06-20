using System;
using System.Collections.Generic;

namespace OCPP.Core.Management.Models
{
    public class OwnerReportViewModel
    {
        public DateTime StartDate { get; set; }
        public DateTime StopDate { get; set; }
        public List<OwnerReportItem> Owners { get; set; } = new List<OwnerReportItem>();
    }

    public class OwnerReportItem
    {
        public int? OwnerId { get; set; }
        public string OwnerName { get; set; }
        public string OwnerEmail { get; set; }
        public double EnergyKwh { get; set; }
        public decimal EnergyRevenue { get; set; }
        public decimal UsageFeeTotal { get; set; }
        public decimal UserSessionFeeTotal { get; set; }
        public decimal OwnerSessionFeeTotal { get; set; }
        public decimal OperatorCommissionTotal { get; set; }
        public decimal OperatorRevenueTotal { get; set; }
        public decimal OwnerPayoutTotal { get; set; }
        public int SessionCount { get; set; }
        public decimal GrossTotal => EnergyRevenue + UsageFeeTotal + UserSessionFeeTotal;
        public List<OwnerReportTransaction> Transactions { get; set; } = new List<OwnerReportTransaction>();
    }

    public class OwnerReportTransaction
    {
        public int TransactionId { get; set; }
        public string ChargePointId { get; set; }
        public string ChargePointName { get; set; }
        public int ConnectorId { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? StopTime { get; set; }
        public double EnergyKwh { get; set; }
        public decimal EnergyRevenue { get; set; }
        public decimal UsageFee { get; set; }
        public decimal UserSessionFee { get; set; }
        public decimal OwnerSessionFee { get; set; }
        public decimal OperatorCommission { get; set; }
        public decimal OperatorRevenueTotal { get; set; }
        public decimal OwnerPayoutTotal { get; set; }
    }
}
