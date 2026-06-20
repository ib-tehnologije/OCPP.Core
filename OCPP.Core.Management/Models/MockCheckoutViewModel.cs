using System;

namespace OCPP.Core.Management.Models
{
    public class MockCheckoutViewModel
    {
        public Guid ReservationId { get; set; }
        public string SessionId { get; set; }
        public string SuccessUrl { get; set; }
        public string CancelUrl { get; set; }
    }
}
