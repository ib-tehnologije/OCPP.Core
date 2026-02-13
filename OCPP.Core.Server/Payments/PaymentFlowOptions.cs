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

        /// <summary>
        /// Optional daily window (local time) during which idle-fee minutes are NOT billed.
        /// Format: "HH:mm-HH:mm" (example: "20:00-08:00").
        /// </summary>
        public string IdleFeeExcludedWindow { get; set; }

        /// <summary>
        /// Time zone id used to interpret <see cref="IdleFeeExcludedWindow"/> (IANA on Linux, Windows id on Windows).
        /// Examples: "Europe/Zagreb" (Linux), "Central European Standard Time" (Windows).
        /// </summary>
        public string IdleFeeExcludedTimeZoneId { get; set; }
    }
}
