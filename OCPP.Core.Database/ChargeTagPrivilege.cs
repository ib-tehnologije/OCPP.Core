/*
 * OCPP.Core - https://github.com/dallmann-consulting/OCPP.Core
 */

using System;

namespace OCPP.Core.Database
{
    /// <summary>
    /// Optional per-tag privilege that can allow free charging globally or per charge point.
    /// </summary>
    public partial class ChargeTagPrivilege
    {
        public int Id { get; set; }
        public string TagId { get; set; }
        public string ChargePointId { get; set; }
        public bool FreeChargingEnabled { get; set; }
        public string Note { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime? UpdatedAtUtc { get; set; }

        public virtual ChargeTag Tag { get; set; }
        public virtual ChargePoint ChargePoint { get; set; }
    }
}
