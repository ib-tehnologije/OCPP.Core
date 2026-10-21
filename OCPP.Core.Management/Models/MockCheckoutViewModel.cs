using System;

namespace OCPP.Core.Management.Models
{
    public class MockCheckoutViewModel
    {
        public Guid ReservationId { get; set; }

        /// <summary>
        /// Optional provider checkout reference (the Stripe mock passes its session id). Null when the mocked
        /// provider has no session id in the checkout URL.
        /// </summary>
        public string SessionId { get; set; }

        /// <summary>
        /// Optional display name of the mocked payment provider (for example "Saferpay").
        /// </summary>
        public string ProviderName { get; set; }

        public string SuccessUrl { get; set; }
        public string CancelUrl { get; set; }

        public bool HasSessionId => !string.IsNullOrWhiteSpace(SessionId);

        public string Title => string.IsNullOrWhiteSpace(ProviderName)
            ? "Mock payment checkout"
            : $"Mock {ProviderName} checkout";
    }
}
