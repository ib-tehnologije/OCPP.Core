using System;
using System.Runtime.Serialization;

namespace OCPP.Core.Server.Messages_OCPP16
{
    public class ReserveNowRequest
    {
        public int ConnectorId { get; set; }
        public string IdTag { get; set; }
        public DateTime ExpiryDate { get; set; }
        public int ReservationId { get; set; }
    }

    public class ReserveNowResponse
    {
        public ReserveNowStatus Status { get; set; }
    }

    public enum ReserveNowStatus
    {
        [EnumMember(Value = @"Accepted")]
        Accepted = 0,

        [EnumMember(Value = @"Faulted")]
        Faulted = 1,

        [EnumMember(Value = @"Occupied")]
        Occupied = 2,

        [EnumMember(Value = @"Rejected")]
        Rejected = 3,

        [EnumMember(Value = @"Unavailable")]
        Unavailable = 4
    }
}
