/*
 * OCPP.Core - https://github.com/dallmann-consulting/OCPP.Core
 */

using System;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OCPP.Core.Database;
using OCPP.Core.Server.Payments.Invoices;
using OCPP.Core.Server.Payments.Providers;

namespace OCPP.Core.Server.Payments
{
    public partial class PaymentCoordinator
    {
        private const string IdempotencyAuthorizationRelease = "authorization_release";
        private const int DefaultAuthorizationReleaseMaxAttempts = 4;
        private const int DefaultAuthorizationReleaseRetryBaseMinutes = 1;
        private const int DefaultAuthorizationReleaseInProgressTimeoutMinutes = 5;

        private static readonly Regex EmailPattern = new(
            @"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        public PaymentAuthorizationReleaseResult ReconcileTerminalPaymentAuthorization(
            OCPPCoreContext dbContext,
            ChargePaymentReservation reservation,
            string trigger)
        {
            if (!IsOperational)
            {
                return SkippedResult();
            }
            if (dbContext == null) throw new ArgumentNullException(nameof(dbContext));
            if (reservation == null) return SkippedResult();

            // Decide on the current row, not on a copy loaded before a racing confirmation, notification or
            // cancellation changed it.
            if (!RefreshReservationForRelease(dbContext, reservation))
            {
                return SkippedResult();
            }

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
            if (attempt == null)
            {
                // Another reconciliation took the lease (or the row changed) since it was read.
                return SkippedResult();
            }

            if (!IsTerminalNoChargeStatus(reservation.Status))
            {
                return FinishReviewRequired(dbContext, reservation, attempt, "Reservation is not an eligible terminal no-charge status.");
            }

            if ((reservation.CapturedAmountCents ?? 0) > 0 || reservation.CapturedAtUtc.HasValue)
            {
                return FinishReviewRequired(dbContext, reservation, attempt, "Captured payment evidence exists.");
            }

            // A reservation that never reached Authorized never had a charge tag or a remote start, so no
            // transaction can belong to it; only authorized reservations need the linkage-window evidence.
            if (!IsNeverAuthorized(reservation))
            {
                var transactionEvidence = Recovery.FinancialRecoveryAuthorizationAssessor.EvaluateTransactionEvidence(
                    dbContext,
                    reservation);
                if (!transactionEvidence.CanEvaluate)
                {
                    return FinishReviewRequired(dbContext, reservation, attempt, transactionEvidence.Reason);
                }

                if (transactionEvidence.HasTransaction)
                {
                    return FinishReviewRequired(dbContext, reservation, attempt, TransactionEvidenceReviewReason);
                }
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

            var gateway = ResolveGateway(reservation);
            if (!IsGatewayUsable(gateway))
            {
                // Fail closed: an authorization is only ever released by the provider that created it.
                return FinishProviderFailure(dbContext, reservation, attempt, ProviderUnavailable(reservation, gateway), maxAttempts);
            }

            PaymentAuthorizationSnapshot paymentIntent = null;
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

                PaymentCheckoutStatus checkoutSession;
                try
                {
                    checkoutSession = gateway.GetCheckout(reservation.StripeCheckoutSessionId);
                }
                catch (PaymentProviderException ex)
                {
                    return FinishProviderFailure(dbContext, reservation, attempt, ex, maxAttempts);
                }

                if (checkoutSession == null ||
                    !string.Equals(checkoutSession.SessionReference, reservation.StripeCheckoutSessionId, StringComparison.Ordinal))
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

                if (string.IsNullOrWhiteSpace(AuthorizationReferenceOf(checkoutSession)))
                {
                    var lifetimeEnd = CheckoutLifetimeEnd(gateway, reservation);
                    var closed = IsClosedWithoutAuthorization(checkoutSession);
                    if (!closed && lifetimeEnd.HasValue && now < lifetimeEnd.Value)
                    {
                        // The payer can still complete this checkout and the provider may not signal it: look again
                        // once the payment page can no longer be completed, without spending the retry budget.
                        return FinishWaitingForCheckoutLifetime(dbContext, reservation, attempt, checkoutSession, lifetimeEnd.Value);
                    }

                    if (!closed && !lifetimeEnd.HasValue)
                    {
                        if (IsOpenCheckoutOfNeverAuthorizedReservation(checkoutSession, reservation))
                        {
                            // Abandoned while the payer never paid: nothing is held now. Providers without a page
                            // lifetime signal a later completion, and that signal releases the late authorization.
                            return FinishReleased(
                                dbContext,
                                reservation,
                                attempt,
                                PaymentAuthorizationReleaseOutcome.AlreadyReleased,
                                checkoutSession.RawStatus ?? checkoutSession.State.ToString());
                        }

                        return FinishIndeterminateProviderResult(
                            dbContext,
                            reservation,
                            attempt,
                            "payment_intent_linkage_pending",
                            "Provider checkout session has not linked a payment authorization yet.",
                            maxAttempts);
                    }

                    // The checkout is closed (or its page lifetime is over) without an authorization reference.
                    // An expired checkout reference says nothing about an authorization made before it expired,
                    // so ask the provider by reservation before concluding that no funds are held.
                    PaymentAuthorizationSnapshot found;
                    try
                    {
                        found = gateway.FindAuthorizationByReservation(reservation.ReservationId);
                    }
                    catch (PaymentProviderException ex)
                    {
                        return FinishProviderFailure(dbContext, reservation, attempt, ex, maxAttempts);
                    }

                    if (found == null || string.IsNullOrWhiteSpace(found.AuthorizationReference))
                    {
                        // An expired, cancelled or failed checkout without an authorization holds no funds.
                        return FinishReleased(
                            dbContext,
                            reservation,
                            attempt,
                            PaymentAuthorizationReleaseOutcome.AlreadyReleased,
                            checkoutSession.RawStatus ?? checkoutSession.State.ToString());
                    }

                    if (!ProviderOwnershipMatches(found, reservation.ReservationId))
                    {
                        return FinishReviewRequired(
                            dbContext,
                            reservation,
                            attempt,
                            "Provider authorization found for the reservation has missing or inconsistent ownership.");
                    }

                    reservation.StripePaymentIntentId = found.AuthorizationReference;
                    reservation.UpdatedAtUtc = now;
                    attempt.StripePaymentIntentId = Truncate(found.AuthorizationReference, 200);
                    dbContext.SaveChanges();
                    paymentIntent = found;
                }
                else
                {
                    reservation.StripePaymentIntentId = AuthorizationReferenceOf(checkoutSession);
                    reservation.UpdatedAtUtc = now;
                    attempt.StripePaymentIntentId = Truncate(AuthorizationReferenceOf(checkoutSession), 200);
                    dbContext.SaveChanges();

                    // Use the authorization the provider returned with the checkout (Saferpay Assert): a separate
                    // lookup can lag behind it.
                    paymentIntent = InlineAuthorizationOf(checkoutSession, reservation.StripePaymentIntentId);
                }
            }

            if (paymentIntent == null)
            {
                try
                {
                    paymentIntent = gateway.GetAuthorization(reservation.StripePaymentIntentId);
                }
                catch (PaymentProviderException ex)
                {
                    return FinishProviderFailure(dbContext, reservation, attempt, ex, maxAttempts);
                }
            }

            if (paymentIntent == null ||
                !string.Equals(paymentIntent.AuthorizationReference, reservation.StripePaymentIntentId, StringComparison.Ordinal))
            {
                return FinishReviewRequired(dbContext, reservation, attempt, "Provider payment authorization identifier is missing or inconsistent.");
            }

            attempt.ProviderStatus = Truncate(paymentIntent.RawStatus, 50);
            attempt.AmountCapturableCents = paymentIntent.AmountCapturableMinor;

            if (!ProviderOwnershipMatches(paymentIntent, reservation.ReservationId))
            {
                return FinishReviewRequired(dbContext, reservation, attempt, "Provider ownership metadata is missing or inconsistent.");
            }

            if (paymentIntent.AmountReceivedMinor > 0)
            {
                return FinishReviewRequired(
                    dbContext,
                    reservation,
                    attempt,
                    "Provider payment authorization contains received funds; automatic release is forbidden.");
            }

            if (paymentIntent.State == PaymentAuthorizationState.Canceled)
            {
                return FinishReleased(
                    dbContext,
                    reservation,
                    attempt,
                    PaymentAuthorizationReleaseOutcome.AlreadyReleased,
                    paymentIntent.RawStatus);
            }

            if (paymentIntent.State == PaymentAuthorizationState.Captured)
            {
                return FinishReviewRequired(dbContext, reservation, attempt, $"Provider payment status is {CapturedStatusText(paymentIntent)}; automatic release is forbidden.");
            }

            if (paymentIntent.State != PaymentAuthorizationState.RequiresCapture ||
                paymentIntent.AmountCapturableMinor <= 0)
            {
                return FinishReviewRequired(
                    dbContext,
                    reservation,
                    attempt,
                    $"Provider payment status '{Truncate(paymentIntent.RawStatus, 50) ?? "missing"}' is not an uncaptured authorization with a positive capturable amount.");
            }

            try
            {
                var canceled = gateway.Cancel(
                    paymentIntent.AuthorizationReference,
                    BuildIdempotencyKey(
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

                if (canceled.State != PaymentAuthorizationState.Canceled)
                {
                    return FinishReviewRequired(
                        dbContext,
                        reservation,
                        attempt,
                        $"Provider cancellation returned unexpected status '{Truncate(canceled.RawStatus, 50) ?? "missing"}'.");
                }

                return FinishReleased(
                    dbContext,
                    reservation,
                    attempt,
                    PaymentAuthorizationReleaseOutcome.Released,
                    canceled.RawStatus);
            }
            catch (PaymentProviderException ex)
            {
                return FinishProviderFailure(dbContext, reservation, attempt, ex, maxAttempts);
            }
        }

        /// <summary>
        /// Takes the InProgress lease and records the attempt. The lease is taken with a conditional update, so of
        /// two reconciliations that read the same row only one proceeds; the other gets null. The attempt count is
        /// the retry budget of the current arming; attempt numbers keep increasing across re-armings.
        /// </summary>
        private PaymentAuthorizationReleaseAttempt BeginAuthorizationReleaseAttempt(
            OCPPCoreContext dbContext,
            ChargePaymentReservation reservation,
            string trigger,
            DateTime now)
        {
            var loadedStatus = PersistedStatus(dbContext, reservation);
            var loadedReleaseState = reservation.AuthorizationReleaseState;
            var loadedAttemptCount = reservation.AuthorizationReleaseAttemptCount;
            var attemptNumber = NextAuthorizationReleaseAttemptNumber(dbContext, reservation);

            reservation.AuthorizationReleaseAttemptCount++;
            reservation.AuthorizationReleaseState = PaymentAuthorizationReleaseState.InProgress;
            reservation.AuthorizationReleaseLastAttemptAtUtc = now;
            reservation.AuthorizationReleaseNextAttemptAtUtc = null;

            var attempt = new PaymentAuthorizationReleaseAttempt
            {
                PaymentAuthorizationReleaseAttemptId = Guid.NewGuid(),
                ReservationId = reservation.ReservationId,
                StripePaymentIntentId = Truncate(reservation.StripePaymentIntentId, 200),
                AttemptNumber = attemptNumber,
                Trigger = Truncate(string.IsNullOrWhiteSpace(trigger) ? "Unknown" : trigger, 50),
                StartedAtUtc = now,
                Outcome = PaymentAuthorizationReleaseOutcome.InProgress
            };

            // On the in-memory provider the lease is saved together with the attempt below. A lease whose attempt
            // row fails to save only expires (InProgress timeout); attempt numbers are derived from the saved rows.
            if (!TryCommitReservationTransition(
                    dbContext,
                    reservation,
                    loadedStatus,
                    row => row.AuthorizationReleaseState == loadedReleaseState &&
                           row.AuthorizationReleaseAttemptCount == loadedAttemptCount,
                    saveWhenNotRelational: false))
            {
                _logger.LogInformation(
                    "Payments/Release => Reservation changed before the release lease was taken reservation={ReservationId} status={Status} releaseState={ReleaseState}",
                    reservation.ReservationId,
                    reservation.Status ?? "(null)",
                    reservation.AuthorizationReleaseState ?? "(null)");
                return null;
            }

            dbContext.PaymentAuthorizationReleaseAttempts.Add(attempt);
            dbContext.SaveChanges();
            return attempt;
        }

        /// <summary>
        /// Attempt numbers stay unique per reservation (UX_AuthorizationReleaseAttempt_Reservation_Number) when a
        /// re-armed reservation starts a new retry budget.
        /// </summary>
        private static int NextAuthorizationReleaseAttemptNumber(OCPPCoreContext dbContext, ChargePaymentReservation reservation)
        {
            var reservationId = reservation.ReservationId;
            var highestRecorded = dbContext.PaymentAuthorizationReleaseAttempts
                .Where(candidate => candidate.ReservationId == reservationId)
                .Select(candidate => (int?)candidate.AttemptNumber)
                .Max() ?? 0;
            return Math.Max(reservation.AuthorizationReleaseAttemptCount + 1, highestRecorded + 1);
        }

        /// <summary>
        /// Arms the release reconciliation (due now). A reservation armed from a state that was not armed gets a new
        /// retry budget, so a hold found after an earlier release run ended can still be cancelled.
        /// </summary>
        internal static void ArmAuthorizationReleaseState(ChargePaymentReservation reservation) =>
            ArmAuthorizationReleaseState(reservation, firstAttemptAtUtc: null);

        /// <summary>
        /// Arms the release reconciliation with its first attempt at <paramref name="firstAttemptAtUtc"/> (null = due
        /// now). Deferring keeps the cleanup sweep from racing a caller that is about to cancel the hold itself.
        /// </summary>
        internal static void ArmAuthorizationReleaseState(ChargePaymentReservation reservation, DateTime? firstAttemptAtUtc)
        {
            if (reservation == null) return;
            if (!PaymentAuthorizationReleaseState.IsArmed(reservation.AuthorizationReleaseState))
            {
                reservation.AuthorizationReleaseAttemptCount = 0;
            }

            reservation.AuthorizationReleaseState = PaymentAuthorizationReleaseState.Pending;
            reservation.AuthorizationReleaseNextAttemptAtUtc = firstAttemptAtUtc;
        }

        private bool HasAuthorizationReleaseBudget(ChargePaymentReservation reservation) =>
            reservation != null &&
            reservation.AuthorizationReleaseAttemptCount < ResolveAuthorizationReleaseMaxAttempts();

        /// <summary>
        /// Reloads an unmodified tracked reservation from a relational database. False when the row no longer exists.
        /// A reservation with unsaved changes (or on the in-memory provider) is used as it is.
        /// </summary>
        private static bool RefreshReservationForRelease(OCPPCoreContext dbContext, ChargePaymentReservation reservation)
        {
            if (!dbContext.Database.IsRelational())
            {
                return true;
            }

            var entry = dbContext.Entry(reservation);
            if (entry.State != EntityState.Unchanged)
            {
                return entry.State != EntityState.Deleted;
            }

            entry.Reload();
            return entry.State != EntityState.Detached;
        }

        /// <summary>
        /// Open checkout without an authorization of a reservation that never reached Authorized (no start window,
        /// remote start or transaction): the payer has not paid.
        /// </summary>
        private static bool IsOpenCheckoutOfNeverAuthorizedReservation(PaymentCheckoutStatus checkout, ChargePaymentReservation reservation) =>
            checkout != null &&
            checkout.State == PaymentCheckoutState.Open &&
            !checkout.IsPaid &&
            string.IsNullOrWhiteSpace(AuthorizationReferenceOf(checkout)) &&
            IsNeverAuthorized(reservation);

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
            var sanitized = SanitizeProviderError(message, GatewayForSanitizing(reservation));
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
            PaymentProviderException exception,
            int maxAttempts)
        {
            var now = _utcNow();
            var code = Truncate(
                exception?.Code ??
                "provider_error",
                100);
            var message = SanitizeProviderError(
                exception?.ProviderMessage ?? exception?.Message ?? "Provider request failed.",
                GatewayForSanitizing(reservation));
            // A transient failure on the last mutation attempt still schedules one final,
            // read-only provider verification before the reservation becomes permanent failure.
            // The budget and backoff count the attempts of the current arming.
            var attemptInBudget = reservation.AuthorizationReleaseAttemptCount;
            var shouldRetry = exception?.IsTransient == true && attemptInBudget <= maxAttempts;
            var nextRetry = shouldRetry
                ? now.Add(ResolveAuthorizationReleaseRetryDelay(attemptInBudget))
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
            var sanitized = SanitizeProviderError(message, GatewayForSanitizing(reservation));
            var attemptInBudget = reservation.AuthorizationReleaseAttemptCount;
            var shouldRetry = attemptInBudget <= maxAttempts;
            var nextRetry = shouldRetry
                ? now.Add(ResolveAuthorizationReleaseRetryDelay(attemptInBudget))
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
            if (!IsTerminalNoChargeStatus(reservation.Status))
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

            if (!IsNeverAuthorized(reservation))
            {
                var transactionEvidence = Recovery.FinancialRecoveryAuthorizationAssessor.EvaluateTransactionEvidence(
                    dbContext,
                    reservation);
                if (!transactionEvidence.CanEvaluate)
                {
                    return FinishReservationOnly(
                        dbContext,
                        reservation,
                        PaymentAuthorizationReleaseState.ReviewRequired,
                        PaymentAuthorizationReleaseOutcome.ReviewRequired,
                        null,
                        transactionEvidence.Reason);
                }

                if (transactionEvidence.HasTransaction)
                {
                    return FinishReservationOnly(
                        dbContext,
                        reservation,
                        PaymentAuthorizationReleaseState.ReviewRequired,
                        PaymentAuthorizationReleaseOutcome.ReviewRequired,
                        null,
                        TransactionEvidenceReviewReason);
                }
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

            var gateway = ResolveGateway(reservation);
            if (!IsGatewayUsable(gateway))
            {
                return FinishReservationOnly(
                    dbContext,
                    reservation,
                    PaymentAuthorizationReleaseState.PermanentFailure,
                    PaymentAuthorizationReleaseOutcome.PermanentFailure,
                    null,
                    FormatProviderError(ProviderUnavailable(reservation, gateway), "Final provider verification failed."));
            }

            PaymentAuthorizationSnapshot paymentIntent = null;
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

                PaymentCheckoutStatus checkoutSession;
                try
                {
                    checkoutSession = gateway.GetCheckout(reservation.StripeCheckoutSessionId);
                }
                catch (PaymentProviderException ex)
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
                    !string.Equals(checkoutSession.SessionReference, reservation.StripeCheckoutSessionId, StringComparison.Ordinal) ||
                    !ProviderOwnershipMatches(checkoutSession, reservation.ReservationId))
                {
                    return FinishReservationOnly(
                        dbContext,
                        reservation,
                        PaymentAuthorizationReleaseState.ReviewRequired,
                        PaymentAuthorizationReleaseOutcome.ReviewRequired,
                        checkoutSession?.RawStatus,
                        "Provider checkout session is missing, inconsistent, or not owned by the reservation.");
                }

                PaymentAuthorizationSnapshot linked = null;
                if (string.IsNullOrWhiteSpace(AuthorizationReferenceOf(checkoutSession)))
                {
                    var lifetimeEnd = CheckoutLifetimeEnd(gateway, reservation);
                    var closed = IsClosedWithoutAuthorization(checkoutSession);
                    var now = _utcNow();
                    if (!closed && lifetimeEnd.HasValue && now < lifetimeEnd.Value)
                    {
                        return ScheduleAfterCheckoutLifetime(dbContext, reservation, checkoutSession, lifetimeEnd.Value);
                    }

                    if (!closed && !lifetimeEnd.HasValue)
                    {
                        if (IsOpenCheckoutOfNeverAuthorizedReservation(checkoutSession, reservation))
                        {
                            return FinishReservationOnly(
                                dbContext,
                                reservation,
                                PaymentAuthorizationReleaseState.Released,
                                PaymentAuthorizationReleaseOutcome.AlreadyReleased,
                                checkoutSession.RawStatus ?? checkoutSession.State.ToString(),
                                null);
                        }

                        return FinishReservationOnly(
                            dbContext,
                            reservation,
                            PaymentAuthorizationReleaseState.PermanentFailure,
                            PaymentAuthorizationReleaseOutcome.PermanentFailure,
                            checkoutSession.RawStatus,
                            "Authorization release retry budget exhausted before payment authorization linkage completed.");
                    }

                    try
                    {
                        linked = gateway.FindAuthorizationByReservation(reservation.ReservationId);
                    }
                    catch (PaymentProviderException ex)
                    {
                        return FinishReservationOnly(
                            dbContext,
                            reservation,
                            PaymentAuthorizationReleaseState.PermanentFailure,
                            PaymentAuthorizationReleaseOutcome.PermanentFailure,
                            null,
                            FormatProviderError(ex, "Provider authorization lookup failed."));
                    }

                    if (linked == null || string.IsNullOrWhiteSpace(linked.AuthorizationReference))
                    {
                        return FinishReservationOnly(
                            dbContext,
                            reservation,
                            PaymentAuthorizationReleaseState.Released,
                            PaymentAuthorizationReleaseOutcome.AlreadyReleased,
                            checkoutSession.RawStatus ?? checkoutSession.State.ToString(),
                            null);
                    }

                    if (!ProviderOwnershipMatches(linked, reservation.ReservationId))
                    {
                        return FinishReservationOnly(
                            dbContext,
                            reservation,
                            PaymentAuthorizationReleaseState.ReviewRequired,
                            PaymentAuthorizationReleaseOutcome.ReviewRequired,
                            linked.RawStatus,
                            "Provider authorization found for the reservation has missing or inconsistent ownership.");
                    }

                    reservation.StripePaymentIntentId = linked.AuthorizationReference;
                }
                else
                {
                    reservation.StripePaymentIntentId = AuthorizationReferenceOf(checkoutSession);
                    linked = InlineAuthorizationOf(checkoutSession, reservation.StripePaymentIntentId);
                }

                reservation.UpdatedAtUtc = _utcNow();
                dbContext.SaveChanges();
                paymentIntent = linked;
            }

            if (paymentIntent == null)
            {
                try
                {
                    paymentIntent = gateway.GetAuthorization(reservation.StripePaymentIntentId);
                }
                catch (PaymentProviderException ex)
                {
                    return FinishReservationOnly(
                        dbContext,
                        reservation,
                        PaymentAuthorizationReleaseState.PermanentFailure,
                        PaymentAuthorizationReleaseOutcome.PermanentFailure,
                        null,
                        FormatProviderError(ex, "Final provider verification failed."));
                }
            }

            if (paymentIntent == null ||
                !string.Equals(paymentIntent.AuthorizationReference, reservation.StripePaymentIntentId, StringComparison.Ordinal) ||
                !ProviderOwnershipMatches(paymentIntent, reservation.ReservationId))
            {
                return FinishReservationOnly(
                    dbContext,
                    reservation,
                    PaymentAuthorizationReleaseState.ReviewRequired,
                    PaymentAuthorizationReleaseOutcome.ReviewRequired,
                    paymentIntent?.RawStatus,
                    "Final provider verification returned missing, inconsistent, or unowned payment state.");
            }

            if (paymentIntent.AmountReceivedMinor > 0 ||
                paymentIntent.State == PaymentAuthorizationState.Captured)
            {
                return FinishReservationOnly(
                    dbContext,
                    reservation,
                    PaymentAuthorizationReleaseState.ReviewRequired,
                    PaymentAuthorizationReleaseOutcome.ReviewRequired,
                    paymentIntent.RawStatus,
                    "Final provider verification found received or succeeded payment evidence.");
            }

            if (paymentIntent.State == PaymentAuthorizationState.Canceled)
            {
                return FinishReservationOnly(
                    dbContext,
                    reservation,
                    PaymentAuthorizationReleaseState.Released,
                    PaymentAuthorizationReleaseOutcome.AlreadyReleased,
                    paymentIntent.RawStatus,
                    null);
            }

            return FinishReservationOnly(
                dbContext,
                reservation,
                PaymentAuthorizationReleaseState.PermanentFailure,
                PaymentAuthorizationReleaseOutcome.PermanentFailure,
                paymentIntent.RawStatus,
                $"Authorization release retry budget exhausted with provider status '{Truncate(paymentIntent.RawStatus, 50) ?? "missing"}'.");
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
            var sanitized = SanitizeProviderError(message, GatewayForSanitizing(reservation));
            reservation.AuthorizationReleaseState = state;
            reservation.AuthorizationReleaseLastAttemptAtUtc = now;
            reservation.AuthorizationReleaseNextAttemptAtUtc = null;
            reservation.AuthorizationReleaseLastError = sanitized;
            CloseLatestOpenReleaseAttempt(dbContext, reservation, outcome, providerStatus, sanitized, now);
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

        /// <summary>Finishes the latest attempt when it is still open (in progress or scheduled for a retry).</summary>
        private static void CloseLatestOpenReleaseAttempt(
            OCPPCoreContext dbContext,
            ChargePaymentReservation reservation,
            string outcome,
            string providerStatus,
            string message,
            DateTime now)
        {
            var reservationId = reservation.ReservationId;
            var latestAttempt = dbContext.PaymentAuthorizationReleaseAttempts
                .Where(candidate => candidate.ReservationId == reservationId)
                .OrderByDescending(candidate => candidate.AttemptNumber)
                .FirstOrDefault();
            if (latestAttempt == null ||
                (!string.Equals(latestAttempt.Outcome, PaymentAuthorizationReleaseOutcome.InProgress, StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(latestAttempt.Outcome, PaymentAuthorizationReleaseOutcome.RetryScheduled, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            latestAttempt.FinishedAtUtc = now;
            latestAttempt.ProviderStatus = Truncate(providerStatus, 50);
            latestAttempt.Outcome = outcome;
            latestAttempt.NextRetryAtUtc = null;
            if (!string.IsNullOrWhiteSpace(message))
            {
                latestAttempt.ErrorMessage = message;
            }
        }

        private static bool ProviderOwnershipMatches(PaymentAuthorizationSnapshot authorization, Guid reservationId) =>
            authorization?.OwnerReservationId.HasValue == true &&
            authorization.OwnerReservationId.Value == reservationId;

        private static bool ProviderOwnershipMatches(PaymentCheckoutStatus checkout, Guid reservationId) =>
            checkout?.OwnerReservationId.HasValue == true &&
            checkout.OwnerReservationId.Value == reservationId;

        private static string CapturedStatusText(PaymentAuthorizationSnapshot authorization) =>
            string.IsNullOrWhiteSpace(authorization?.RawStatus)
                ? "captured"
                : Truncate(authorization.RawStatus, 50);

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

        private static string FormatProviderError(PaymentProviderException exception, string fallback)
        {
            var code = exception?.Code ?? "provider_error";
            var message = exception?.ProviderMessage ?? exception?.Message ?? fallback;
            return $"{Truncate(code, 100)}: {message}";
        }

        /// <summary>
        /// Redacts e-mail addresses and PSP identifiers. Uses the reservation's gateway, or every
        /// registered gateway when the owning provider is unknown, so redaction never gets weaker.
        /// </summary>
        private string SanitizeProviderError(string value, IPaymentGateway gateway)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var sanitized = EmailPattern.Replace(value, "[redacted-email]");
            if (gateway != null)
            {
                sanitized = gateway.SanitizeProviderText(sanitized);
            }
            else
            {
                foreach (var candidate in _gateways.All)
                {
                    sanitized = candidate.SanitizeProviderText(sanitized);
                }
            }

            return Truncate(sanitized, 500);
        }

        private IPaymentGateway GatewayForSanitizing(ChargePaymentReservation reservation) => ResolveGateway(reservation);

        private static string AuthorizationReferenceOf(PaymentCheckoutStatus checkout) =>
            string.IsNullOrWhiteSpace(checkout?.AuthorizationReference)
                ? checkout?.Authorization?.AuthorizationReference
                : checkout.AuthorizationReference;

        private static bool IsClosedWithoutAuthorization(PaymentCheckoutStatus checkout) =>
            checkout != null &&
            string.IsNullOrWhiteSpace(AuthorizationReferenceOf(checkout)) &&
            (checkout.State == PaymentCheckoutState.Expired ||
             checkout.State == PaymentCheckoutState.Canceled ||
             checkout.State == PaymentCheckoutState.Failed);

        /// <summary>
        /// Authorization the provider returned together with the checkout, when it is the one just linked.
        /// Null when the provider returns none (Stripe) so the caller looks it up separately.
        /// </summary>
        private static PaymentAuthorizationSnapshot InlineAuthorizationOf(PaymentCheckoutStatus checkout, string authorizationReference)
        {
            var inline = checkout?.Authorization;
            if (inline == null ||
                string.IsNullOrWhiteSpace(inline.AuthorizationReference) ||
                !string.Equals(inline.AuthorizationReference, authorizationReference, StringComparison.Ordinal))
            {
                return null;
            }

            return inline;
        }

        /// <summary>Grace added to a provider's checkout lifetime before an open checkout is treated as closed.</summary>
        private static readonly TimeSpan CheckoutLifetimeGrace = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Time after which a checkout of a provider with a limited page lifetime can no longer be completed;
        /// null for providers that always signal late completions (<see cref="IPaymentGateway.CheckoutLifetime"/> null).
        /// </summary>
        private static DateTime? CheckoutLifetimeEnd(IPaymentGateway gateway, ChargePaymentReservation reservation)
        {
            var lifetime = gateway?.CheckoutLifetime;
            if (!lifetime.HasValue || lifetime.Value <= TimeSpan.Zero || reservation == null)
            {
                return null;
            }

            return reservation.CreatedAtUtc.Add(lifetime.Value).Add(CheckoutLifetimeGrace);
        }

        /// <summary>
        /// The checkout is still open, so it is too early to tell whether funds will be held. Schedules the next
        /// check for when the checkout can no longer complete. The attempt only read provider state, so it is not
        /// kept as a release attempt and does not count against the retry budget.
        /// </summary>
        private PaymentAuthorizationReleaseResult FinishWaitingForCheckoutLifetime(
            OCPPCoreContext dbContext,
            ChargePaymentReservation reservation,
            PaymentAuthorizationReleaseAttempt attempt,
            PaymentCheckoutStatus checkout,
            DateTime nextAttemptAtUtc)
        {
            dbContext.PaymentAuthorizationReleaseAttempts.Remove(attempt);
            reservation.AuthorizationReleaseAttemptCount = Math.Max(0, reservation.AuthorizationReleaseAttemptCount - 1);
            return ScheduleAfterCheckoutLifetime(dbContext, reservation, checkout, nextAttemptAtUtc);
        }

        private PaymentAuthorizationReleaseResult ScheduleAfterCheckoutLifetime(
            OCPPCoreContext dbContext,
            ChargePaymentReservation reservation,
            PaymentCheckoutStatus checkout,
            DateTime nextAttemptAtUtc)
        {
            var message = $"Provider checkout is still open; the next release check is scheduled for {nextAttemptAtUtc:u}.";
            reservation.AuthorizationReleaseState = PaymentAuthorizationReleaseState.RetryScheduled;
            reservation.AuthorizationReleaseNextAttemptAtUtc = nextAttemptAtUtc;
            reservation.AuthorizationReleaseLastError = message;
            dbContext.SaveChanges();

            _logger.LogInformation(
                "Payments/Release => Checkout still open reservation={ReservationId} status={Status} nextCheck={NextCheck:u}",
                reservation.ReservationId,
                checkout?.RawStatus ?? checkout?.State.ToString() ?? "(null)",
                nextAttemptAtUtc);

            return new PaymentAuthorizationReleaseResult
            {
                Outcome = PaymentAuthorizationReleaseOutcome.RetryScheduled,
                ProviderStatus = Truncate(checkout?.RawStatus, 50),
                NextRetryAtUtc = nextAttemptAtUtc,
                Error = message
            };
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
