namespace OCPP.Core.Server.Payments
{
    public class PaymentFlowOptions
    {
        /// <summary>
        /// Minutes allowed between payment authorization and receiving StartTransaction; after this, the reservation is timed out and released.
        /// </summary>
        public int StartWindowMinutes { get; set; } = 7;

        /// <summary>
        /// Enable charger-side reservation profile (ReserveNow/CancelReservation) when supported.
        /// </summary>
        public bool EnableReservationProfile { get; set; } = false;
    }
}
