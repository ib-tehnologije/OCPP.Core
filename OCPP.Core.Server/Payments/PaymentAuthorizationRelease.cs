using System;

namespace OCPP.Core.Server.Payments
{
    public static class PaymentAuthorizationReleaseState
    {
        public const string Pending = "Pending";
        public const string InProgress = "InProgress";
        public const string RetryScheduled = "RetryScheduled";
        public const string Released = "Released";
        public const string ReviewRequired = "ReviewRequired";
        public const string PermanentFailure = "PermanentFailure";

        public static bool IsArmed(string state) =>
            string.Equals(state, Pending, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(state, InProgress, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(state, RetryScheduled, StringComparison.OrdinalIgnoreCase);
    }

    public static class PaymentAuthorizationReleaseOutcome
    {
        public const string InProgress = "InProgress";
        public const string Released = "Released";
        public const string AlreadyReleased = "AlreadyReleased";
        public const string RetryScheduled = "RetryScheduled";
        public const string PermanentFailure = "PermanentFailure";
        public const string ReviewRequired = "ReviewRequired";
        public const string SkippedNotEligible = "SkippedNotEligible";
    }

    public static class PaymentAuthorizationReleaseTrigger
    {
        public const string CleanupSweep = "CleanupSweep";
        public const string CheckoutCompletedWebhook = "CheckoutCompletedWebhook";
        public const string AmountCapturableWebhook = "AmountCapturableWebhook";
        public const string NoChargeCompletion = "NoChargeCompletion";
    }

    public sealed class PaymentAuthorizationReleaseResult
    {
        public string Outcome { get; set; }
        public string ProviderStatus { get; set; }
        public DateTime? NextRetryAtUtc { get; set; }
        public string Error { get; set; }
    }
}
