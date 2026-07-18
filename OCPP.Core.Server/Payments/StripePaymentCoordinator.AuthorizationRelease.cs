using System;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using OCPP.Core.Database;
using OCPP.Core.Server.Payments.Invoices;
using Stripe;

namespace OCPP.Core.Server.Payments
{
    public partial class StripePaymentCoordinator
    {
        private const string IdempotencyAuthorizationRelease = "authorization_release";
        private const int DefaultAuthorizationReleaseMaxAttempts = 4;
        private const int DefaultAuthorizationReleaseRetryBaseMinutes = 1;

        private static readonly Regex ProviderIdentifierPattern = new(
            @"\b(?:pi|ch|cs|req|cus|pm|evt|in|src|tok)_[A-Za-z0-9_]+\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex EmailPattern = new(
            @"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        public PaymentAuthorizationReleaseResult ReconcileTerminalPaymentAuthorization(
            OCPPCoreContext dbContext,
            ChargePaymentReservation reservation,
            string trigger)
        {
            if (!IsEnabled)
            {
                return SkippedResult();
            }
            if (dbContext == null) throw new ArgumentNullException(nameof(dbContext));
            if (reservation == null) return SkippedResult();
            if (!PaymentAuthorizationReleaseState.IsArmed(reservation.AuthorizationReleaseState))
            {
                return SkippedResult();
            }

            var now = _utcNow();
            if (reservation.AuthorizationReleaseNextAttemptAtUtc.HasValue &&
                reservation.AuthorizationReleaseNextAttemptAtUtc.Value > now)
            {
                return new PaymentAuthorizationReleaseResult
                {
                    Outcome = PaymentAuthorizationReleaseOutcome.SkippedNotEligible,
                    NextRetryAtUtc = reservation.AuthorizationReleaseNextAttemptAtUtc
                };
            }

            var maxAttempts = ResolveAuthorizationReleaseMaxAttempts();
            if (reservation.AuthorizationReleaseAttemptCount >= maxAttempts)
            {
                const string exhausted = "Authorization release retry budget exhausted.";
                reservation.AuthorizationReleaseState = PaymentAuthorizationReleaseState.PermanentFailure;
                reservation.AuthorizationReleaseNextAttemptAtUtc = null;
                reservation.AuthorizationReleaseLastError = exhausted;
                dbContext.SaveChanges();
                return new PaymentAuthorizationReleaseResult
                {
                    Outcome = PaymentAuthorizationReleaseOutcome.PermanentFailure,
                    Error = exhausted
                };
            }

            var attempt = BeginAuthorizationReleaseAttempt(dbContext, reservation, trigger, now);

            if (!string.Equals(reservation.Status, PaymentReservationStatus.Abandoned, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(reservation.Status, PaymentReservationStatus.Failed, StringComparison.OrdinalIgnoreCase))
            {
                return FinishReviewRequired(dbContext, reservation, attempt, "Reservation is not an eligible terminal no-charge status.");
            }

            if (string.IsNullOrWhiteSpace(reservation.StripePaymentIntentId))
            {
                return FinishReviewRequired(dbContext, reservation, attempt, "Payment authorization identifier is missing.");
            }

            if ((reservation.CapturedAmountCents ?? 0) > 0 || reservation.CapturedAtUtc.HasValue)
            {
                return FinishReviewRequired(dbContext, reservation, attempt, "Captured payment evidence exists.");
            }

            if (HasActiveTransaction(dbContext, reservation))
            {
                return FinishReviewRequired(dbContext, reservation, attempt, "An active transaction still exists for this reservation.");
            }

            if (InvoiceSubmissionLogLookup.HasSubmittedOrExternalInvoice(
                dbContext,
                reservation.ReservationId,
                _logger,
                "authorization release eligibility"))
            {
                return FinishReviewRequired(dbContext, reservation, attempt, "Submitted or external invoice evidence exists.");
            }

            PaymentIntent paymentIntent;
            try
            {
                paymentIntent = _paymentIntentService.Get(reservation.StripePaymentIntentId);
            }
            catch (StripeException ex)
            {
                return FinishProviderFailure(dbContext, reservation, attempt, ex, maxAttempts);
            }

            if (paymentIntent == null ||
                !string.Equals(paymentIntent.Id, reservation.StripePaymentIntentId, StringComparison.Ordinal))
            {
                return FinishReviewRequired(dbContext, reservation, attempt, "Provider payment authorization identifier is missing or inconsistent.");
            }

            attempt.ProviderStatus = Truncate(paymentIntent.Status, 50);
            attempt.AmountCapturableCents = paymentIntent.AmountCapturable;

            if (!ProviderOwnershipMatches(paymentIntent, reservation.ReservationId))
            {
                return FinishReviewRequired(dbContext, reservation, attempt, "Provider ownership metadata is missing or inconsistent.");
            }

            if (string.Equals(paymentIntent.Status, "canceled", StringComparison.OrdinalIgnoreCase))
            {
                return FinishReleased(
                    dbContext,
                    reservation,
                    attempt,
                    PaymentAuthorizationReleaseOutcome.AlreadyReleased,
                    paymentIntent.Status);
            }

            if (string.Equals(paymentIntent.Status, "succeeded", StringComparison.OrdinalIgnoreCase))
            {
                return FinishReviewRequired(dbContext, reservation, attempt, "Provider payment status is succeeded; automatic release is forbidden.");
            }

            if (!string.Equals(paymentIntent.Status, "requires_capture", StringComparison.OrdinalIgnoreCase) ||
                paymentIntent.AmountCapturable <= 0)
            {
                return FinishReviewRequired(
                    dbContext,
                    reservation,
                    attempt,
                    $"Provider payment status '{Truncate(paymentIntent.Status, 50) ?? "missing"}' is not an uncaptured authorization with a positive capturable amount.");
            }

            try
            {
                var canceled = _paymentIntentService.Cancel(
                    paymentIntent.Id,
                    BuildIdempotencyOptions(
                        IdempotencyAuthorizationRelease,
                        reservation.ReservationId,
                        attempt.AttemptNumber));

                if (canceled != null &&
                    !string.Equals(canceled.Status, "canceled", StringComparison.OrdinalIgnoreCase))
                {
                    return FinishReviewRequired(
                        dbContext,
                        reservation,
                        attempt,
                        $"Provider cancellation returned unexpected status '{Truncate(canceled.Status, 50) ?? "missing"}'.");
                }

                return FinishReleased(
                    dbContext,
                    reservation,
                    attempt,
                    PaymentAuthorizationReleaseOutcome.Released,
                    attempt.ProviderStatus);
            }
            catch (StripeException ex)
            {
                return FinishProviderFailure(dbContext, reservation, attempt, ex, maxAttempts);
            }
        }

        private PaymentAuthorizationReleaseAttempt BeginAuthorizationReleaseAttempt(
            OCPPCoreContext dbContext,
            ChargePaymentReservation reservation,
            string trigger,
            DateTime now)
        {
            reservation.AuthorizationReleaseAttemptCount++;
            reservation.AuthorizationReleaseState = PaymentAuthorizationReleaseState.InProgress;
            reservation.AuthorizationReleaseLastAttemptAtUtc = now;
            reservation.AuthorizationReleaseNextAttemptAtUtc = null;

            var attempt = new PaymentAuthorizationReleaseAttempt
            {
                PaymentAuthorizationReleaseAttemptId = Guid.NewGuid(),
                ReservationId = reservation.ReservationId,
                StripePaymentIntentId = Truncate(reservation.StripePaymentIntentId, 200),
                AttemptNumber = reservation.AuthorizationReleaseAttemptCount,
                Trigger = Truncate(string.IsNullOrWhiteSpace(trigger) ? "Unknown" : trigger, 50),
                StartedAtUtc = now,
                Outcome = PaymentAuthorizationReleaseOutcome.InProgress
            };

            dbContext.PaymentAuthorizationReleaseAttempts.Add(attempt);
            dbContext.SaveChanges();
            return attempt;
        }

        private PaymentAuthorizationReleaseResult FinishReleased(
            OCPPCoreContext dbContext,
            ChargePaymentReservation reservation,
            PaymentAuthorizationReleaseAttempt attempt,
            string outcome,
            string providerStatus)
        {
            var now = _utcNow();
            attempt.FinishedAtUtc = now;
            attempt.ProviderStatus = Truncate(providerStatus, 50);
            attempt.Outcome = outcome;
            attempt.NextRetryAtUtc = null;
            attempt.ErrorCode = null;
            attempt.ErrorMessage = null;

            reservation.AuthorizationReleaseState = PaymentAuthorizationReleaseState.Released;
            reservation.AuthorizationReleasedAtUtc = now;
            reservation.AuthorizationReleaseNextAttemptAtUtc = null;
            reservation.AuthorizationReleaseLastError = null;
            dbContext.SaveChanges();

            return new PaymentAuthorizationReleaseResult
            {
                Outcome = outcome,
                ProviderStatus = providerStatus
            };
        }

        private PaymentAuthorizationReleaseResult FinishReviewRequired(
            OCPPCoreContext dbContext,
            ChargePaymentReservation reservation,
            PaymentAuthorizationReleaseAttempt attempt,
            string message)
        {
            var sanitized = SanitizeProviderError(message);
            attempt.FinishedAtUtc = _utcNow();
            attempt.Outcome = PaymentAuthorizationReleaseOutcome.ReviewRequired;
            attempt.ErrorMessage = sanitized;
            attempt.NextRetryAtUtc = null;

            reservation.AuthorizationReleaseState = PaymentAuthorizationReleaseState.ReviewRequired;
            reservation.AuthorizationReleaseNextAttemptAtUtc = null;
            reservation.AuthorizationReleaseLastError = sanitized;
            dbContext.SaveChanges();

            return new PaymentAuthorizationReleaseResult
            {
                Outcome = PaymentAuthorizationReleaseOutcome.ReviewRequired,
                ProviderStatus = attempt.ProviderStatus,
                Error = sanitized
            };
        }

        private PaymentAuthorizationReleaseResult FinishProviderFailure(
            OCPPCoreContext dbContext,
            ChargePaymentReservation reservation,
            PaymentAuthorizationReleaseAttempt attempt,
            StripeException exception,
            int maxAttempts)
        {
            var now = _utcNow();
            var code = Truncate(
                exception?.StripeError?.Code ??
                exception?.StripeError?.Type ??
                "stripe_error",
                100);
            var message = SanitizeProviderError(
                exception?.StripeError?.Message ?? exception?.Message ?? "Provider request failed.");
            var shouldRetry = IsTransientProviderFailure(exception) && attempt.AttemptNumber < maxAttempts;
            var nextRetry = shouldRetry
                ? now.Add(ResolveAuthorizationReleaseRetryDelay(attempt.AttemptNumber))
                : (DateTime?)null;
            var outcome = shouldRetry
                ? PaymentAuthorizationReleaseOutcome.RetryScheduled
                : PaymentAuthorizationReleaseOutcome.PermanentFailure;

            attempt.FinishedAtUtc = now;
            attempt.Outcome = outcome;
            attempt.ErrorCode = code;
            attempt.ErrorMessage = message;
            attempt.NextRetryAtUtc = nextRetry;

            reservation.AuthorizationReleaseState = shouldRetry
                ? PaymentAuthorizationReleaseState.RetryScheduled
                : PaymentAuthorizationReleaseState.PermanentFailure;
            reservation.AuthorizationReleaseNextAttemptAtUtc = nextRetry;
            reservation.AuthorizationReleaseLastError = message;
            dbContext.SaveChanges();

            return new PaymentAuthorizationReleaseResult
            {
                Outcome = outcome,
                ProviderStatus = attempt.ProviderStatus,
                NextRetryAtUtc = nextRetry,
                Error = message
            };
        }

        private bool HasActiveTransaction(OCPPCoreContext dbContext, ChargePaymentReservation reservation)
        {
            return dbContext.Transactions.Any(transaction =>
                !transaction.StopTime.HasValue &&
                ((reservation.TransactionId.HasValue && transaction.TransactionId == reservation.TransactionId.Value) ||
                 (transaction.ChargePointId == reservation.ChargePointId &&
                  transaction.ConnectorId == reservation.ConnectorId &&
                  transaction.StartTagId == reservation.ChargeTagId)));
        }

        private static bool ProviderOwnershipMatches(PaymentIntent paymentIntent, Guid reservationId)
        {
            if (paymentIntent?.Metadata == null ||
                !paymentIntent.Metadata.TryGetValue("reservation_id", out var providerReservationId) ||
                !Guid.TryParse(providerReservationId, out var parsedReservationId))
            {
                return false;
            }

            return parsedReservationId == reservationId;
        }

        private int ResolveAuthorizationReleaseMaxAttempts()
        {
            var configured = _configuration?.GetValue<int?>("Maintenance:AuthorizationReleaseMaxAttempts") ??
                             DefaultAuthorizationReleaseMaxAttempts;
            return Math.Clamp(configured, 1, 10);
        }

        private TimeSpan ResolveAuthorizationReleaseRetryDelay(int attemptNumber)
        {
            var configuredBaseMinutes = _configuration?.GetValue<int?>("Maintenance:AuthorizationReleaseRetryBaseMinutes") ??
                                        DefaultAuthorizationReleaseRetryBaseMinutes;
            var baseMinutes = Math.Clamp(configuredBaseMinutes, 1, 60);
            var multiplier = Math.Pow(2, Math.Max(0, attemptNumber - 1));
            return TimeSpan.FromMinutes(Math.Min(24 * 60, baseMinutes * multiplier));
        }

        private static bool IsTransientProviderFailure(StripeException exception)
        {
            if (exception == null) return false;

            var statusCode = Convert.ToInt32(exception.HttpStatusCode);
            var type = exception.StripeError?.Type ?? string.Empty;
            return statusCode == 408 ||
                   statusCode == 409 ||
                   statusCode == 429 ||
                   statusCode >= 500 ||
                   string.Equals(type, "api_connection_error", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(type, "api_error", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(type, "rate_limit_error", StringComparison.OrdinalIgnoreCase);
        }

        private static string SanitizeProviderError(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var sanitized = EmailPattern.Replace(value, "[redacted-email]");
            sanitized = ProviderIdentifierPattern.Replace(sanitized, "[redacted-id]");
            return Truncate(sanitized, 500);
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            return value.Length <= maxLength ? value : value.Substring(0, maxLength);
        }

        private static PaymentAuthorizationReleaseResult SkippedResult() =>
            new() { Outcome = PaymentAuthorizationReleaseOutcome.SkippedNotEligible };
    }
}
