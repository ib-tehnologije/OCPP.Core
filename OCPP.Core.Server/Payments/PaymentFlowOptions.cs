namespace OCPP.Core.Server.Payments
{
    public class PaymentFlowOptions
    {
        /// <summary>
        /// Minutes allowed between payment authorization and receiving StartTransaction; after this, the reservation is timed out and released.
        /// </summary>
        public int StartWindowMinutes { get; set; } = 5;

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

        /// <summary>
        /// Optional automatic stop threshold for sessions that remain in SuspendedEV.
        /// Set to 0 to disable.
        /// </summary>
        public int IdleAutoStopMinutes { get; set; } = 0;

        /// <summary>
        /// Minimum delivered energy required before the fixed session fee is charged.
        /// Missing or inconsistent delivered-energy readings never qualify for the fixed session fee.
        /// Set to 0 to allow the fixed session fee for any valid delivered-energy reading.
        /// </summary>
        public decimal MinimumSessionFeeKwh { get; set; } = 1.0m;

        /// <summary>
        /// Minimum positive capture amount in the payment currency minor unit.
        /// Set to 0 to disable the positive-amount minimum guard.
        /// </summary>
        public long MinimumChargeAmountCents { get; set; } = 50;
    }
}
