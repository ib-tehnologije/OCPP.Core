using System;

namespace OCPP.Core.Database
{
    /// <summary>
    /// Stores processed Stripe webhook events for idempotency/audit.
    /// </summary>
    public class StripeWebhookEvent
    {
        public string EventId { get; set; } // Stripe evt_*
        public string Type { get; set; }
        public DateTime? StripeCreatedAtUtc { get; set; }
        public DateTime ProcessedAtUtc { get; set; }
        public Guid? ReservationId { get; set; }
    }
}
