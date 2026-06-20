using System.Runtime.Serialization;

namespace OCPP.Core.Server.Messages_OCPP16
{
    public class CancelReservationRequest
    {
        public int ReservationId { get; set; }
    }

    public class CancelReservationResponse
    {
        public CancelReservationStatus Status { get; set; }
    }

    public enum CancelReservationStatus
    {
        [EnumMember(Value = @"Accepted")]
        Accepted = 0,

        [EnumMember(Value = @"Rejected")]
        Rejected = 1
    }
}
