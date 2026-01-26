using System;
using System.Runtime.Serialization;

namespace OCPP.Core.Server.Messages_OCPP21
{
    public class ReserveNowRequest
    {
        public EVSEType Evse { get; set; }
        public IdTokenType IdToken { get; set; }
        public DateTime ExpiryDateTime { get; set; }
        public int Id { get; set; }
    }

    public class ReserveNowResponse
    {
        public ReserveNowStatusEnumType Status { get; set; }
    }

    public enum ReserveNowStatusEnumType
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
