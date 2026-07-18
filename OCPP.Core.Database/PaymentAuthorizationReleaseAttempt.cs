using System;

#nullable disable

namespace OCPP.Core.Database
{
    public class PaymentAuthorizationReleaseAttempt
    {
        public Guid PaymentAuthorizationReleaseAttemptId { get; set; }
        public Guid ReservationId { get; set; }
        public string StripePaymentIntentId { get; set; }
        public int AttemptNumber { get; set; }
        public string Trigger { get; set; }
        public DateTime StartedAtUtc { get; set; }
        public DateTime? FinishedAtUtc { get; set; }
        public string ProviderStatus { get; set; }
        public long? AmountCapturableCents { get; set; }
        public string Outcome { get; set; }
        public string ErrorCode { get; set; }
        public string ErrorMessage { get; set; }
        public DateTime? NextRetryAtUtc { get; set; }

        public ChargePaymentReservation Reservation { get; set; }
    }
}
