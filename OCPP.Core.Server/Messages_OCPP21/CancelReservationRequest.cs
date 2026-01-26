namespace OCPP.Core.Server.Messages_OCPP21
{
    public class CancelReservationRequest
    {
        public int ReservationId { get; set; }
    }

    public class CancelReservationResponse
    {
        public CancelReservationStatusEnumType Status { get; set; }
    }

    public enum CancelReservationStatusEnumType
    {
        Accepted,
        Rejected
    }
}
