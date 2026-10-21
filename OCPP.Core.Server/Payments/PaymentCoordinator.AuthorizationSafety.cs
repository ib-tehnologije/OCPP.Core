/*
 * OCPP.Core - https://github.com/dallmann-consulting/OCPP.Core
 */

using System;
using System.Collections.Concurrent;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OCPP.Core.Database;
using OCPP.Core.Server.Payments.Invoices;
using OCPP.Core.Server.Payments.Providers;

namespace OCPP.Core.Server.Payments
{
    /// <summary>
    /// Safety nets that keep a payer's funds from staying held (or charged) when a reservation ends without a
    /// charging session: late authorizations, checkouts captured at once, unconfirmed cancellations, and the
    /// browser-return/notification race on the charge tag.
    /// </summary>
    public partial class PaymentCoordinator
    {
        private const string LateAuthorizationReleasedReason = "late_authorization_released";
        private const string CapturedAtCheckoutCode = "captured_at_checkout";
        private const string CapturedAtCheckoutMessage = "Payment was captured immediately by the payment method; refund required.";
        private const string CapturePendingCode = "capture_pending";
        private const string CancelNotConfirmedCode = "cancel_not_confirmed";
        private const string PaymentStillProcessingStatus = "Pending";
        private const string PaymentStillProcessingMessage = "Payment is still being processed.";

        /// <summary>Minimum time between two provider checks of the same reservation triggered by a browser return.</summary>
        private static readonly TimeSpan ProviderCheckInterval = TimeSpan.FromSeconds(2);
        private const int ProviderCheckPruneThreshold = 1024;
        private readonly ConcurrentDictionary<Guid, DateTime> _lastProviderCheckAtUtc = new();

        /// <summary>How long a sent payment-authorized e-mail is remembered to suppress a racing second one.</summary>
        private static readonly TimeSpan AuthorizationEmailClaimRetention = TimeSpan.FromHours(1);
        private readonly ConcurrentDictionary<Guid, DateTime> _authorizationEmailClaimedAtUtc = new();

        /// <summary>Statuses in which a reservation ended without a charging session.</summary>
        private static bool IsTerminalNoChargeStatus(string status) =>
            string.Equals(status, PaymentReservationStatus.Cancelled, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, PaymentReservationStatus.Abandoned, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, PaymentReservationStatus.Failed, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, PaymentReservationStatus.StartRejected, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, PaymentReservationStatus.StartTimeout, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Terminal no-charge reservation: ended without a transaction and nothing was captured. Invoice evidence
        /// is checked separately because it needs the database.
        /// </summary>
        private static bool IsTerminalNoChargeCandidate(ChargePaymentReservation reservation) =>
            reservation != null &&
            IsTerminalNoChargeStatus(reservation.Status) &&
            !reservation.TransactionId.HasValue &&
            !reservation.StartTransactionId.HasValue &&
            (reservation.CapturedAmountCents ?? 0) == 0 &&
            !reservation.CapturedAtUtc.HasValue;

        /// <summary>
        /// The reservation never reached Authorized: no authorization time or start window, no remote start and no
        /// transaction linkage, so no transaction can belong to it. A charge tag alone is no evidence: it is claimed
        /// together with the authorization, and rows from before that could keep a tag whose authorization was
        /// never saved.
        /// </summary>
        private static bool IsNeverAuthorized(ChargePaymentReservation reservation) =>
            reservation != null &&
            !reservation.AuthorizedAtUtc.HasValue &&
            !reservation.StartDeadlineAtUtc.HasValue &&
            !reservation.RemoteStartSentAtUtc.HasValue &&
            !reservation.RemoteStartAcceptedAtUtc.HasValue &&
            string.IsNullOrWhiteSpace(reservation.RemoteStartResult) &&
            reservation.AwaitingPlug != true &&
            !reservation.StartTransactionAtUtc.HasValue &&
            !reservation.TransactionId.HasValue &&
            !reservation.StartTransactionId.HasValue;

        /// <summary>Statuses of a reservation that was authorized and is still active.</summary>
        private static bool IsAuthorizedActiveStatus(string status) =>
            string.Equals(status, PaymentReservationStatus.Authorized, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, PaymentReservationStatus.StartRequested, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, PaymentReservationStatus.Charging, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, PaymentReservationStatus.WaitingForDisconnect, StringComparison.OrdinalIgnoreCase);

        private const string TransactionEvidenceReviewReason = "Reservation has active transaction or stopped matching transaction evidence.";
        private const string TransactionLinkageIndeterminatePrefix = Recovery.FinancialRecoveryAuthorizationAssessor.IndeterminateTransactionEvidencePrefix;

        /// <summary>
        /// The release stopped for review because a transaction may belong to the reservation; such a hold is kept
        /// for the settlement recovery and never released automatically.
        /// </summary>
        private static bool IsTransactionEvidenceReview(ChargePaymentReservation reservation) =>
            string.Equals(reservation?.AuthorizationReleaseState, PaymentAuthorizationReleaseState.ReviewRequired, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(reservation.AuthorizationReleaseLastError) &&
            (reservation.AuthorizationReleaseLastError.StartsWith(TransactionEvidenceReviewReason, StringComparison.Ordinal) ||
             reservation.AuthorizationReleaseLastError.StartsWith(TransactionLinkageIndeterminatePrefix, StringComparison.Ordinal));

        private static bool IsHeldOrCapturedCheckoutAuthorization(PaymentCheckoutStatus checkout)
        {
            var authorization = checkout?.Authorization;
            if (authorization != null)
            {
                return authorization.State == PaymentAuthorizationState.RequiresCapture ||
                       authorization.State == PaymentAuthorizationState.Captured;
            }

            return checkout != null &&
                   checkout.IsPaid &&
                   !string.IsNullOrWhiteSpace(checkout.AuthorizationReference);
        }

        /// <summary>
        /// Arms the release reconciliation for a hold that could not be confirmed released. An already armed
        /// release keeps its schedule, lease and retry budget; only the reason is updated.
        /// </summary>
        private void ArmAuthorizationRelease(ChargePaymentReservation reservation, string reason, IPaymentGateway gateway)
        {
            if (reservation == null) return;

            if (!PaymentAuthorizationReleaseState.IsArmed(reservation.AuthorizationReleaseState))
            {
                // A new arming gets a new retry budget, even after an earlier release run used up its own.
                ArmAuthorizationReleaseState(reservation);
            }

            reservation.AuthorizationReleaseLastError = SanitizeProviderError(reason, gateway ?? GatewayForSanitizing(reservation));
        }

        private void MarkAuthorizationReleased(ChargePaymentReservation reservation)
        {
            reservation.AuthorizationReleaseState = PaymentAuthorizationReleaseState.Released;
            reservation.AuthorizationReleasedAtUtc = _utcNow();
            reservation.AuthorizationReleaseNextAttemptAtUtc = null;
            reservation.AuthorizationReleaseLastError = null;
        }

        private bool IsFreshInProgressRelease(ChargePaymentReservation reservation) =>
            string.Equals(reservation.AuthorizationReleaseState, PaymentAuthorizationReleaseState.InProgress, StringComparison.OrdinalIgnoreCase) &&
            reservation.AuthorizationReleaseLastAttemptAtUtc.HasValue &&
            reservation.AuthorizationReleaseLastAttemptAtUtc.Value > _utcNow().Subtract(ResolveAuthorizationReleaseInProgressTimeout());

        /// <summary>
        /// The payment method charged the payer at checkout (no deferred capture). No session is started on it;
        /// the reservation fails with the captured amount recorded so an operator can refund it.
        /// </summary>
        private void ApplyCapturedAtCheckout(
            ChargePaymentReservation reservation,
            IPaymentGateway gateway,
            PaymentAuthorizationSnapshot authorization,
            PaymentCheckoutStatus checkout,
            string authorizationReference,
            string source)
        {
            var now = _utcNow();
            var reference = string.IsNullOrWhiteSpace(authorizationReference)
                ? authorization?.AuthorizationReference
                : authorizationReference;
            if (string.IsNullOrWhiteSpace(reservation.StripePaymentIntentId) && !string.IsNullOrWhiteSpace(reference))
            {
                reservation.StripePaymentIntentId = reference;
            }

            PersistPayerDetails(reservation, checkout);
            var capturedAmount = authorization == null
                ? 0
                : authorization.AmountReceivedMinor > 0 ? authorization.AmountReceivedMinor : authorization.AmountMinor;
            var previousStatus = reservation.Status;
            reservation.Status = PaymentReservationStatus.Failed;
            reservation.FailureCode = CapturedAtCheckoutCode;
            reservation.FailureMessage = CapturedAtCheckoutMessage;
            reservation.LastError = CapturedAtCheckoutMessage;
            reservation.CapturedAmountCents = capturedAmount;
            reservation.CapturedAtUtc = now;
            reservation.UpdatedAtUtc = now;

            _logger.LogError(
                "Payments/{Source} => {Provider} payment was captured at checkout; no session is started and a refund is required reservation={ReservationId} authorization={AuthorizationReference} capturedMinor={CapturedMinor} previousStatus={PreviousStatus}",
                source,
                gateway?.ProviderName,
                reservation.ReservationId,
                reservation.StripePaymentIntentId,
                capturedAmount,
                previousStatus ?? "(null)");
        }

        /// <summary>
        /// Checks the snapshot a settlement cancel returned. True when the funds were released; a captured
        /// result records the capture and returns false; anything else is not a confirmed release and throws so
        /// the capture failure path arms the release reconciliation.
        /// </summary>
        private bool ApplySettlementCancelResult(
            ChargePaymentReservation reservation,
            IPaymentGateway gateway,
            PaymentAuthorizationSnapshot canceled,
            DateTime now)
        {
            if (canceled?.State == PaymentAuthorizationState.Canceled)
            {
                return true;
            }

            if (canceled?.State == PaymentAuthorizationState.Captured)
            {
                var capturedAmount = canceled.AmountReceivedMinor > 0 ? canceled.AmountReceivedMinor : canceled.AmountMinor;
                reservation.CapturedAmountCents = capturedAmount;
                reservation.CapturedAtUtc = now;
                reservation.Status = PaymentReservationStatus.Completed;
                reservation.LastError = $"{AuthorizationSubject(gateway)} was already captured ({capturedAmount} minor units) when the settlement tried to release it; review the settlement.";

                _logger.LogError(
                    "Payments/Complete => Settlement cancel found an existing capture provider={Provider} reservation={ReservationId} paymentIntent={PaymentIntentId} capturedCents={CapturedCents}",
                    gateway.ProviderName,
                    reservation.ReservationId,
                    canceled.AuthorizationReference ?? reservation.StripePaymentIntentId,
                    capturedAmount);
                return false;
            }

            throw new PaymentProviderException(
                gateway.ProviderName,
                CancelNotConfirmedCode,
                $"{AuthorizationSubject(gateway)} cancellation returned status '{Truncate(canceled?.RawStatus, 50) ?? "missing"}'.",
                isTransient: true);
        }

        /// <summary>
        /// True when a cancel of this pending reservation reads the checkout first
        /// (<see cref="ResolvePendingCheckoutBeforeCancel"/>): its provider is usable and its payment page can
        /// complete unnoticed (<see cref="IPaymentGateway.CheckoutLifetime"/>).
        /// </summary>
        private bool ResolvesPendingCheckoutBeforeCancel(ChargePaymentReservation reservation)
        {
            var gateway = ResolveGateway(reservation);
            return IsGatewayUsable(gateway) && gateway.CheckoutLifetime.HasValue;
        }

        /// <summary>
        /// Before cancelling a pending reservation of a provider whose payment page can complete unnoticed
        /// (<see cref="IPaymentGateway.CheckoutLifetime"/>), reads the checkout: a held authorization is released,
        /// and a checkout that may still complete keeps the release armed so the reconciliation checks it again
        /// once the page has expired. Returns true when the provider was consulted.
        /// <paramref name="releaseNotNeeded"/> is true when the provider showed that nothing is held (no checkout,
        /// an aborted or failed page, a cancelled authorization) or that the payment was captured at checkout.
        /// </summary>
        private bool ResolvePendingCheckoutBeforeCancel(
            ChargePaymentReservation reservation,
            string reason,
            out bool releaseNotNeeded)
        {
            releaseNotNeeded = false;
            var gateway = ResolveGateway(reservation);
            if (!IsGatewayUsable(gateway) || !gateway.CheckoutLifetime.HasValue)
            {
                return false;
            }

            PaymentCheckoutStatus checkout;
            PaymentAuthorizationSnapshot authorization = null;
            try
            {
                checkout = gateway.GetCheckout(reservation.StripeCheckoutSessionId);
                var reference = AuthorizationReferenceOf(checkout);
                if (checkout?.State == PaymentCheckoutState.Complete && !string.IsNullOrWhiteSpace(reference))
                {
                    authorization = InlineAuthorizationOf(checkout, reference) ?? gateway.GetAuthorization(reference);
                }
            }
            catch (PaymentProviderException pex)
            {
                _logger.LogWarning(pex, "Payments/Cancel => Checkout lookup failed before cancel reservation={ReservationId}; arming authorization release", reservation.ReservationId);
                ArmAuthorizationRelease(reservation, FormatProviderError(pex, "Checkout lookup failed before cancel."), gateway);
                return true;
            }

            if (checkout == null)
            {
                // The provider does not know the checkout, so it cannot hold funds for it.
                releaseNotNeeded = true;
                return true;
            }

            if ((checkout.OwnerReservationId.HasValue && checkout.OwnerReservationId.Value != reservation.ReservationId) ||
                (!string.IsNullOrWhiteSpace(checkout.SessionReference) &&
                 !string.Equals(checkout.SessionReference, reservation.StripeCheckoutSessionId, StringComparison.Ordinal)))
            {
                ArmAuthorizationRelease(reservation, "Provider checkout does not match the reservation; release needs verification.", gateway);
                return true;
            }

            PersistPayerDetails(reservation, checkout);

            if (authorization != null)
            {
                if (!ProviderOwnershipMatches(authorization, reservation.ReservationId))
                {
                    ArmAuthorizationRelease(reservation, "Provider authorization ownership is missing or inconsistent; release needs verification.", gateway);
                    return true;
                }

                if (!string.IsNullOrWhiteSpace(authorization.AuthorizationReference))
                {
                    reservation.StripePaymentIntentId = authorization.AuthorizationReference;
                }

                if (authorization.State == PaymentAuthorizationState.Captured && gateway.CapturedCheckoutRequiresRefund)
                {
                    ApplyCapturedAtCheckout(reservation, gateway, authorization, checkout, authorization.AuthorizationReference, "Cancel");
                    releaseNotNeeded = true;
                    return true;
                }

                if (authorization.State == PaymentAuthorizationState.Canceled)
                {
                    releaseNotNeeded = true;
                    return true;
                }

                if (authorization.State != PaymentAuthorizationState.RequiresCapture || !authorization.IsCancelable)
                {
                    ArmAuthorizationRelease(reservation, $"Provider authorization status '{Truncate(authorization.RawStatus, 50) ?? "missing"}' at cancel; release needs verification.", gateway);
                    return true;
                }

                _logger.LogInformation(
                    "Payments/Cancel => Releasing authorization found on the pending checkout provider={Provider} reservation={ReservationId} authorization={AuthorizationReference} reason={Reason}",
                    gateway.ProviderName,
                    reservation.ReservationId,
                    reservation.StripePaymentIntentId,
                    reason ?? "(none)");
                try
                {
                    var canceled = gateway.Cancel(
                        reservation.StripePaymentIntentId,
                        BuildIdempotencyKey(IdempotencyCancel, reservation.ReservationId));
                    if (canceled?.State == PaymentAuthorizationState.Canceled)
                    {
                        MarkAuthorizationReleased(reservation);
                    }
                    else
                    {
                        ArmAuthorizationRelease(reservation, $"{AuthorizationSubject(gateway)} cancellation returned status '{Truncate(canceled?.RawStatus, 50) ?? "missing"}'.", gateway);
                    }
                }
                catch (PaymentProviderException pex)
                {
                    _logger.LogWarning(pex, "Payments/Cancel => Unable to release authorization found on the pending checkout reservation={ReservationId}", reservation.ReservationId);
                    ArmAuthorizationRelease(reservation, FormatProviderError(pex, pex.Message), gateway);
                }

                return true;
            }

            if (checkout.State == PaymentCheckoutState.Canceled || checkout.State == PaymentCheckoutState.Failed)
            {
                // The payer aborted or the payment failed: nothing is held.
                releaseNotNeeded = true;
                return true;
            }

            // Still open (or not readable as final): the payer can complete the page after this cancel.
            ArmAuthorizationRelease(reservation, "Checkout was still open when the reservation was cancelled; a late authorization will be released.", gateway);
            return true;
        }

        /// <summary>Reads the authorization of a terminal reservation; a permanent lookup failure arms the release.</summary>
        private PaymentAuthorizationSnapshot LookupLateAuthorization(
            OCPPCoreContext dbContext,
            IPaymentGateway gateway,
            ChargePaymentReservation reservation)
        {
            try
            {
                return gateway.GetAuthorization(reservation.StripePaymentIntentId);
            }
            catch (PaymentProviderException pex) when (!pex.IsTransient)
            {
                _logger.LogWarning(pex, "Payments/Webhook => Unable to read the late authorization of reservation={ReservationId}; arming authorization release", reservation.ReservationId);
                ArmAuthorizationRelease(reservation, FormatProviderError(pex, pex.Message), gateway);
                reservation.UpdatedAtUtc = _utcNow();
                dbContext.SaveChanges();
                return null;
            }
        }

        /// <summary>
        /// Releases an authorization that a provider signal or lookup revealed for a reservation that already ended
        /// without a charging session: nothing will ever capture it. Returns true when the authorization was
        /// handled. A reservation that was authorized gets the reconciliation's transaction-evidence check first:
        /// when a transaction may belong to it (or that cannot be evaluated) the hold is kept for review, and a hold
        /// already kept for that reason is never released here. A transient provider failure arms the release and
        /// propagates, so a notification is answered with a retry status.
        /// </summary>
        private bool TryReleaseLateAuthorization(
            OCPPCoreContext dbContext,
            IPaymentGateway gateway,
            ChargePaymentReservation reservation,
            PaymentAuthorizationSnapshot authorization,
            string source)
        {
            if (gateway == null || authorization == null || !IsTerminalNoChargeCandidate(reservation))
            {
                return false;
            }

            var held = authorization.State == PaymentAuthorizationState.RequiresCapture && authorization.IsCancelable;
            var capturedAtCheckout = authorization.State == PaymentAuthorizationState.Captured && gateway.CapturedCheckoutRequiresRefund;
            if (!held && !capturedAtCheckout)
            {
                return false;
            }

            var reference = string.IsNullOrWhiteSpace(authorization.AuthorizationReference)
                ? reservation.StripePaymentIntentId
                : authorization.AuthorizationReference;
            if (string.IsNullOrWhiteSpace(reference) ||
                (!string.IsNullOrWhiteSpace(reservation.StripePaymentIntentId) &&
                 !string.Equals(reservation.StripePaymentIntentId, reference, StringComparison.Ordinal)))
            {
                _logger.LogWarning("Payments/{Source} => Late authorization does not match reservation={ReservationId}", source, reservation.ReservationId);
                return false;
            }

            if (!ProviderOwnershipMatches(authorization, reservation.ReservationId))
            {
                _logger.LogWarning("Payments/{Source} => Late authorization is not owned by reservation={ReservationId}", source, reservation.ReservationId);
                return false;
            }

            if (IsFreshInProgressRelease(reservation))
            {
                // A release attempt holds the lease and will cancel it.
                return false;
            }

            if (!InvoiceSubmissionLogLookup.TryHasSubmittedOrExternalInvoice(
                    dbContext,
                    reservation.ReservationId,
                    _logger,
                    "late authorization release",
                    out var hasSubmittedOrExternalInvoice) ||
                hasSubmittedOrExternalInvoice)
            {
                _logger.LogWarning("Payments/{Source} => Late authorization of reservation={ReservationId} is not released automatically because invoice evidence exists or cannot be verified", source, reservation.ReservationId);
                return false;
            }

            reservation.StripePaymentIntentId = reference;
            if (capturedAtCheckout)
            {
                ApplyCapturedAtCheckout(reservation, gateway, authorization, null, reference, source);
                dbContext.SaveChanges();
                return true;
            }

            if (!IsNeverAuthorized(reservation) && KeepLateAuthorizationForReview(dbContext, reservation, authorization, source))
            {
                return true;
            }

            _logger.LogWarning(
                "Payments/{Source} => {Provider} authorization arrived for ended reservation={ReservationId} status={Status}; cancelling authorization={AuthorizationReference}",
                source,
                gateway.ProviderName,
                reservation.ReservationId,
                reservation.Status,
                reference);

            try
            {
                var cancelled = gateway.Cancel(
                    reference,
                    BuildIdempotencyKey(IdempotencyCancel, reservation.ReservationId));
                if (cancelled?.State == PaymentAuthorizationState.Canceled)
                {
                    reservation.LastError = LateAuthorizationReleasedReason;
                    MarkAuthorizationReleased(reservation);
                    CloseLatestOpenReleaseAttempt(dbContext, reservation, PaymentAuthorizationReleaseOutcome.Released, cancelled.RawStatus, null, _utcNow());
                }
                else if (cancelled?.State == PaymentAuthorizationState.Captured)
                {
                    _logger.LogError(
                        "Payments/{Source} => Late authorization of reservation={ReservationId} was captured instead of released",
                        source,
                        reservation.ReservationId);
                    reservation.LastError = $"Late {AuthorizationTerm(gateway)} is captured and could not be released; review required.";
                    reservation.AuthorizationReleaseState = PaymentAuthorizationReleaseState.ReviewRequired;
                    reservation.AuthorizationReleaseNextAttemptAtUtc = null;
                    reservation.AuthorizationReleaseLastError = reservation.LastError;
                    CloseLatestOpenReleaseAttempt(dbContext, reservation, PaymentAuthorizationReleaseOutcome.ReviewRequired, cancelled.RawStatus, reservation.LastError, _utcNow());
                }
                else
                {
                    _logger.LogWarning(
                        "Payments/{Source} => Late authorization of reservation={ReservationId} is {Status} after cancel",
                        source,
                        reservation.ReservationId,
                        cancelled?.RawStatus ?? "(unknown)");
                    reservation.LastError = Truncate(
                        $"Late {AuthorizationTerm(gateway)} could not be released (status={cancelled?.RawStatus ?? "unknown"}).",
                        500);
                    ArmAuthorizationRelease(reservation, reservation.LastError, gateway);
                }
            }
            catch (PaymentProviderException pex) when (!pex.IsTransient)
            {
                _logger.LogError(pex, "Payments/{Source} => Unable to cancel late authorization of reservation={ReservationId}", source, reservation.ReservationId);
                reservation.LastError = SanitizeProviderError(pex.Message, gateway);
                reservation.AuthorizationReleaseState = PaymentAuthorizationReleaseState.PermanentFailure;
                reservation.AuthorizationReleaseNextAttemptAtUtc = null;
                reservation.AuthorizationReleaseLastError = SanitizeProviderError(FormatProviderError(pex, pex.Message), gateway);
            }
            catch (PaymentProviderException pex)
            {
                _logger.LogWarning(pex, "Payments/{Source} => Transient failure cancelling late authorization of reservation={ReservationId}; arming authorization release", source, reservation.ReservationId);
                ArmAuthorizationRelease(reservation, FormatProviderError(pex, pex.Message), gateway);
                reservation.UpdatedAtUtc = _utcNow();
                dbContext.SaveChanges();
                throw;
            }

            reservation.UpdatedAtUtc = _utcNow();
            dbContext.SaveChanges();
            return true;
        }

        /// <summary>
        /// Applies the reconciliation's transaction-evidence check to a late authorization of a reservation that was
        /// authorized. True when the hold is kept for review instead of being released (a charger may have started a
        /// session on it that was never linked).
        /// </summary>
        private bool KeepLateAuthorizationForReview(
            OCPPCoreContext dbContext,
            ChargePaymentReservation reservation,
            PaymentAuthorizationSnapshot authorization,
            string source)
        {
            if (IsTransactionEvidenceReview(reservation))
            {
                _logger.LogWarning(
                    "Payments/{Source} => Late authorization of reservation={ReservationId} is kept: the release is under review for transaction evidence",
                    source,
                    reservation.ReservationId);
                reservation.UpdatedAtUtc = _utcNow();
                dbContext.SaveChanges();
                return true;
            }

            var transactionEvidence = Recovery.FinancialRecoveryAuthorizationAssessor.EvaluateTransactionEvidence(dbContext, reservation);
            if (transactionEvidence.CanEvaluate && !transactionEvidence.HasTransaction)
            {
                return false;
            }

            var reason = transactionEvidence.CanEvaluate ? TransactionEvidenceReviewReason : transactionEvidence.Reason;
            _logger.LogWarning(
                "Payments/{Source} => Late authorization of reservation={ReservationId} is kept for review reason={Reason}",
                source,
                reservation.ReservationId,
                reason);
            reservation.UpdatedAtUtc = _utcNow();
            FinishReservationOnly(
                dbContext,
                reservation,
                PaymentAuthorizationReleaseState.ReviewRequired,
                PaymentAuthorizationReleaseOutcome.ReviewRequired,
                authorization?.RawStatus,
                reason);
            return true;
        }

        /// <summary>
        /// A confirmation or provider signal found an authorization, but another request (a cancellation or the
        /// stale-reservation sweep) ended the reservation first. Releases that authorization unless the request that
        /// ended the reservation already released it. Uses the authorization returned with the checkout, or a fresh
        /// lookup: an earlier read can predate the other request's cancellation.
        /// </summary>
        private bool ReleaseAuthorizationAfterLostTransition(
            OCPPCoreContext dbContext,
            IPaymentGateway gateway,
            ChargePaymentReservation reservation,
            PaymentCheckoutStatus checkout,
            string authorizationReference,
            string source)
        {
            if (gateway == null || string.IsNullOrWhiteSpace(authorizationReference) || !IsTerminalNoChargeCandidate(reservation))
            {
                return false;
            }

            if (string.Equals(reservation.AuthorizationReleaseState, PaymentAuthorizationReleaseState.Released, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(reservation.StripePaymentIntentId, authorizationReference, StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "Payments/{Source} => Authorization of ended reservation={ReservationId} was already released by the request that ended it",
                    source,
                    reservation.ReservationId);
                return true;
            }

            var authorization = InlineAuthorizationOf(checkout, authorizationReference);
            if (authorization == null)
            {
                try
                {
                    authorization = gateway.GetAuthorization(authorizationReference);
                }
                catch (PaymentProviderException pex) when (!pex.IsTransient)
                {
                    _logger.LogWarning(pex, "Payments/{Source} => Unable to read the authorization of ended reservation={ReservationId}; arming authorization release", source, reservation.ReservationId);
                    if (string.IsNullOrWhiteSpace(reservation.StripePaymentIntentId))
                    {
                        reservation.StripePaymentIntentId = authorizationReference;
                    }

                    ArmAuthorizationRelease(reservation, FormatProviderError(pex, pex.Message), gateway);
                    reservation.UpdatedAtUtc = _utcNow();
                    dbContext.SaveChanges();
                    return false;
                }
            }

            return TryReleaseLateAuthorization(dbContext, gateway, reservation, authorization, source);
        }

        /// <summary>
        /// Browser return for a reservation that already ended, of a provider whose payment page can complete
        /// unnoticed: while the page can still be completed, check it (throttled) and release a late authorization.
        /// </summary>
        private void TryReleaseLateAuthorizationOnReturn(OCPPCoreContext dbContext, ChargePaymentReservation reservation)
        {
            if (!IsTerminalNoChargeCandidate(reservation) ||
                !string.IsNullOrWhiteSpace(reservation.StripePaymentIntentId) ||
                string.IsNullOrWhiteSpace(reservation.StripeCheckoutSessionId))
            {
                return;
            }

            var gateway = ResolveGateway(reservation);
            if (!IsGatewayUsable(gateway) || !string.IsNullOrEmpty(gateway.SuccessUrlSessionPlaceholder))
            {
                return;
            }

            var lifetimeEnd = CheckoutLifetimeEnd(gateway, reservation);
            if (!lifetimeEnd.HasValue || _utcNow() >= lifetimeEnd.Value || !TryEnterProviderCheck(reservation.ReservationId))
            {
                return;
            }

            try
            {
                var checkout = gateway.GetCheckout(reservation.StripeCheckoutSessionId);
                if (checkout == null ||
                    checkout.State != PaymentCheckoutState.Complete ||
                    (checkout.OwnerReservationId.HasValue && checkout.OwnerReservationId.Value != reservation.ReservationId) ||
                    (!string.IsNullOrWhiteSpace(checkout.SessionReference) &&
                     !string.Equals(checkout.SessionReference, reservation.StripeCheckoutSessionId, StringComparison.Ordinal)))
                {
                    return;
                }

                var reference = AuthorizationReferenceOf(checkout);
                if (string.IsNullOrWhiteSpace(reference))
                {
                    return;
                }

                var authorization = InlineAuthorizationOf(checkout, reference) ?? gateway.GetAuthorization(reference);
                PersistPayerDetails(reservation, checkout);
                TryReleaseLateAuthorization(dbContext, gateway, reservation, authorization, "Confirm");
            }
            catch (PaymentProviderException pex)
            {
                _logger.LogWarning(pex, "Payments/Confirm => Late authorization check failed for ended reservation={ReservationId}", reservation.ReservationId);
            }
        }

        /// <summary>
        /// Assigns the reservation's charge tag exactly once. The browser return and a provider notification can
        /// authorize the same reservation at the same time; on a relational database the tag is claimed with a
        /// conditional update and both callers continue with the persisted one, so the remote start always uses
        /// the tag the transaction will be linked by.
        /// </summary>
        private static void ClaimOcppIdTag(OCPPCoreContext dbContext, ChargePaymentReservation reservation)
        {
            if (!string.IsNullOrWhiteSpace(reservation.OcppIdTag))
            {
                EnsureChargeTagExists(dbContext, reservation.OcppIdTag);
                return;
            }

            var candidate = GenerateOcppIdTag();
            if (!dbContext.Database.IsRelational())
            {
                reservation.OcppIdTag = candidate;
                EnsureChargeTagExists(dbContext, candidate);
                return;
            }

            var reservationId = reservation.ReservationId;
            dbContext.ChargePaymentReservations
                .Where(candidateRow => candidateRow.ReservationId == reservationId &&
                                       (candidateRow.OcppIdTag == null || candidateRow.OcppIdTag == ""))
                .ExecuteUpdate(setters => setters.SetProperty(row => row.OcppIdTag, candidate));
            var persisted = dbContext.ChargePaymentReservations
                .AsNoTracking()
                .Where(row => row.ReservationId == reservationId)
                .Select(row => row.OcppIdTag)
                .FirstOrDefault();

            var tag = string.IsNullOrWhiteSpace(persisted) ? candidate : persisted;
            reservation.OcppIdTag = tag;
            var entry = dbContext.Entry(reservation);
            if (entry.State == EntityState.Modified || entry.State == EntityState.Unchanged)
            {
                // The database already holds the tag; keep SaveChanges from writing it again.
                var property = entry.Property(row => row.OcppIdTag);
                property.OriginalValue = tag;
                property.IsModified = false;
            }

            EnsureChargeTagExists(dbContext, tag);
        }

        /// <summary>
        /// Claims the payment-authorized e-mail of a reservation of a provider without a payer-supplied checkout
        /// reference. The browser return and a provider notification can both find the reservation still pending
        /// and both authorize it; only the first claim sends the e-mail (per server instance).
        /// </summary>
        private bool TryClaimAuthorizationEmail(Guid reservationId)
        {
            var now = _utcNow();
            if (!_authorizationEmailClaimedAtUtc.TryAdd(reservationId, now))
            {
                return false;
            }

            if (_authorizationEmailClaimedAtUtc.Count > ProviderCheckPruneThreshold)
            {
                var cutoff = now - AuthorizationEmailClaimRetention;
                foreach (var pair in _authorizationEmailClaimedAtUtc)
                {
                    if (pair.Value < cutoff)
                    {
                        _authorizationEmailClaimedAtUtc.TryRemove(pair.Key, out _);
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// At most one provider check per reservation per <see cref="ProviderCheckInterval"/> for callers that only
        /// need the reservation id (browser returns of providers without a payer-supplied checkout reference).
        /// </summary>
        private bool TryEnterProviderCheck(Guid reservationId)
        {
            var now = _utcNow();
            while (true)
            {
                if (_lastProviderCheckAtUtc.TryGetValue(reservationId, out var last))
                {
                    if (now >= last && now - last < ProviderCheckInterval)
                    {
                        return false;
                    }

                    if (_lastProviderCheckAtUtc.TryUpdate(reservationId, now, last))
                    {
                        break;
                    }
                }
                else if (_lastProviderCheckAtUtc.TryAdd(reservationId, now))
                {
                    break;
                }
            }

            if (_lastProviderCheckAtUtc.Count > ProviderCheckPruneThreshold)
            {
                var cutoff = now - TimeSpan.FromMinutes(1);
                foreach (var pair in _lastProviderCheckAtUtc)
                {
                    if (pair.Value < cutoff)
                    {
                        _lastProviderCheckAtUtc.TryRemove(pair.Key, out _);
                    }
                }
            }

            return true;
        }
    }
}
