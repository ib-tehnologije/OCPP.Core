using System;

namespace OCPP.Core.Management.Models
{
    public class PaymentResultViewModel
    {
        public string Status { get; set; }
        public string Message { get; set; }
        public bool Success { get; set; }
        public Guid ReservationId { get; set; }
    }
}
