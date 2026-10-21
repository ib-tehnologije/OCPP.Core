using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OCPP.Core.Database;
using OCPP.Core.Server.Payments;
using OCPP.Core.Server.Payments.Providers;
using OCPP.Core.Server.Payments.Providers.Saferpay;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    /// <summary>
    /// Late-authorization release guards, settlement retries after transient provider failures, the
    /// never-authorized test, re-armed retry budgets and the Resume throttle.
    /// </summary>
    public partial class PaymentCoordinatorProviderTests
    {
        // ---- Late authorization of a reservation that was authorized: transaction evidence first ----

        [Theory]
        [InlineData(null)]
        [InlineData(PaymentAuthorizationReleaseState.ReviewRequired)]
        public async Task CheckoutNotification_LateAuthorizationWithAnUnlinkedTransactionIsKeptForReview(string? releaseState)
        {
            using var context = CreateContext();
            var reservation = AddAuthorizedTerminalReservation(context, PaymentReservationStatus.StartTimeout, "fake_token_evidence", "fake_tx_evidence");
            reservation.AuthorizationReleaseState = releaseState;
            reservation.AuthorizationReleaseLastError = releaseState == null
                ? null
                : "Reservation has active transaction or stopped matching transaction evidence.";
            // The charger started late (offline): a transaction under the reservation's tag was never linked.
            context.Transactions.Add(new Transaction
            {
                TransactionId = 77,
                ChargePointId = reservation.ChargePointId,
                ConnectorId = reservation.ConnectorId,
                StartTagId = reservation.OcppIdTag,
                StartTime = reservation.AuthorizedAtUtc!.Value.AddMinutes(2),
                MeterStart = 0
            });
            context.SaveChanges();
            var fake = FakePaymentGateway.SaferpayLike();
            fake.CheckoutStatus = CompletedCheckout(reservation.ReservationId, "fake_token_evidence", "fake_tx_evidence", inlineAuthorization: true, amountMinor: 450);
            fake.ParseResult = Notification(reservation.ReservationId, eventId: null);
            var coordinator = CreateCoordinator(fake);

            var result = await coordinator.HandleWebhookAsync(context, NotifyRequest(), CancellationToken.None);

            Assert.Equal(200, result.StatusCode);
            Assert.Empty(fake.Cancels);
            Assert.Equal(PaymentReservationStatus.StartTimeout, reservation.Status);
            Assert.Equal(PaymentAuthorizationReleaseState.ReviewRequired, reservation.AuthorizationReleaseState);
            Assert.StartsWith("Reservation has active transaction", reservation.AuthorizationReleaseLastError);
            Assert.NotEqual("late_authorization_released", reservation.LastError);
        }

        [Fact]
        public async Task CheckoutNotification_LateAuthorizationWhoseTransactionLinkageCannotBeEvaluatedIsKeptForReview()
        {
            using var context = CreateContext();
            var reservation = AddAuthorizedTerminalReservation(context, PaymentReservationStatus.StartRejected, "fake_token_no_window", "fake_tx_no_window");
            reservation.StartDeadlineAtUtc = null;
            context.SaveChanges();
            var fake = FakePaymentGateway.SaferpayLike();
            fake.CheckoutStatus = CompletedCheckout(reservation.ReservationId, "fake_token_no_window", "fake_tx_no_window", inlineAuthorization: true, amountMinor: 450);
            fake.ParseResult = Notification(reservation.ReservationId, eventId: null);
            var coordinator = CreateCoordinator(fake);

            var result = await coordinator.HandleWebhookAsync(context, NotifyRequest(), CancellationToken.None);

            Assert.Equal(200, result.StatusCode);
            Assert.Empty(fake.Cancels);
            Assert.Equal(PaymentAuthorizationReleaseState.ReviewRequired, reservation.AuthorizationReleaseState);
            Assert.Contains("transaction linkage", reservation.AuthorizationReleaseLastError, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task CheckoutNotification_LateAuthorizationOfAnAuthorizedReservationWithoutTransactionIsReleased()
        {
            using var context = CreateContext();
            var reservation = AddAuthorizedTerminalReservation(context, PaymentReservationStatus.StartTimeout, "fake_token_no_tx", "fake_tx_no_tx");
            var fake = FakePaymentGateway.SaferpayLike();
            fake.CheckoutStatus = CompletedCheckout(reservation.ReservationId, "fake_token_no_tx", "fake_tx_no_tx", inlineAuthorization: true, amountMinor: 450);
            fake.ParseResult = Notification(reservation.ReservationId, eventId: null);
            var coordinator = CreateCoordinator(fake);

            var result = await coordinator.HandleWebhookAsync(context, NotifyRequest(), CancellationToken.None);

            Assert.Equal(200, result.StatusCode);
            Assert.Equal("fake_tx_no_tx", Assert.Single(fake.Cancels).Reference);
            Assert.Equal(PaymentAuthorizationReleaseState.Released, reservation.AuthorizationReleaseState);
            Assert.Equal("late_authorization_released", reservation.LastError);
        }

        // ---- Never authorized: no authorization time, start window, remote start or transaction ----

        [Fact]
        public void Release_AbandonedReservationWithALeftoverTagButNoAuthorizationIsCheckedWithTheProvider()
        {
            using var context = CreateContext();
            var reservation = AddSweptReservation(context, "fake_token_leftover_tag");
            // A tag claimed by an authorization whose save failed before the tag was part of it.
            reservation.OcppIdTag = "RLEFTOVERTAG";
            context.SaveChanges();
            var fake = FakePaymentGateway.SaferpayLike();
            fake.CheckoutStatus = CompletedCheckout(reservation.ReservationId, "fake_token_leftover_tag", "fake_tx_leftover_tag", inlineAuthorization: true, amountMinor: 450);
            var coordinator = CreateCoordinator(fake);

            var result = coordinator.ReconcileTerminalPaymentAuthorization(context, reservation, PaymentAuthorizationReleaseTrigger.CleanupSweep);

            Assert.Equal(PaymentAuthorizationReleaseOutcome.Released, result.Outcome);
            Assert.Equal("fake_tx_leftover_tag", Assert.Single(fake.Cancels).Reference);
        }

        [Fact]
        public void Release_RemoteStartEvidenceKeepsTheTransactionLinkageCheck()
        {
            using var context = CreateContext();
            var reservation = AddSweptReservation(context, "fake_token_remote_start");
            reservation.RemoteStartSentAtUtc = Now.AddMinutes(-15);
            context.SaveChanges();
            var fake = FakePaymentGateway.SaferpayLike();
            fake.CheckoutStatus = CompletedCheckout(reservation.ReservationId, "fake_token_remote_start", "fake_tx_remote_start", inlineAuthorization: true, amountMinor: 450);
            var coordinator = CreateCoordinator(fake);

            var result = coordinator.ReconcileTerminalPaymentAuthorization(context, reservation, PaymentAuthorizationReleaseTrigger.CleanupSweep);

            Assert.Equal(PaymentAuthorizationReleaseOutcome.ReviewRequired, result.Outcome);
            Assert.Contains("transaction linkage", reservation.AuthorizationReleaseLastError, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(fake.Calls);
        }

        // ---- Providers without a page lifetime: an abandoned open checkout holds nothing ----

        [Fact]
        public void Release_BudgetExhaustedVerificationOfAnAbandonedOpenCheckoutIsAlreadyReleased()
        {
            using var context = CreateContext();
            var reservation = AddSweptReservation(context, "fake_token_open_exhausted");
            reservation.AuthorizationReleaseState = PaymentAuthorizationReleaseState.RetryScheduled;
            reservation.AuthorizationReleaseAttemptCount = 4;
            context.SaveChanges();
            var fake = new FakePaymentGateway
            {
                CheckoutStatus = new PaymentCheckoutStatus
                {
                    SessionReference = "fake_token_open_exhausted",
                    State = PaymentCheckoutState.Open,
                    RawStatus = "open",
                    OwnerReservationId = reservation.ReservationId
                }
            };
            var coordinator = CreateCoordinator(fake);

            var result = coordinator.ReconcileTerminalPaymentAuthorization(context, reservation, PaymentAuthorizationReleaseTrigger.CleanupSweep);

            Assert.Equal(PaymentAuthorizationReleaseOutcome.AlreadyReleased, result.Outcome);
            Assert.Equal(PaymentAuthorizationReleaseState.Released, reservation.AuthorizationReleaseState);
            Assert.Empty(fake.Cancels);
        }

        // ---- Re-armed release: a new retry budget, attempt numbers keep increasing ----

        [Fact]
        public async Task CheckoutNotification_ReArmedReleaseAfterAnExhaustedRunCancelsWithTheNextAttemptNumber()
        {
            using var context = CreateContext();
            var reservation = AddSweptReservation(context, "fake_token_rearm");
            reservation.AuthorizationReleaseState = PaymentAuthorizationReleaseState.PermanentFailure;
            reservation.AuthorizationReleaseAttemptCount = 4;
            for (var number = 1; number <= 4; number++)
            {
                context.PaymentAuthorizationReleaseAttempts.Add(new PaymentAuthorizationReleaseAttempt
                {
                    PaymentAuthorizationReleaseAttemptId = Guid.NewGuid(),
                    ReservationId = reservation.ReservationId,
                    AttemptNumber = number,
                    Trigger = PaymentAuthorizationReleaseTrigger.CleanupSweep,
                    StartedAtUtc = Now.AddMinutes(-30 + number),
                    FinishedAtUtc = Now.AddMinutes(-30 + number),
                    Outcome = number == 4 ? PaymentAuthorizationReleaseOutcome.PermanentFailure : PaymentAuthorizationReleaseOutcome.RetryScheduled
                });
            }

            context.SaveChanges();
            var fake = FakePaymentGateway.SaferpayLike();
            fake.CheckoutStatus = CompletedCheckout(reservation.ReservationId, "fake_token_rearm", "fake_tx_rearm", inlineAuthorization: true, amountMinor: 450);
            fake.ParseResult = Notification(reservation.ReservationId, eventId: null);
            fake.CancelException = new PaymentProviderException(FakeProvider, "timeout", "Provider timeout", isTransient: true, httpStatusCode: 504);
            fake.AuthorizationResponse = Authorization(reservation.ReservationId, "fake_tx_rearm", PaymentAuthorizationState.RequiresCapture, 450);
            var coordinator = CreateCoordinator(fake);

            var first = await coordinator.HandleWebhookAsync(context, NotifyRequest(), CancellationToken.None);

            Assert.Equal(503, first.StatusCode);
            Assert.Equal(PaymentAuthorizationReleaseState.Pending, reservation.AuthorizationReleaseState);
            Assert.Equal(0, reservation.AuthorizationReleaseAttemptCount);

            fake.CancelException = null;
            var result = coordinator.ReconcileTerminalPaymentAuthorization(context, reservation, PaymentAuthorizationReleaseTrigger.CleanupSweep);

            Assert.Equal(PaymentAuthorizationReleaseOutcome.Released, result.Outcome);
            Assert.Equal(PaymentAuthorizationReleaseState.Released, reservation.AuthorizationReleaseState);
            Assert.Equal(1, reservation.AuthorizationReleaseAttemptCount);
            var cancel = Assert.Single(fake.Cancels);
            Assert.Equal($"authorization_release:{reservation.ReservationId}:5", cancel.IdempotencyKey);
            Assert.Equal(5, context.PaymentAuthorizationReleaseAttempts.Max(attempt => attempt.AttemptNumber));
        }

        [Fact]
        public void CancelPaymentIntent_ArmingAReleaseThatEndedGivesItANewRetryBudget()
        {
            using var context = CreateContext();
            var reservation = AddReleaseReservation(context, FakeProvider, authorizationReference: "fake_tx_cancel_rearm");
            reservation.Status = PaymentReservationStatus.StartRejected;
            reservation.AuthorizationReleaseState = PaymentAuthorizationReleaseState.Released;
            reservation.AuthorizationReleaseAttemptCount = 4;
            context.SaveChanges();
            var fake = new FakePaymentGateway
            {
                GetAuthorizationException = new PaymentProviderException(FakeProvider, "timeout", "Provider timeout", isTransient: true, httpStatusCode: 504)
            };
            var coordinator = CreateCoordinator(fake);

            coordinator.CancelPaymentIntentIfCancelable(context, reservation, "Start rejected");

            Assert.Equal(PaymentAuthorizationReleaseState.Pending, reservation.AuthorizationReleaseState);
            Assert.Equal(0, reservation.AuthorizationReleaseAttemptCount);
        }

        // ---- Settlement: transient provider failures are retried, unverified captures flagged ----

        [Fact]
        public void Complete_TransientProviderFailureKeepsTheReservationForASettlementRetry()
        {
            using var context = CreateContext();
            var reservation = AddChargingReservation(context, FakeProvider, "fake_tx_retry");
            var transaction = AddStoppedTransaction(context, reservation, meterStopKwh: 2);
            var clock = Now;
            var fake = new FakePaymentGateway
            {
                GetAuthorizationException = new PaymentProviderException(FakeProvider, "inquire_not_found", "Transaction not found", isTransient: true)
            };
            var invoices = new FakeInvoiceIntegrationService();
            var coordinator = CreateCoordinator(fake, now: () => clock, invoiceIntegrationService: invoices);

            coordinator.CompleteReservation(context, transaction);

            Assert.Equal(PaymentReservationStatus.Charging, reservation.Status);
            Assert.Equal("settlement_retry_pending", reservation.FailureCode);
            Assert.Contains("inquire_not_found", reservation.LastError);
            Assert.Null(reservation.CapturedAmountCents);
            Assert.Null(reservation.AuthorizationReleaseState);
            Assert.Empty(fake.Captures);

            // The provider caught up; the retry settles the same amount once.
            clock = Now.AddMinutes(2);
            fake.GetAuthorizationException = null;
            fake.AuthorizationResponse = Authorization(reservation.ReservationId, "fake_tx_retry", PaymentAuthorizationState.RequiresCapture, 450);

            coordinator.CompleteReservation(context, transaction);

            Assert.Equal(PaymentReservationStatus.Completed, reservation.Status);
            Assert.Null(reservation.FailureCode);
            Assert.Null(reservation.LastError);
            var capture = Assert.Single(fake.Captures);
            Assert.Equal(130, capture.AmountMinor);
            // A retry uses a key scoped to the minute since the settlement started (stop at Now-5, retry at Now+2).
            Assert.Equal($"capture:{reservation.ReservationId}:130:r7", capture.IdempotencyKey);
            Assert.Equal(130, reservation.CapturedAmountCents);
            Assert.Equal(1, invoices.HandleCompletedReservationCount);
        }

        [Fact]
        public void Complete_RetriedIdleSettlementCapturesTheSameAmount()
        {
            using var context = CreateContext();
            var reservation = AddChargingReservation(context, FakeProvider, "fake_tx_idle_retry");
            reservation.UsageFeeAnchorMinutes = 1;
            reservation.UsageFeePerMinute = 0.20m;
            reservation.StartUsageFeeAfterMinutes = 0;
            reservation.MaxUsageFeeMinutes = 60;
            var transaction = AddStoppedTransaction(context, reservation, meterStopKwh: 2);
            // Charging stopped five minutes before the cable was unplugged: five idle minutes.
            reservation.DisconnectedAtUtc = transaction.StopTime!.Value.AddMinutes(5);
            context.SaveChanges();
            var clock = Now;
            var fake = new FakePaymentGateway
            {
                GetAuthorizationException = new PaymentProviderException(FakeProvider, "inquire_not_found", "Transaction not found", isTransient: true)
            };
            var coordinator = CreateCoordinator(fake, now: () => clock);

            coordinator.CompleteReservation(context, transaction);

            Assert.Equal("settlement_retry_pending", reservation.FailureCode);
            Assert.Equal(5, transaction.IdleUsageFeeMinutes);

            clock = Now.AddMinutes(3);
            fake.GetAuthorizationException = null;
            fake.AuthorizationResponse = Authorization(reservation.ReservationId, "fake_tx_idle_retry", PaymentAuthorizationState.RequiresCapture, 450);

            coordinator.CompleteReservation(context, transaction);

            Assert.Equal(PaymentReservationStatus.Completed, reservation.Status);
            // 2 kWh x 0.40 + 0.50 session fee + 5 idle minutes x 0.20, not ten idle minutes.
            Assert.Equal(230, Assert.Single(fake.Captures).AmountMinor);
            Assert.Equal(5, transaction.IdleUsageFeeMinutes);
        }

        [Fact]
        public void Complete_TransientFailurePastTheRetryWindowFailsForAnOperator()
        {
            using var context = CreateContext();
            var reservation = AddChargingReservation(context, FakeProvider, "fake_tx_retry_window");
            var transaction = AddStoppedTransaction(context, reservation, meterStopKwh: 2);
            reservation.DisconnectedAtUtc = Now.AddHours(-2);
            context.SaveChanges();
            var fake = new FakePaymentGateway
            {
                GetAuthorizationException = new PaymentProviderException(FakeProvider, "capture_state_unknown", "Captured amount unknown", isTransient: true)
            };
            var coordinator = CreateCoordinator(fake);

            coordinator.CompleteReservation(context, transaction);

            Assert.Equal(PaymentReservationStatus.Failed, reservation.Status);
            Assert.Equal("settlement_retry_exhausted", reservation.FailureCode);
            Assert.Contains("capture_state_unknown", reservation.LastError);
        }

        [Fact]
        public void Complete_UnverifiedCaptureCompletesForReviewWithoutInvoice()
        {
            using var context = CreateContext();
            var reservation = AddChargingReservation(context, FakeProvider, "fake_tx_unverified");
            var transaction = AddStoppedTransaction(context, reservation, meterStopKwh: 2);
            var invoices = new FakeInvoiceIntegrationService();
            var fake = new FakePaymentGateway
            {
                AuthorizationResponse = Authorization(reservation.ReservationId, "fake_tx_unverified", PaymentAuthorizationState.RequiresCapture, 450),
                CaptureResponse = new PaymentAuthorizationSnapshot
                {
                    AuthorizationReference = "fake_tx_unverified",
                    State = PaymentAuthorizationState.Captured,
                    RawStatus = SaferpayPaymentGateway.UnverifiedCaptureStatus,
                    AmountMinor = 450,
                    AmountReceivedMinor = 130,
                    OwnerReservationId = reservation.ReservationId
                }
            };
            var coordinator = CreateCoordinator(fake, invoiceIntegrationService: invoices);

            coordinator.CompleteReservation(context, transaction);

            Assert.Equal(PaymentReservationStatus.Completed, reservation.Status);
            Assert.Equal(130, reservation.CapturedAmountCents);
            Assert.Equal("capture_amount_unverified", reservation.FailureCode);
            Assert.Contains("could not confirm the captured amount", reservation.LastError);
            Assert.Equal(0, invoices.HandleCompletedReservationCount);
        }

        // ---- Resume: provider checks of placeholder-less providers are throttled ----

        [Fact]
        public void Resume_ProviderChecksAreThrottled()
        {
            using var context = CreateContext();
            var reservation = AddPendingReservation(context, FakeProvider, "fake_token_resume_throttle");
            var fake = FakePaymentGateway.SaferpayLike();
            fake.CheckoutStatus = new PaymentCheckoutStatus
            {
                SessionReference = "fake_token_resume_throttle",
                State = PaymentCheckoutState.Open,
                RawStatus = "PENDING",
                OwnerReservationId = reservation.ReservationId
            };
            var coordinator = CreateCoordinator(fake);

            var first = coordinator.ResumeReservation(context, reservation.ReservationId);
            var second = coordinator.ResumeReservation(context, reservation.ReservationId);

            Assert.Equal("ResumeUnavailable", first.Status);
            // The throttled Resume sends the payer to the polling status page, without the cancel prompt.
            Assert.True(second.Success);
            Assert.Equal("Status", second.Status);
            Assert.Null(second.Error);
            Assert.Single(fake.Calls, call => call.StartsWith("GetCheckout", StringComparison.Ordinal));
            Assert.Equal(PaymentReservationStatus.Pending, reservation.Status);
        }

        // ---- helpers ----

        /// <summary>An authorized reservation that ended without a linked transaction (start rejected or timed out).</summary>
        private static ChargePaymentReservation AddAuthorizedTerminalReservation(
            OCPPCoreContext context,
            string status,
            string checkoutReference,
            string authorizationReference)
        {
            var reservation = new ChargePaymentReservation
            {
                ReservationId = Guid.NewGuid(),
                ChargePointId = "CP-LATE",
                ConnectorId = 1,
                ChargeTagId = "TAG1",
                OcppIdTag = "RLATETAG0000000001",
                PaymentProvider = FakeProvider,
                StripeCheckoutSessionId = checkoutReference,
                StripePaymentIntentId = authorizationReference,
                Status = status,
                MaxAmountCents = 450,
                Currency = "eur",
                CreatedAtUtc = Now.AddMinutes(-30),
                AuthorizedAtUtc = Now.AddMinutes(-28),
                StartDeadlineAtUtc = Now.AddMinutes(-21),
                RemoteStartSentAtUtc = Now.AddMinutes(-27),
                UpdatedAtUtc = Now.AddMinutes(-20)
            };
            context.ChargePaymentReservations.Add(reservation);
            context.SaveChanges();
            return reservation;
        }
    }
}
