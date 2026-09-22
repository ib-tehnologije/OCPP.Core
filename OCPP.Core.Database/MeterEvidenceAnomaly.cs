using System;

#nullable disable

namespace OCPP.Core.Database
{
    public class MeterEvidenceAnomaly
    {
        public long MeterEvidenceAnomalyId { get; set; }
        public int TransactionId { get; set; }
        public string ChargePointId { get; set; }
        public int ConnectorId { get; set; }
        public DateTime ObservedAtUtc { get; set; }
        public string Protocol { get; set; }
        public string Source { get; set; }
        public string RawValue { get; set; }
        public string RawUnit { get; set; }
        public int RawUnitMultiplier { get; set; }
        public string EvidenceKey { get; set; }
        public double? NormalizedMeterKwh { get; set; }
        public string Outcome { get; set; }
        public string Reason { get; set; }
        public double? AcceptedMeterKwh { get; set; }
        public DateTime? AcceptedMeterAtUtc { get; set; }
        public double? TrustedMaximumPowerKw { get; set; }
        public DateTime CreatedAtUtc { get; set; }

        public virtual Transaction Transaction { get; set; }
    }
}
