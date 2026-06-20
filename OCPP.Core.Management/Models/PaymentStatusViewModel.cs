using System;

namespace OCPP.Core.Management.Models
{
    public class PaymentStatusViewModel
    {
        public Guid ReservationId { get; set; }
        public string Origin { get; set; }
        public string ApiPayload { get; set; }
        public bool ApiSuccess { get; set; }
        public string ApiError { get; set; }
        public string ServerApiUrl { get; set; }
    }
}
