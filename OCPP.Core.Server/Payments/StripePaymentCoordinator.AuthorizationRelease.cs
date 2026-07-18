using System;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using OCPP.Core.Database;
using OCPP.Core.Server.Payments.Invoices;
using Stripe;
using Stripe.Checkout;

namespace OCPP.Core.Server.Payments
{
    public partial class StripePaymentCoordinator
    {
        private const string IdempotencyAuthorizationRelease = "authorization_release";
        private const int DefaultAuthorizationReleaseMaxAttempts = 4;
        private const int DefaultAuthorizationReleaseRetryBaseMinutes = 1;
        private const int DefaultAuthorizationReleaseInProgressTimeoutMinutes = 5;

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
            if (string.Equals(
                    reservation.AuthorizationReleaseState,
                    PaymentAuthorizationReleaseState.InProgress,
                    StringComparison.OrdinalIgnoreCase) &&
                reservation.AuthorizationReleaseLastAttemptAtUtc.HasValue &&
                reservation.AuthorizationReleaseLastAttemptAtUtc.Value > now.Subtract(ResolveAuthorizationReleaseInProgressTimeout()))
            {
                return SkippedResult();
            }

            var providerSignalTrigger = IsProviderSignalTrigger(trigger);
            if (reservation.AuthorizationReleaseNextAttemptAtUtc.HasValue &&
                reservation.AuthorizationReleaseNextAttemptAtUtc.Value > now &&
                !providerSignalTrigger)
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
                return VerifyAuthorizationReleaseAfterRetryBudget(dbContext, reservation);
            }

            var attempt = BeginAuthorizationReleaseAttempt(dbContext, reservation, trigger, now);

            if (!string.Equals(reservation.Status, PaymentReservationStatus.Abandoned, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(reservation.Status, PaymentReservationStatus.Failed, StringComparison.OrdinalIgnoreCase))
            {
                return FinishReviewRequired(dbContext, reservation, attempt, "Reservation is not an eligible terminal no-charge status.");
            }

            if ((reservation.CapturedAmountCents ?? 0) > 0 || reservation.CapturedAtUtc.HasValue)
            {
                return FinishReviewRequired(dbContext, reservation, attempt, "Captured payment evidence exists.");
            }

            if (HasActiveTransaction(dbContext, reservation))
            {
                return FinishReviewRequired(dbContext, reservation, attempt, "An active transaction still exists for this reservation.");
            }

            if (!InvoiceSubmissionLogLookup.TryHasSubmittedOrExternalInvoice(
                    dbContext,
                    reservation.ReservationId,
                    _logger,
                    "authorization release eligibility",
                    out var hasSubmittedOrExternalInvoice))
            {
                return FinishReviewRequired(
                    dbContext,
                    reservation,
                    attempt,
                    "Unable to verify invoice exclusion; automatic release is forbidden.");
            }

            if (hasSubmittedOrExternalInvoice)
            {
                return FinishReviewRequired(dbContext, reservation, attempt, "Submitted or external invoice evidence exists.");
            }

            if (string.IsNullOrWhiteSpace(reservation.StripePaymentIntentId))
            {
                if (string.IsNullOrWhiteSpace(reservation.StripeCheckoutSessionId))
                {
                    return FinishReviewRequired(
                        dbContext,
                        reservation,
                        attempt,
                        "Payment authorization identifier and checkout session identifier are missing.");
                }

                Session checkoutSession;
                try
                {
                    checkoutSession = _sessionService.Get(reservation.StripeCheckoutSessionId);
                }
                catch (StripeException ex)
                {
                    return FinishProviderFailure(dbContext, reservation, attempt, ex, maxAttempts);
                }

                if (checkoutSession == null ||
                    !string.Equals(checkoutSession.Id, reservation.StripeCheckoutSessionId, StringComparison.Ordinal))
                {
                    return FinishReviewRequired(
                        dbContext,
                        reservation,
                        attempt,
                        "Provider checkout session identifier is missing or inconsistent.");
                }

                if (!ProviderOwnershipMatches(checkoutSession, reservation.ReservationId))
                {
                    return FinishReviewRequired(
                        dbContext,
                        reservation,
                        attempt,
                        "Provider checkout session ownership metadata is missing or inconsistent.");
                }

                if (string.IsNullOrWhiteSpace(checkoutSession.PaymentIntentId))
                {
                    return FinishIndeterminateProviderResult(
                        dbContext,
                        reservation,
                        attempt,
                        "payment_intent_linkage_pending",
                        "Provider checkout session has not linked a payment authorization yet.",
                        maxAttempts);
                }

                reservation.StripePaymentIntentId = checkoutSession.PaymentIntentId;
                reservation.UpdatedAtUtc = now;
                attempt.StripePaymentIntentId = Truncate(checkoutSession.PaymentIntentId, 200);
                dbContext.SaveChanges();
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

            if (paymentIntent.AmountReceived > 0)
            {
                return FinishReviewRequired(
                    dbContext,
                    reservation,
                    attempt,
                    "Provider payment authorization contains received funds; automatic release is forbidden.");
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

                if (canceled == null)
                {
                    return FinishIndeterminateProviderResult(
                        dbContext,
                        reservation,
                        attempt,
                        "missing_cancel_response",
                        "Provider cancellation returned a missing status response.",
                        maxAttempts);
                }

                if (!string.Equals(canceled.Status, "canceled", StringComparison.OrdinalIgnoreCase))
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
                    canceled.Status);
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
            // A transient failure on the last mutation attempt still schedules one final,
            // read-only provider verification before the reservation becomes permanent failure.
            var shouldRetry = IsTransientProviderFailure(exception) && attempt.AttemptNumber <= maxAttempts;
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

        private PaymentAuthorizationReleaseResult FinishIndeterminateProviderResult(
            OCPPCoreContext dbContext,
            ChargePaymentReservation reservation,
            PaymentAuthorizationReleaseAttempt attempt,
            string errorCode,
            string message,
            int maxAttempts)
        {
            var now = _utcNow();
            var sanitized = SanitizeProviderError(message);
            var shouldRetry = attempt.AttemptNumber <= maxAttempts;
            var nextRetry = shouldRetry
                ? now.Add(ResolveAuthorizationReleaseRetryDelay(attempt.AttemptNumber))
                : (DateTime?)null;
            var outcome = shouldRetry
                ? PaymentAuthorizationReleaseOutcome.RetryScheduled
                : PaymentAuthorizationReleaseOutcome.PermanentFailure;

            attempt.FinishedAtUtc = now;
            attempt.Outcome = outcome;
            attempt.ErrorCode = Truncate(errorCode, 100);
            attempt.ErrorMessage = sanitized;
            attempt.NextRetryAtUtc = nextRetry;

            reservation.AuthorizationReleaseState = shouldRetry
                ? PaymentAuthorizationReleaseState.RetryScheduled
                : PaymentAuthorizationReleaseState.PermanentFailure;
            reservation.AuthorizationReleaseNextAttemptAtUtc = nextRetry;
            reservation.AuthorizationReleaseLastError = sanitized;
            dbContext.SaveChanges();

            return new PaymentAuthorizationReleaseResult
            {
                Outcome = outcome,
                ProviderStatus = attempt.ProviderStatus,
                NextRetryAtUtc = nextRetry,
                Error = sanitized
            };
        }

        private PaymentAuthorizationReleaseResult VerifyAuthorizationReleaseAfterRetryBudget(
            OCPPCoreContext dbContext,
            ChargePaymentReservation reservation)
        {
            if (!string.Equals(reservation.Status, PaymentReservationStatus.Abandoned, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(reservation.Status, PaymentReservationStatus.Failed, StringComparison.OrdinalIgnoreCase))
            {
                return FinishReservationOnly(
                    dbContext,
                    reservation,
                    PaymentAuthorizationReleaseState.ReviewRequired,
                    PaymentAuthorizationReleaseOutcome.ReviewRequired,
                    null,
                    "Reservation is not an eligible terminal no-charge status.");
            }

            if ((reservation.CapturedAmountCents ?? 0) > 0 || reservation.CapturedAtUtc.HasValue)
            {
                return FinishReservationOnly(
                    dbContext,
                    reservation,
                    PaymentAuthorizationReleaseState.ReviewRequired,
                    PaymentAuthorizationReleaseOutcome.ReviewRequired,
                    null,
                    "Captured payment evidence exists.");
            }

            if (HasActiveTransaction(dbContext, reservation))
            {
                return FinishReservationOnly(
                    dbContext,
                    reservation,
                    PaymentAuthorizationReleaseState.ReviewRequired,
                    PaymentAuthorizationReleaseOutcome.ReviewRequired,
                    null,
                    "An active transaction still exists for this reservation.");
            }

            if (!InvoiceSubmissionLogLookup.TryHasSubmittedOrExternalInvoice(
                    dbContext,
                    reservation.ReservationId,
                    _logger,
                    "authorization release final verification",
                    out var hasSubmittedOrExternalInvoice))
            {
                return FinishReservationOnly(
                    dbContext,
                    reservation,
                    PaymentAuthorizationReleaseState.ReviewRequired,
                    PaymentAuthorizationReleaseOutcome.ReviewRequired,
                    null,
                    "Unable to verify invoice exclusion; automatic release is forbidden.");
            }

            if (hasSubmittedOrExternalInvoice)
            {
                return FinishReservationOnly(
                    dbContext,
                    reservation,
                    PaymentAuthorizationReleaseState.ReviewRequired,
                    PaymentAuthorizationReleaseOutcome.ReviewRequired,
                    null,
                    "Submitted or external invoice evidence exists.");
            }

            if (string.IsNullOrWhiteSpace(reservation.StripePaymentIntentId))
            {
                if (string.IsNullOrWhiteSpace(reservation.StripeCheckoutSessionId))
                {
                    return FinishReservationOnly(
                        dbContext,
                        reservation,
                        PaymentAuthorizationReleaseState.PermanentFailure,
                        PaymentAuthorizationReleaseOutcome.PermanentFailure,
                        null,
                        "Authorization release retry budget exhausted without provider identifiers.");
                }

                Session checkoutSession;
                try
                {
                    checkoutSession = _sessionService.Get(reservation.StripeCheckoutSessionId);
                }
                catch (StripeException ex)
                {
                    return FinishReservationOnly(
                        dbContext,
                        reservation,
                        PaymentAuthorizationReleaseState.PermanentFailure,
                        PaymentAuthorizationReleaseOutcome.PermanentFailure,
                        null,
                        FormatProviderError(ex, "Provider checkout verification failed."));
                }

                if (checkoutSession == null ||
                    !string.Equals(checkoutSession.Id, reservation.StripeCheckoutSessionId, StringComparison.Ordinal) ||
                    !ProviderOwnershipMatches(checkoutSession, reservation.ReservationId))
                {
                    return FinishReservationOnly(
                        dbContext,
                        reservation,
                        PaymentAuthorizationReleaseState.ReviewRequired,
                        PaymentAuthorizationReleaseOutcome.ReviewRequired,
                        checkoutSession?.Status,
                        "Provider checkout session is missing, inconsistent, or not owned by the reservation.");
                }

                if (string.IsNullOrWhiteSpace(checkoutSession.PaymentIntentId))
                {
                    return FinishReservationOnly(
                        dbContext,
                        reservation,
                        PaymentAuthorizationReleaseState.PermanentFailure,
                        PaymentAuthorizationReleaseOutcome.PermanentFailure,
                        checkoutSession.Status,
                        "Authorization release retry budget exhausted before payment authorization linkage completed.");
                }

                reservation.StripePaymentIntentId = checkoutSession.PaymentIntentId;
                reservation.UpdatedAtUtc = _utcNow();
                dbContext.SaveChanges();
            }

            PaymentIntent paymentIntent;
            try
            {
                paymentIntent = _paymentIntentService.Get(reservation.StripePaymentIntentId);
            }
            catch (StripeException ex)
            {
                return FinishReservationOnly(
                    dbContext,
                    reservation,
                    PaymentAuthorizationReleaseState.PermanentFailure,
                    PaymentAuthorizationReleaseOutcome.PermanentFailure,
                    null,
                    FormatProviderError(ex, "Final provider verification failed."));
            }

            if (paymentIntent == null ||
                !string.Equals(paymentIntent.Id, reservation.StripePaymentIntentId, StringComparison.Ordinal) ||
                !ProviderOwnershipMatches(paymentIntent, reservation.ReservationId))
            {
                return FinishReservationOnly(
                    dbContext,
                    reservation,
                    PaymentAuthorizationReleaseState.ReviewRequired,
                    PaymentAuthorizationReleaseOutcome.ReviewRequired,
                    paymentIntent?.Status,
                    "Final provider verification returned missing, inconsistent, or unowned payment state.");
            }

            if (paymentIntent.AmountReceived > 0 ||
                string.Equals(paymentIntent.Status, "succeeded", StringComparison.OrdinalIgnoreCase))
            {
                return FinishReservationOnly(
                    dbContext,
                    reservation,
                    PaymentAuthorizationReleaseState.ReviewRequired,
                    PaymentAuthorizationReleaseOutcome.ReviewRequired,
                    paymentIntent.Status,
                    "Final provider verification found received or succeeded payment evidence.");
            }

            if (string.Equals(paymentIntent.Status, "canceled", StringComparison.OrdinalIgnoreCase))
            {
                return FinishReservationOnly(
                    dbContext,
                    reservation,
                    PaymentAuthorizationReleaseState.Released,
                    PaymentAuthorizationReleaseOutcome.AlreadyReleased,
                    paymentIntent.Status,
                    null);
            }

            return FinishReservationOnly(
                dbContext,
                reservation,
                PaymentAuthorizationReleaseState.PermanentFailure,
                PaymentAuthorizationReleaseOutcome.PermanentFailure,
                paymentIntent.Status,
                $"Authorization release retry budget exhausted with provider status '{Truncate(paymentIntent.Status, 50) ?? "missing"}'.");
        }

        private PaymentAuthorizationReleaseResult FinishReservationOnly(
            OCPPCoreContext dbContext,
            ChargePaymentReservation reservation,
            string state,
            string outcome,
            string providerStatus,
            string message)
        {
            var now = _utcNow();
            var sanitized = SanitizeProviderError(message);
            reservation.AuthorizationReleaseState = state;
            reservation.AuthorizationReleaseLastAttemptAtUtc = now;
            reservation.AuthorizationReleaseNextAttemptAtUtc = null;
            reservation.AuthorizationReleaseLastError = sanitized;
            var latestAttempt = dbContext.PaymentAuthorizationReleaseAttempts
                .FirstOrDefault(candidate =>
                    candidate.ReservationId == reservation.ReservationId &&
                    candidate.AttemptNumber == reservation.AuthorizationReleaseAttemptCount);
            if (latestAttempt != null &&
                (string.Equals(latestAttempt.Outcome, PaymentAuthorizationReleaseOutcome.InProgress, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(latestAttempt.Outcome, PaymentAuthorizationReleaseOutcome.RetryScheduled, StringComparison.OrdinalIgnoreCase)))
            {
                latestAttempt.FinishedAtUtc = now;
                latestAttempt.ProviderStatus = Truncate(providerStatus, 50);
                latestAttempt.Outcome = outcome;
                latestAttempt.NextRetryAtUtc = null;
                if (!string.IsNullOrWhiteSpace(sanitized))
                {
                    latestAttempt.ErrorMessage = sanitized;
                }
            }
            if (string.Equals(state, PaymentAuthorizationReleaseState.Released, StringComparison.OrdinalIgnoreCase))
            {
                reservation.AuthorizationReleasedAtUtc = now;
            }
            dbContext.SaveChanges();

            return new PaymentAuthorizationReleaseResult
            {
                Outcome = outcome,
                ProviderStatus = Truncate(providerStatus, 50),
                Error = sanitized
            };
        }

        private bool HasActiveTransaction(OCPPCoreContext dbContext, ChargePaymentReservation reservation)
        {
            return dbContext.Transactions.Any(transaction =>
                !transaction.StopTime.HasValue &&
                ((reservation.TransactionId.HasValue && transaction.TransactionId == reservation.TransactionId.Value) ||
                 (transaction.ChargePointId == reservation.ChargePointId &&
                  transaction.ConnectorId == reservation.ConnectorId &&
                  !string.IsNullOrWhiteSpace(transaction.StartTagId) &&
                  (transaction.StartTagId == reservation.OcppIdTag ||
                   transaction.StartTagId == reservation.ChargeTagId))));
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

        private static bool ProviderOwnershipMatches(Session session, Guid reservationId)
        {
            if (session?.Metadata == null ||
                !session.Metadata.TryGetValue("reservation_id", out var providerReservationId) ||
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

        private TimeSpan ResolveAuthorizationReleaseInProgressTimeout()
        {
            var configuredMinutes = _configuration?.GetValue<int?>("Maintenance:AuthorizationReleaseInProgressTimeoutMinutes") ??
                                    DefaultAuthorizationReleaseInProgressTimeoutMinutes;
            return TimeSpan.FromMinutes(Math.Clamp(configuredMinutes, 1, 60));
        }

        private static bool IsProviderSignalTrigger(string trigger) =>
            string.Equals(trigger, PaymentAuthorizationReleaseTrigger.CheckoutCompletedWebhook, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trigger, PaymentAuthorizationReleaseTrigger.AmountCapturableWebhook, StringComparison.OrdinalIgnoreCase);

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

        private static string FormatProviderError(StripeException exception, string fallback)
        {
            var code = exception?.StripeError?.Code ?? exception?.StripeError?.Type ?? "stripe_error";
            var message = exception?.StripeError?.Message ?? exception?.Message ?? fallback;
            return $"{Truncate(code, 100)}: {message}";
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
