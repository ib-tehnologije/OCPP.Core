using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OCPP.Core.Database;
using OCPP.Core.Server.Payments;
using OCPP.Core.Server.Payments.Providers;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    /// <summary>
    /// Money-safety behaviour against a Saferpay-like gateway: holds are released for every reservation that ends
    /// without a charging session, payments captured at checkout are never started, and unconfirmed
    /// cancellations stay armed for the release reconciliation.
    /// </summary>
    public partial class PaymentCoordinatorProviderTests
    {
        // ---- Release reconciliation: never-authorized rows, all terminal statuses, open/expired checkouts ----

        [Fact]
        public void Release_SweptNeverAuthorizedReservation_LinksInlineAuthorizationAndCancelsIt()
        {
            using var context = CreateContext();
            var reservation = AddSweptReservation(context, "fake_token_swept");
            var fake = FakePaymentGateway.SaferpayLike();
            fake.CheckoutStatus = CompletedCheckout(reservation.ReservationId, "fake_token_swept", "fake_tx_swept", inlineAuthorization: true, amountMinor: 450);
            var coordinator = CreateCoordinator(fake);

            var result = coordinator.ReconcileTerminalPaymentAuthorization(context, reservation, PaymentAuthorizationReleaseTrigger.CleanupSweep);

            Assert.Equal(PaymentAuthorizationReleaseOutcome.Released, result.Outcome);
            Assert.Equal(PaymentAuthorizationReleaseState.Released, reservation.AuthorizationReleaseState);
            Assert.Equal("fake_tx_swept", reservation.StripePaymentIntentId);
            var cancel = Assert.Single(fake.Cancels);
            Assert.Equal("fake_tx_swept", cancel.Reference);
            Assert.Equal($"authorization_release:{reservation.ReservationId}:1", cancel.IdempotencyKey);
            // The authorization returned with the checkout is used; a separate lookup can lag behind it.
            Assert.DoesNotContain(fake.Calls, call => call.StartsWith("GetAuthorization", StringComparison.Ordinal));
        }

        [Fact]
        public void Release_StartupAbandonedReservation_IsCheckedWithTheProvider()
        {
            using var context = CreateContext();
            var reservation = AddPendingReservation(context, FakeProvider, "fake_token_startup");
            reservation.UpdatedAtUtc = Now.AddHours(-2);
            context.SaveChanges();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Maintenance:PendingPaymentTimeoutMinutes"] = "15" })
                .Build();
            StartupMaintenance.Run(context, NullLogger.Instance, configuration, () => Now);
            Assert.Equal(PaymentReservationStatus.Abandoned, reservation.Status);
            Assert.Equal(PaymentAuthorizationReleaseState.Pending, reservation.AuthorizationReleaseState);

            var fake = FakePaymentGateway.SaferpayLike();
            fake.CheckoutStatus = new PaymentCheckoutStatus
            {
                SessionReference = "fake_token_startup",
                State = PaymentCheckoutState.Canceled,
                RawStatus = "TRANSACTION_ABORTED",
                OwnerReservationId = reservation.ReservationId
            };
            var coordinator = CreateCoordinator(fake);

            var result = coordinator.ReconcileTerminalPaymentAuthorization(context, reservation, PaymentAuthorizationReleaseTrigger.CleanupSweep);

            Assert.Equal(PaymentAuthorizationReleaseOutcome.AlreadyReleased, result.Outcome);
            Assert.Equal(PaymentAuthorizationReleaseState.Released, reservation.AuthorizationReleaseState);
            Assert.Contains("GetCheckout:fake_token_startup", fake.Calls);
            Assert.Contains($"FindAuthorizationByReservation:{reservation.ReservationId}", fake.Calls);
        }

        [Theory]
        [InlineData(PaymentReservationStatus.StartRejected)]
        [InlineData(PaymentReservationStatus.StartTimeout)]
        [InlineData(PaymentReservationStatus.Cancelled)]
        public void Release_ArmedReservationInEveryTerminalNoChargeStatusIsReleased(string status)
        {
            using var context = CreateContext();
            var reservation = AddReleaseReservation(context, FakeProvider, authorizationReference: "fake_tx_terminal");
            reservation.Status = status;
            context.SaveChanges();
            var fake = new FakePaymentGateway
            {
                AuthorizationResponse = Authorization(reservation.ReservationId, "fake_tx_terminal", PaymentAuthorizationState.RequiresCapture, 450)
            };
            var coordinator = CreateCoordinator(fake);

            var result = coordinator.ReconcileTerminalPaymentAuthorization(context, reservation, PaymentAuthorizationReleaseTrigger.CleanupSweep);

            Assert.Equal(PaymentAuthorizationReleaseOutcome.Released, result.Outcome);
            Assert.Equal(PaymentAuthorizationReleaseState.Released, reservation.AuthorizationReleaseState);
            Assert.Equal("fake_tx_terminal", Assert.Single(fake.Cancels).Reference);
        }

        [Fact]
        public void Release_TokenOnlyCompleteCheckoutUsesInlineAuthorizationWithoutLookup()
        {
            using var context = CreateContext();
            var reservation = AddReleaseReservation(context, FakeProvider, authorizationReference: null, checkoutReference: "fake_token_inline");
            var fake = new FakePaymentGateway
            {
                CheckoutStatus = CompletedCheckout(reservation.ReservationId, "fake_token_inline", "fake_tx_inline", inlineAuthorization: true, amountMinor: 450)
            };
            var coordinator = CreateCoordinator(fake);

            var result = coordinator.ReconcileTerminalPaymentAuthorization(context, reservation, PaymentAuthorizationReleaseTrigger.CleanupSweep);

            Assert.Equal(PaymentAuthorizationReleaseOutcome.Released, result.Outcome);
            Assert.Equal("fake_tx_inline", reservation.StripePaymentIntentId);
            Assert.Single(fake.Cancels);
            Assert.DoesNotContain(fake.Calls, call => call.StartsWith("GetAuthorization", StringComparison.Ordinal));
            Assert.Equal(450, context.PaymentAuthorizationReleaseAttempts.Single().AmountCapturableCents);
        }

        [Fact]
        public void Release_OpenCheckoutWaitsForPageLifetimeWithoutSpendingRetryBudget()
        {
            using var context = CreateContext();
            var reservation = AddSweptReservation(context, "fake_token_open_page");
            var lifetimeEnd = reservation.CreatedAtUtc.AddHours(1).AddMinutes(5);
            var fake = FakePaymentGateway.SaferpayLike();
            fake.CheckoutStatus = new PaymentCheckoutStatus
            {
                SessionReference = "fake_token_open_page",
                State = PaymentCheckoutState.Open,
                RawStatus = "TRANSACTION_IN_WRONG_STATE",
                OwnerReservationId = reservation.ReservationId
            };
            var clock = Now;
            var coordinator = CreateCoordinator(fake, now: () => clock);

            var waiting = coordinator.ReconcileTerminalPaymentAuthorization(context, reservation, PaymentAuthorizationReleaseTrigger.CleanupSweep);

            Assert.Equal(PaymentAuthorizationReleaseOutcome.RetryScheduled, waiting.Outcome);
            Assert.Equal(lifetimeEnd, waiting.NextRetryAtUtc);
            Assert.Equal(PaymentAuthorizationReleaseState.RetryScheduled, reservation.AuthorizationReleaseState);
            Assert.Equal(lifetimeEnd, reservation.AuthorizationReleaseNextAttemptAtUtc);
            Assert.Equal(0, reservation.AuthorizationReleaseAttemptCount);
            Assert.Empty(context.PaymentAuthorizationReleaseAttempts);
            Assert.DoesNotContain(fake.Calls, call => call.StartsWith("FindAuthorizationByReservation", StringComparison.Ordinal));

            clock = Now.AddMinutes(10);
            var early = coordinator.ReconcileTerminalPaymentAuthorization(context, reservation, PaymentAuthorizationReleaseTrigger.CleanupSweep);
            Assert.Equal(PaymentAuthorizationReleaseOutcome.SkippedNotEligible, early.Outcome);
            Assert.Equal(1, fake.Calls.Count(call => call.StartsWith("GetCheckout", StringComparison.Ordinal)));

            // Once the page can no longer be completed, an open checkout means nothing was authorized.
            clock = lifetimeEnd.AddMinutes(1);
            var final = coordinator.ReconcileTerminalPaymentAuthorization(context, reservation, PaymentAuthorizationReleaseTrigger.CleanupSweep);

            Assert.Equal(PaymentAuthorizationReleaseOutcome.AlreadyReleased, final.Outcome);
            Assert.Equal(PaymentAuthorizationReleaseState.Released, reservation.AuthorizationReleaseState);
            Assert.Equal(1, reservation.AuthorizationReleaseAttemptCount);
            Assert.Contains($"FindAuthorizationByReservation:{reservation.ReservationId}", fake.Calls);
            Assert.Empty(fake.Cancels);
        }

        [Fact]
        public void Release_ExpiredCheckoutFindsAuthorizationByReservationAndCancelsIt()
        {
            using var context = CreateContext();
            var reservation = AddSweptReservation(context, "fake_token_expired");
            var fake = FakePaymentGateway.SaferpayLike();
            fake.CheckoutStatus = new PaymentCheckoutStatus
            {
                SessionReference = "fake_token_expired",
                State = PaymentCheckoutState.Expired,
                RawStatus = "TOKEN_EXPIRED",
                OwnerReservationId = reservation.ReservationId
            };
            fake.AuthorizationByReservation = Authorization(reservation.ReservationId, "fake_tx_by_order", PaymentAuthorizationState.RequiresCapture, 450);
            var coordinator = CreateCoordinator(fake);

            var result = coordinator.ReconcileTerminalPaymentAuthorization(context, reservation, PaymentAuthorizationReleaseTrigger.CleanupSweep);

            Assert.Equal(PaymentAuthorizationReleaseOutcome.Released, result.Outcome);
            Assert.Equal("fake_tx_by_order", reservation.StripePaymentIntentId);
            Assert.Equal("fake_tx_by_order", Assert.Single(fake.Cancels).Reference);
            Assert.DoesNotContain(fake.Calls, call => call.StartsWith("GetAuthorization", StringComparison.Ordinal));
        }

        [Fact]
        public void Release_AuthorizationFoundForAnotherReservationRequiresReview()
        {
            using var context = CreateContext();
            var reservation = AddSweptReservation(context, "fake_token_foreign");
            var fake = FakePaymentGateway.SaferpayLike();
            fake.CheckoutStatus = new PaymentCheckoutStatus
            {
                SessionReference = "fake_token_foreign",
                State = PaymentCheckoutState.Expired,
                RawStatus = "TOKEN_EXPIRED",
                OwnerReservationId = reservation.ReservationId
            };
            fake.AuthorizationByReservation = Authorization(Guid.NewGuid(), "fake_tx_foreign", PaymentAuthorizationState.RequiresCapture, 450);
            var coordinator = CreateCoordinator(fake);

            var result = coordinator.ReconcileTerminalPaymentAuthorization(context, reservation, PaymentAuthorizationReleaseTrigger.CleanupSweep);

            Assert.Equal(PaymentAuthorizationReleaseOutcome.ReviewRequired, result.Outcome);
            Assert.Null(reservation.StripePaymentIntentId);
            Assert.Empty(fake.Cancels);
        }

        // ---- Late authorizations of ended reservations ----

        [Theory]
        [InlineData(PaymentReservationStatus.Abandoned, PaymentAuthorizationReleaseState.ReviewRequired)]
        [InlineData(PaymentReservationStatus.Abandoned, PaymentAuthorizationReleaseState.PermanentFailure)]
        [InlineData(PaymentReservationStatus.Failed, PaymentAuthorizationReleaseState.Released)]
        [InlineData(PaymentReservationStatus.StartRejected, null)]
        [InlineData(PaymentReservationStatus.StartTimeout, PaymentAuthorizationReleaseState.ReviewRequired)]
        public async Task CheckoutNotification_LateAuthorizationOfAnyEndedReservationIsReleased(string status, string? releaseState)
        {
            using var context = CreateContext();
            var reservation = AddPendingReservation(context, FakeProvider, "fake_token_ended");
            reservation.Status = status;
            reservation.AuthorizationReleaseState = releaseState;
            reservation.AuthorizationReleaseAttemptCount = releaseState == null ? 0 : 4;
            context.SaveChanges();
            var fake = FakePaymentGateway.SaferpayLike();
            fake.CheckoutStatus = CompletedCheckout(reservation.ReservationId, "fake_token_ended", "fake_tx_ended", inlineAuthorization: true, amountMinor: 450);
            fake.ParseResult = Notification(reservation.ReservationId, eventId: null);
            var coordinator = CreateCoordinator(fake);

            var result = await coordinator.HandleWebhookAsync(context, NotifyRequest(), CancellationToken.None);

            Assert.Equal(200, result.StatusCode);
            Assert.Empty(result.AuthorizedReservationIds);
            Assert.Equal(status, reservation.Status);
            Assert.Equal("fake_tx_ended", reservation.StripePaymentIntentId);
            Assert.Equal("late_authorization_released", reservation.LastError);
            Assert.Equal(PaymentAuthorizationReleaseState.Released, reservation.AuthorizationReleaseState);
            Assert.Equal(Now, reservation.AuthorizationReleasedAtUtc);
            var cancel = Assert.Single(fake.Cancels);
            Assert.Equal($"cancel:{reservation.ReservationId}", cancel.IdempotencyKey);
        }

        [Fact]
        public async Task CheckoutNotification_TransientLateReleaseFailureArmsReleaseAndAsksForRetry()
        {
            using var context = CreateContext();
            var reservation = AddPendingReservation(context, FakeProvider, "fake_token_late_arm");
            reservation.Status = PaymentReservationStatus.Abandoned;
            reservation.AuthorizationReleaseState = PaymentAuthorizationReleaseState.ReviewRequired;
            context.SaveChanges();
            var fake = FakePaymentGateway.SaferpayLike();
            fake.CheckoutStatus = CompletedCheckout(reservation.ReservationId, "fake_token_late_arm", "fake_tx_late_arm", inlineAuthorization: true, amountMinor: 450);
            fake.ParseResult = Notification(reservation.ReservationId, eventId: null);
            fake.CancelException = new PaymentProviderException(FakeProvider, "timeout", "Provider timeout", isTransient: true, httpStatusCode: 504);
            var coordinator = CreateCoordinator(fake);

            var result = await coordinator.HandleWebhookAsync(context, NotifyRequest(), CancellationToken.None);

            Assert.Equal(503, result.StatusCode);
            var persisted = context.ChargePaymentReservations.AsNoTracking().Single(r => r.ReservationId == reservation.ReservationId);
            Assert.Equal(PaymentAuthorizationReleaseState.Pending, persisted.AuthorizationReleaseState);
            Assert.Equal("fake_tx_late_arm", persisted.StripePaymentIntentId);
            Assert.Contains("timeout", persisted.AuthorizationReleaseLastError);
        }

        [Fact]
        public void Confirm_BrowserReturnForCancelledReservationReleasesLateAuthorization()
        {
            using var context = CreateContext();
            var reservation = AddPendingReservation(context, FakeProvider, "fake_token_return");
            reservation.Status = PaymentReservationStatus.Cancelled;
            reservation.LastError = "checkout_cancelled";
            context.SaveChanges();
            var fake = FakePaymentGateway.SaferpayLike();
            fake.CheckoutStatus = CompletedCheckout(reservation.ReservationId, "fake_token_return", "fake_tx_return", inlineAuthorization: true, amountMinor: 450);
            var coordinator = CreateCoordinator(fake);

            var result = coordinator.ConfirmReservation(context, reservation.ReservationId, null);

            Assert.False(result.Success);
            Assert.Equal(PaymentReservationStatus.Cancelled, result.Status);
            Assert.Equal(PaymentReservationStatus.Cancelled, reservation.Status);
            Assert.Equal("fake_tx_return", reservation.StripePaymentIntentId);
            Assert.Equal("late_authorization_released", reservation.LastError);
            Assert.Equal(PaymentAuthorizationReleaseState.Released, reservation.AuthorizationReleaseState);
            Assert.Equal($"cancel:{reservation.ReservationId}", Assert.Single(fake.Cancels).IdempotencyKey);
        }

        // ---- Cancel of a token-only pending reservation ----

        [Fact]
        public void CancelReservation_PendingCheckoutWithAuthorizationReleasesIt()
        {
            using var context = CreateContext();
            var reservation = AddPendingReservation(context, FakeProvider, "fake_token_cancel_paid");
            var fake = FakePaymentGateway.SaferpayLike();
            fake.CheckoutStatus = CompletedCheckout(reservation.ReservationId, "fake_token_cancel_paid", "fake_tx_cancel_paid", inlineAuthorization: true, amountMinor: 450);
            var coordinator = CreateCoordinator(fake);

            coordinator.CancelReservation(context, reservation.ReservationId, "checkout_cancelled");

            Assert.Equal(PaymentReservationStatus.Cancelled, reservation.Status);
            Assert.Equal("checkout_cancelled", reservation.LastError);
            Assert.Equal("fake_tx_cancel_paid", reservation.StripePaymentIntentId);
            Assert.Equal(PaymentAuthorizationReleaseState.Released, reservation.AuthorizationReleaseState);
            var cancel = Assert.Single(fake.Cancels);
            Assert.Equal("fake_tx_cancel_paid", cancel.Reference);
            Assert.Equal($"cancel:{reservation.ReservationId}", cancel.IdempotencyKey);
            Assert.DoesNotContain(fake.Calls, call => call.StartsWith("GetAuthorization", StringComparison.Ordinal));
        }

        [Fact]
        public void CancelReservation_OpenCheckoutArmsReleaseThatFindsALateAuthorizationAfterThePageLifetime()
        {
            using var context = CreateContext();
            var reservation = AddPendingReservation(context, FakeProvider, "fake_token_cancel_open");
            var fake = FakePaymentGateway.SaferpayLike();
            fake.CheckoutStatus = new PaymentCheckoutStatus
            {
                SessionReference = "fake_token_cancel_open",
                State = PaymentCheckoutState.Open,
                RawStatus = "TRANSACTION_IN_WRONG_STATE",
                OwnerReservationId = reservation.ReservationId
            };
            var clock = Now;
            var coordinator = CreateCoordinator(fake, now: () => clock);

            coordinator.CancelReservation(context, reservation.ReservationId, "checkout_cancelled");

            Assert.Equal(PaymentReservationStatus.Cancelled, reservation.Status);
            Assert.Equal(PaymentAuthorizationReleaseState.Pending, reservation.AuthorizationReleaseState);
            Assert.Empty(fake.Cancels);

            // The first release attempt is deferred by one in-progress timeout (the cancel request's own window).
            clock = Now.AddMinutes(6);
            var waiting = coordinator.ReconcileTerminalPaymentAuthorization(context, reservation, PaymentAuthorizationReleaseTrigger.CleanupSweep);
            Assert.Equal(PaymentAuthorizationReleaseOutcome.RetryScheduled, waiting.Outcome);

            // The payer completed the page later and no notification arrived; the token can no longer be asserted.
            clock = reservation.CreatedAtUtc.AddHours(2);
            fake.CheckoutStatus = new PaymentCheckoutStatus
            {
                SessionReference = "fake_token_cancel_open",
                State = PaymentCheckoutState.Open,
                RawStatus = "TRANSACTION_IN_WRONG_STATE",
                OwnerReservationId = reservation.ReservationId
            };
            fake.AuthorizationByReservation = Authorization(reservation.ReservationId, "fake_tx_after_cancel", PaymentAuthorizationState.RequiresCapture, 450);

            var released = coordinator.ReconcileTerminalPaymentAuthorization(context, reservation, PaymentAuthorizationReleaseTrigger.CleanupSweep);

            Assert.Equal(PaymentAuthorizationReleaseOutcome.Released, released.Outcome);
            Assert.Equal("fake_tx_after_cancel", reservation.StripePaymentIntentId);
            Assert.Equal("fake_tx_after_cancel", Assert.Single(fake.Cancels).Reference);
        }

        [Fact]
        public void CancelReservation_CheckoutLookupFailureArmsRelease()
        {
            using var context = CreateContext();
            var reservation = AddPendingReservation(context, FakeProvider, "fake_token_cancel_error");
            var fake = FakePaymentGateway.SaferpayLike();
            fake.GetCheckoutException = new PaymentProviderException(FakeProvider, "timeout", "Provider timeout", isTransient: true, httpStatusCode: 504);
            var coordinator = CreateCoordinator(fake);

            coordinator.CancelReservation(context, reservation.ReservationId, "checkout_cancelled");

            Assert.Equal(PaymentReservationStatus.Cancelled, reservation.Status);
            Assert.Equal(PaymentAuthorizationReleaseState.Pending, reservation.AuthorizationReleaseState);
            Assert.Contains("timeout", reservation.AuthorizationReleaseLastError);
        }

        [Fact]
        public void CancelReservation_AbortedCheckoutHoldsNothingAndIsNotArmed()
        {
            using var context = CreateContext();
            var reservation = AddPendingReservation(context, FakeProvider, "fake_token_cancel_aborted");
            var fake = FakePaymentGateway.SaferpayLike();
            fake.CheckoutStatus = new PaymentCheckoutStatus
            {
                SessionReference = "fake_token_cancel_aborted",
                State = PaymentCheckoutState.Canceled,
                RawStatus = "TRANSACTION_ABORTED",
                OwnerReservationId = reservation.ReservationId
            };
            var coordinator = CreateCoordinator(fake);

            coordinator.CancelReservation(context, reservation.ReservationId, "checkout_cancelled");

            Assert.Equal(PaymentReservationStatus.Cancelled, reservation.Status);
            Assert.Null(reservation.AuthorizationReleaseState);
            Assert.Empty(fake.Cancels);
        }

        [Fact]
        public void CancelReservation_ProviderWithoutCheckoutLifetimeIsNotLookedUp()
        {
            using var context = CreateContext();
            var reservation = AddPendingReservation(context, FakeProvider, "fake_token_cancel_plain");
            var fake = new FakePaymentGateway();
            var coordinator = CreateCoordinator(fake);

            coordinator.CancelReservation(context, reservation.ReservationId, "checkout_cancelled");

            Assert.Equal(PaymentReservationStatus.Cancelled, reservation.Status);
            Assert.Null(reservation.AuthorizationReleaseState);
            Assert.Empty(fake.Calls);
        }

        // ---- Cancelling an authorization that cannot be confirmed ----

        [Fact]
        public void CancelPaymentIntent_AuthorizationNotFoundArmsPersistedReleaseThatTheSweepFinishes()
        {
            using var context = CreateContext();
            var reservation = AddReleaseReservation(context, FakeProvider, authorizationReference: "fake_tx_lagging");
            reservation.Status = PaymentReservationStatus.StartRejected;
            reservation.AuthorizationReleaseState = null;
            context.SaveChanges();
            var fake = FakePaymentGateway.SaferpayLike();
            var coordinator = CreateCoordinator(fake);

            // The middleware saves StartRejected first and then asks for the cancel without saving again.
            coordinator.CancelPaymentIntentIfCancelable(context, reservation, "Remote start rejected");

            var persisted = context.ChargePaymentReservations.AsNoTracking().Single(r => r.ReservationId == reservation.ReservationId);
            Assert.Equal(PaymentAuthorizationReleaseState.Pending, persisted.AuthorizationReleaseState);
            Assert.Contains("not found", persisted.LastError);
            Assert.Contains("not found", persisted.AuthorizationReleaseLastError);
            Assert.Empty(fake.Cancels);

            fake.AuthorizationResponse = Authorization(reservation.ReservationId, "fake_tx_lagging", PaymentAuthorizationState.RequiresCapture, 450);
            var result = coordinator.ReconcileTerminalPaymentAuthorization(context, reservation, PaymentAuthorizationReleaseTrigger.CleanupSweep);

            Assert.Equal(PaymentAuthorizationReleaseOutcome.Released, result.Outcome);
            Assert.Single(fake.Cancels);
        }

        [Fact]
        public void CancelPaymentIntent_ProviderErrorArmsRelease()
        {
            using var context = CreateContext();
            var reservation = AddReleaseReservation(context, FakeProvider, authorizationReference: "fake_tx_error");
            reservation.Status = PaymentReservationStatus.StartTimeout;
            reservation.AuthorizationReleaseState = null;
            context.SaveChanges();
            var fake = new FakePaymentGateway
            {
                GetAuthorizationException = new PaymentProviderException(FakeProvider, "timeout", "Provider timeout", isTransient: true, httpStatusCode: 504)
            };
            var coordinator = CreateCoordinator(fake);

            coordinator.CancelPaymentIntentIfCancelable(context, reservation, "Start window expired");

            Assert.Equal(PaymentAuthorizationReleaseState.Pending, reservation.AuthorizationReleaseState);
            Assert.Equal("Provider timeout", reservation.LastError);
            Assert.Contains("timeout", reservation.AuthorizationReleaseLastError);
        }

        [Fact]
        public void CancelPaymentIntent_UnconfirmedCancellationArmsRelease()
        {
            using var context = CreateContext();
            var reservation = AddReleaseReservation(context, FakeProvider, authorizationReference: "fake_tx_unconfirmed");
            reservation.Status = PaymentReservationStatus.StartRejected;
            reservation.AuthorizationReleaseState = null;
            context.SaveChanges();
            var fake = new FakePaymentGateway
            {
                AuthorizationResponse = Authorization(reservation.ReservationId, "fake_tx_unconfirmed", PaymentAuthorizationState.RequiresCapture, 450),
                CancelResponse = Authorization(reservation.ReservationId, "fake_tx_unconfirmed", PaymentAuthorizationState.RequiresCapture, 450)
            };
            var coordinator = CreateCoordinator(fake);

            coordinator.CancelPaymentIntentIfCancelable(context, reservation, "Remote start rejected");

            Assert.Single(fake.Cancels);
            Assert.Equal(PaymentAuthorizationReleaseState.Pending, reservation.AuthorizationReleaseState);
            Assert.Contains("AUTHORIZED", reservation.LastError);
        }

        [Fact]
        public void CancelPaymentIntent_UnconfiguredOwnerProviderArmsRelease()
        {
            using var context = CreateContext();
            var reservation = AddReleaseReservation(context, FakeProvider, authorizationReference: "fake_tx_unconfigured");
            reservation.Status = PaymentReservationStatus.Authorized;
            reservation.AuthorizationReleaseState = null;
            context.SaveChanges();
            var fake = new FakePaymentGateway { IsConfigured = false };
            var coordinator = CreateCoordinator(fake);
            Assert.True(coordinator.IsOperational);

            coordinator.CancelReservation(context, reservation.ReservationId, "Stopped by user before charging started.");

            Assert.Equal(PaymentReservationStatus.Cancelled, reservation.Status);
            Assert.Equal(PaymentAuthorizationReleaseState.Pending, reservation.AuthorizationReleaseState);
            Assert.Contains("not configured", reservation.AuthorizationReleaseLastError);
            Assert.Empty(fake.Calls);
        }

        // ---- Settlement cancel/capture results ----

        [Fact]
        public void Complete_ZeroAmountCancelThatIsNotConfirmedIsRetriedInsteadOfCancelling()
        {
            using var context = CreateContext();
            var reservation = AddChargingReservation(context, FakeProvider, "fake_tx_zero_unconfirmed");
            var transaction = AddStoppedTransaction(context, reservation, meterStopKwh: 0);
            var fake = new FakePaymentGateway
            {
                AuthorizationResponse = Authorization(reservation.ReservationId, "fake_tx_zero_unconfirmed", PaymentAuthorizationState.RequiresCapture, 450),
                CancelResponse = Authorization(reservation.ReservationId, "fake_tx_zero_unconfirmed", PaymentAuthorizationState.RequiresCapture, 450)
            };
            var coordinator = CreateCoordinator(fake);

            coordinator.CompleteReservation(context, transaction);

            // The unconfirmed cancellation (transient) is not treated as a release: the settlement is retried.
            Assert.Equal(PaymentReservationStatus.Charging, reservation.Status);
            Assert.Equal("settlement_retry_pending", reservation.FailureCode);
            Assert.Contains("cancel_not_confirmed", reservation.LastError);
            Assert.Null(reservation.CapturedAmountCents);
        }

        [Fact]
        public void Complete_ZeroAmountCancelFindingACaptureRecordsTheCapturedAmount()
        {
            using var context = CreateContext();
            var reservation = AddChargingReservation(context, FakeProvider, "fake_tx_zero_captured");
            var transaction = AddStoppedTransaction(context, reservation, meterStopKwh: 0);
            var invoices = new FakeInvoiceIntegrationService();
            var fake = new FakePaymentGateway
            {
                AuthorizationResponse = Authorization(reservation.ReservationId, "fake_tx_zero_captured", PaymentAuthorizationState.RequiresCapture, 450),
                CancelResponse = CapturedAuthorization(reservation.ReservationId, "fake_tx_zero_captured", 450)
            };
            var coordinator = CreateCoordinator(fake, invoiceIntegrationService: invoices);

            coordinator.CompleteReservation(context, transaction);

            Assert.Equal(PaymentReservationStatus.Completed, reservation.Status);
            Assert.Equal(450, reservation.CapturedAmountCents);
            Assert.Equal(Now, reservation.CapturedAtUtc);
            Assert.Contains("already captured", reservation.LastError);
            Assert.Equal(0, invoices.HandleCompletedReservationCount);
        }

        [Fact]
        public void Complete_BelowMinimumCancelFindingACaptureIssuesNoInvoice()
        {
            using var context = CreateContext();
            var reservation = AddChargingReservation(context, FakeProvider, "fake_tx_min_captured");
            var transaction = AddStoppedTransaction(context, reservation, meterStopKwh: 2.5);
            var invoices = new FakeInvoiceIntegrationService();
            var fake = new FakePaymentGateway
            {
                AuthorizationResponse = Authorization(reservation.ReservationId, "fake_tx_min_captured", PaymentAuthorizationState.RequiresCapture, 450),
                CancelResponse = CapturedAuthorization(reservation.ReservationId, "fake_tx_min_captured", 450)
            };
            var coordinator = CreateCoordinator(
                fake,
                invoiceIntegrationService: invoices,
                flowOptions: new PaymentFlowOptions { StartWindowMinutes = 7, MinimumChargeAmountCents = 500 });

            coordinator.CompleteReservation(context, transaction);

            Assert.Equal(PaymentReservationStatus.Completed, reservation.Status);
            Assert.Equal(450, reservation.CapturedAmountCents);
            Assert.Equal(0, invoices.HandleCompletedReservationCount);
            Assert.Empty(fake.Captures);
        }

        [Fact]
        public void Complete_PersistsTheAmountTheProviderReportsCaptured()
        {
            using var context = CreateContext();
            var reservation = AddChargingReservation(context, FakeProvider, "fake_tx_capture_amount");
            var transaction = AddStoppedTransaction(context, reservation, meterStopKwh: 2.5);
            var fake = new FakePaymentGateway
            {
                AuthorizationResponse = Authorization(reservation.ReservationId, "fake_tx_capture_amount", PaymentAuthorizationState.RequiresCapture, 450),
                CaptureResponse = CapturedAuthorization(reservation.ReservationId, "fake_tx_capture_amount", 140)
            };
            var coordinator = CreateCoordinator(fake);

            coordinator.CompleteReservation(context, transaction);

            Assert.Equal(PaymentReservationStatus.Completed, reservation.Status);
            Assert.Equal(150, Assert.Single(fake.Captures).AmountMinor);
            Assert.Equal(140, reservation.CapturedAmountCents);
        }

        [Fact]
        public void Complete_PendingCaptureIsNotReportedCompleted()
        {
            using var context = CreateContext();
            var reservation = AddChargingReservation(context, FakeProvider, "fake_tx_capture_pending");
            var transaction = AddStoppedTransaction(context, reservation, meterStopKwh: 2.5);
            var invoices = new FakeInvoiceIntegrationService();
            var fake = new FakePaymentGateway
            {
                AuthorizationResponse = Authorization(reservation.ReservationId, "fake_tx_capture_pending", PaymentAuthorizationState.RequiresCapture, 450),
                CaptureResponse = new PaymentAuthorizationSnapshot
                {
                    AuthorizationReference = "fake_tx_capture_pending",
                    State = PaymentAuthorizationState.Pending,
                    RawStatus = "PENDING",
                    AmountMinor = 150
                }
            };
            var coordinator = CreateCoordinator(fake, invoiceIntegrationService: invoices);

            coordinator.CompleteReservation(context, transaction);

            Assert.Equal(PaymentReservationStatus.Failed, reservation.Status);
            Assert.Equal("capture_pending", reservation.FailureCode);
            Assert.Equal(PaymentAuthorizationReleaseState.ReviewRequired, reservation.AuthorizationReleaseState);
            Assert.Null(reservation.CapturedAmountCents);
            Assert.Null(reservation.CapturedAtUtc);
            Assert.Equal(0, invoices.HandleCompletedReservationCount);
        }

        // ---- Payments captured at checkout ----

        [Fact]
        public void Confirm_CheckoutCapturedAtOnceFailsAndRecordsTheRefund()
        {
            using var context = CreateContext();
            var reservation = AddPendingReservation(context, FakeProvider, "fake_token_captured");
            var fake = FakePaymentGateway.SaferpayLike();
            fake.CheckoutStatus = CompletedCheckout(reservation.ReservationId, "fake_token_captured", "fake_tx_captured", inlineAuthorization: true, amountMinor: 450);
            fake.CheckoutStatus.Authorization = CapturedAuthorization(reservation.ReservationId, "fake_tx_captured", 450);
            var email = new FakeEmailNotificationService();
            var coordinator = CreateCoordinator(fake, emailService: email);

            var result = coordinator.ConfirmReservation(context, reservation.ReservationId, null);

            Assert.False(result.Success);
            Assert.Equal(PaymentReservationStatus.Failed, result.Status);
            Assert.Equal(PaymentReservationStatus.Failed, reservation.Status);
            Assert.Equal("captured_at_checkout", reservation.FailureCode);
            Assert.Equal("Payment was captured immediately by the payment method; refund required.", reservation.LastError);
            Assert.Equal(reservation.LastError, reservation.FailureMessage);
            Assert.Equal(450, reservation.CapturedAmountCents);
            Assert.Equal(Now, reservation.CapturedAtUtc);
            Assert.Equal("fake_tx_captured", reservation.StripePaymentIntentId);
            Assert.Null(reservation.OcppIdTag);
            Assert.Null(reservation.AuthorizedAtUtc);
            Assert.Equal(0, email.PaymentAuthorizedCount);
        }

        [Fact]
        public async Task CheckoutNotification_CapturedAtCheckoutIsNotAuthorizedOrStarted()
        {
            using var context = CreateContext();
            var reservation = AddPendingReservation(context, FakeProvider, "fake_token_captured_notify");
            var fake = FakePaymentGateway.SaferpayLike();
            fake.CheckoutStatus = CompletedCheckout(reservation.ReservationId, "fake_token_captured_notify", "fake_tx_captured_notify", inlineAuthorization: true, amountMinor: 450);
            fake.CheckoutStatus.Authorization = CapturedAuthorization(reservation.ReservationId, "fake_tx_captured_notify", 450);
            fake.ParseResult = Notification(reservation.ReservationId, "notify-captured");
            var coordinator = CreateCoordinator(fake);

            var result = await coordinator.HandleWebhookAsync(context, NotifyRequest(), CancellationToken.None);

            Assert.Equal(200, result.StatusCode);
            Assert.Empty(result.AuthorizedReservationIds);
            Assert.Equal(PaymentReservationStatus.Failed, reservation.Status);
            Assert.Equal("captured_at_checkout", reservation.FailureCode);
            Assert.Equal(450, reservation.CapturedAmountCents);
            Assert.Empty(fake.Cancels);
        }

        [Fact]
        public void Confirm_CapturedCheckoutOfProviderWithDeferredCaptureStaysAuthorized()
        {
            using var context = CreateContext();
            var reservation = AddPendingReservation(context, FakeProvider, "fake_token_captured_plain");
            var fake = new FakePaymentGateway
            {
                CheckoutStatus = CompletedCheckout(reservation.ReservationId, "fake_token_captured_plain", "fake_tx_captured_plain", inlineAuthorization: true, amountMinor: 450)
            };
            fake.CheckoutStatus.Authorization = CapturedAuthorization(reservation.ReservationId, "fake_tx_captured_plain", 450);
            var coordinator = CreateCoordinator(fake);

            var result = coordinator.ConfirmReservation(context, reservation.ReservationId, null);

            Assert.True(result.Success);
            Assert.Equal(PaymentReservationStatus.Authorized, reservation.Status);
            Assert.Null(reservation.CapturedAmountCents);
        }

        // ---- Confirm robustness for providers without a payer-supplied checkout reference ----

        [Fact]
        public void Confirm_AlreadyAuthorizedReservationIsNotRecheckedOrEmailedAgain()
        {
            using var context = CreateContext();
            var reservation = AddPendingReservation(context, FakeProvider, "fake_token_reconfirm");
            var fake = new FakePaymentGateway
            {
                CheckoutStatus = CompletedCheckout(reservation.ReservationId, "fake_token_reconfirm", "fake_tx_reconfirm", inlineAuthorization: true, amountMinor: 450)
            };
            var email = new FakeEmailNotificationService();
            var clock = Now;
            var coordinator = CreateCoordinator(fake, now: () => clock, emailService: email);

            var first = coordinator.ConfirmReservation(context, reservation.ReservationId, null);
            clock = Now.AddMinutes(1);
            var second = coordinator.ConfirmReservation(context, reservation.ReservationId, null);

            Assert.True(first.Success);
            Assert.True(second.Success);
            Assert.Equal(PaymentReservationStatus.Authorized, second.Status);
            Assert.Null(second.Error);
            Assert.Equal(1, fake.Calls.Count(call => call.StartsWith("GetCheckout", StringComparison.Ordinal)));
            Assert.Equal(1, email.PaymentAuthorizedCount);
            Assert.Equal(Now, reservation.AuthorizedAtUtc);
        }

        [Fact]
        public void Confirm_PendingReservationProviderChecksAreThrottled()
        {
            using var context = CreateContext();
            var reservation = AddPendingReservation(context, FakeProvider, "fake_token_throttle");
            var fake = new FakePaymentGateway
            {
                CheckoutStatus = new PaymentCheckoutStatus
                {
                    SessionReference = "fake_token_throttle",
                    State = PaymentCheckoutState.Open,
                    RawStatus = "TRANSACTION_IN_WRONG_STATE",
                    OwnerReservationId = reservation.ReservationId
                }
            };
            var clock = Now;
            var coordinator = CreateCoordinator(fake, now: () => clock);

            var first = coordinator.ConfirmReservation(context, reservation.ReservationId, null);
            clock = Now.AddSeconds(1);
            var throttled = coordinator.ConfirmReservation(context, reservation.ReservationId, null);
            clock = Now.AddSeconds(3);
            var later = coordinator.ConfirmReservation(context, reservation.ReservationId, null);

            Assert.Equal("TRANSACTION_IN_WRONG_STATE", first.Status);
            Assert.False(throttled.Success);
            Assert.Equal("Pending", throttled.Status);
            Assert.Equal("Payment is still being processed.", throttled.Error);
            Assert.Equal("TRANSACTION_IN_WRONG_STATE", later.Status);
            Assert.Equal(2, fake.Calls.Count(call => call.StartsWith("GetCheckout", StringComparison.Ordinal)));
            Assert.Equal(PaymentReservationStatus.Pending, reservation.Status);
        }

        // ---- Resume of a checkout the payer already paid ----

        [Fact]
        public void Resume_CompletedCheckoutConfirmsTheReservationAndShowsTheStatusPage()
        {
            using var context = CreateContext();
            var reservation = AddPendingReservation(context, FakeProvider, "fake_token_resume_paid");
            var fake = FakePaymentGateway.SaferpayLike();
            fake.CheckoutStatus = CompletedCheckout(reservation.ReservationId, "fake_token_resume_paid", "fake_tx_resume_paid", inlineAuthorization: true, amountMinor: 450);
            var coordinator = CreateCoordinator(fake);

            var result = coordinator.ResumeReservation(context, reservation.ReservationId);

            Assert.True(result.Success);
            Assert.Equal("Status", result.Status);
            Assert.Null(result.CheckoutUrl);
            Assert.Equal(PaymentReservationStatus.Authorized, reservation.Status);
            Assert.Equal("fake_tx_resume_paid", reservation.StripePaymentIntentId);
            Assert.False(string.IsNullOrWhiteSpace(reservation.OcppIdTag));
            Assert.Equal(Now.AddMinutes(7), reservation.StartDeadlineAtUtc);
        }

        [Fact]
        public void Resume_CheckoutCapturedAtOnceShowsTheStatusPageOfTheFailedReservation()
        {
            using var context = CreateContext();
            var reservation = AddPendingReservation(context, FakeProvider, "fake_token_resume_captured");
            var fake = FakePaymentGateway.SaferpayLike();
            fake.CheckoutStatus = CompletedCheckout(reservation.ReservationId, "fake_token_resume_captured", "fake_tx_resume_captured", inlineAuthorization: true, amountMinor: 450);
            fake.CheckoutStatus.Authorization = CapturedAuthorization(reservation.ReservationId, "fake_tx_resume_captured", 450);
            var coordinator = CreateCoordinator(fake);

            var result = coordinator.ResumeReservation(context, reservation.ReservationId);

            Assert.True(result.Success);
            Assert.Equal("Status", result.Status);
            Assert.Equal(PaymentReservationStatus.Failed, reservation.Status);
            Assert.Equal("captured_at_checkout", reservation.FailureCode);
        }

        // ---- Notification processing failures ----

        [Fact]
        public async Task CheckoutNotification_DatabaseFailureAsksTheProviderToRetry()
        {
            var options = new DbContextOptionsBuilder<OCPPCoreContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            var interceptor = new ThrowOnReservationUpdateInterceptor();
            var failingOptions = new DbContextOptionsBuilder<OCPPCoreContext>(options)
                .AddInterceptors(interceptor)
                .Options;
            Guid reservationId;
            using (var setup = new OCPPCoreContext(options))
            {
                reservationId = AddPendingReservation(setup, FakeProvider, "fake_token_db_failure").ReservationId;
            }

            using var context = new OCPPCoreContext(failingOptions);
            var fake = new FakePaymentGateway
            {
                CheckoutStatus = CompletedCheckout(reservationId, "fake_token_db_failure", "fake_tx_db_failure", inlineAuthorization: true, amountMinor: 450),
                ParseResult = Notification(reservationId, "notify-db-failure")
            };
            var coordinator = CreateCoordinator(fake);
            interceptor.Armed = true;

            var result = await coordinator.HandleWebhookAsync(context, NotifyRequest(), CancellationToken.None);

            Assert.Equal(500, result.StatusCode);
            Assert.Empty(result.AuthorizedReservationIds);
            Assert.Empty(context.ChangeTracker.Entries());
            using var verification = new OCPPCoreContext(options);
            Assert.Equal(PaymentReservationStatus.Pending, verification.ChargePaymentReservations.Single().Status);
            Assert.Empty(verification.StripeWebhookEvents);
        }

        // ---- Payer e-mail fallback ----

        [Fact]
        public void Confirm_AuthorizationEmailFallsBackToTheInvoiceBuyerEmail()
        {
            using var context = CreateContext();
            var reservation = AddPendingReservation(context, FakeProvider, "fake_token_r1_email");
            reservation.InvoiceBuyerEmail = "r1-buyer@example.com";
            context.SaveChanges();
            var fake = new FakePaymentGateway
            {
                CheckoutStatus = CompletedCheckout(reservation.ReservationId, "fake_token_r1_email", "fake_tx_r1_email", inlineAuthorization: true, amountMinor: 450)
            };
            fake.CheckoutStatus.CustomerEmail = null;
            var email = new FakeEmailNotificationService();
            var coordinator = CreateCoordinator(fake, emailService: email);

            var result = coordinator.ConfirmReservation(context, reservation.ReservationId, null);

            Assert.True(result.Success);
            Assert.Equal(1, email.PaymentAuthorizedCount);
            Assert.Equal("r1-buyer@example.com", email.LastToEmail);
        }

        [Fact]
        public void CheckoutDetails_FallBackToTheInvoiceBuyerEmailForIdleAndCompletionEmails()
        {
            using var context = CreateContext();
            context.ChargePoints.Add(new ChargePoint { ChargePointId = "CP1", Name = "Station A" });
            context.SaveChanges();
            var reservation = AddChargingReservation(context, FakeProvider, "fake_tx_r1_completion");
            reservation.InvoiceBuyerEmail = "r1-completion@example.com";
            context.SaveChanges();
            var transaction = AddStoppedTransaction(context, reservation, meterStopKwh: 2.5);
            var fake = new FakePaymentGateway
            {
                AuthorizationResponse = Authorization(reservation.ReservationId, "fake_tx_r1_completion", PaymentAuthorizationState.RequiresCapture, 450)
            };
            var email = new FakeEmailNotificationService();
            var coordinator = CreateCoordinator(fake, emailService: email);

            Assert.Equal("r1-completion@example.com", coordinator.GetCheckoutDetails(reservation).CustomerEmail);

            coordinator.CompleteReservation(context, transaction);

            Assert.Equal(PaymentReservationStatus.Completed, reservation.Status);
            Assert.Equal(1, email.ChargingCompletedCount);
            Assert.Equal("r1-completion@example.com", email.LastToEmail);
        }

        // ---- Charge tag claim when the browser return and a notification race ----

        [Fact]
        public async Task ConfirmAndNotification_RacingOnSqlite_EndWithOnePersistedChargeTag()
        {
            using var keepAlive = new SqliteConnection($"Data Source=claim-race-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
            keepAlive.Open();
            var options = new DbContextOptionsBuilder<OCPPCoreContext>()
                .UseSqlite(keepAlive.ConnectionString)
                .Options;
            using var browserContext = new OCPPCoreContext(options);
            browserContext.Database.EnsureCreated();
            var reservation = AddPendingReservation(browserContext, FakeProvider, "fake_token_race");
            using var notifyContext = new OCPPCoreContext(options);
            // The notification request loaded the reservation before the browser return saved it.
            var staleCopy = notifyContext.ChargePaymentReservations.Find(reservation.ReservationId)!;
            Assert.Null(staleCopy.OcppIdTag);
            var fake = new FakePaymentGateway
            {
                CheckoutStatus = CompletedCheckout(reservation.ReservationId, "fake_token_race", "fake_tx_race", inlineAuthorization: true, amountMinor: 450),
                ParseResult = Notification(reservation.ReservationId, eventId: null)
            };
            var coordinator = CreateCoordinator(fake);

            var confirmation = coordinator.ConfirmReservation(browserContext, reservation.ReservationId, null);
            var notification = await coordinator.HandleWebhookAsync(notifyContext, NotifyRequest(), CancellationToken.None);

            Assert.True(confirmation.Success);
            Assert.Equal(200, notification.StatusCode);
            Assert.False(string.IsNullOrWhiteSpace(reservation.OcppIdTag));
            Assert.Equal(reservation.OcppIdTag, staleCopy.OcppIdTag);
            using var verification = new OCPPCoreContext(options);
            var persisted = verification.ChargePaymentReservations.Single();
            Assert.Equal(PaymentReservationStatus.Authorized, persisted.Status);
            Assert.Equal(reservation.OcppIdTag, persisted.OcppIdTag);
            Assert.Equal(reservation.OcppIdTag, verification.ChargeTags.Single().TagId);
        }

        [Fact]
        public void Confirm_ToleratesAConcurrentInsertOfTheSameChargeTag()
        {
            using var keepAlive = new SqliteConnection($"Data Source=claim-tag-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
            keepAlive.Open();
            var options = new DbContextOptionsBuilder<OCPPCoreContext>()
                .UseSqlite(keepAlive.ConnectionString)
                .Options;
            var interceptor = new ConcurrentChargeTagInsertInterceptor(options);
            using var context = new OCPPCoreContext(new DbContextOptionsBuilder<OCPPCoreContext>(options).AddInterceptors(interceptor).Options);
            context.Database.EnsureCreated();
            var reservation = AddPendingReservation(context, FakeProvider, "fake_token_tag_race");
            var fake = new FakePaymentGateway
            {
                CheckoutStatus = CompletedCheckout(reservation.ReservationId, "fake_token_tag_race", "fake_tx_tag_race", inlineAuthorization: true, amountMinor: 450)
            };
            var coordinator = CreateCoordinator(fake);
            interceptor.Armed = true;

            var confirmation = coordinator.ConfirmReservation(context, reservation.ReservationId, null);

            Assert.True(interceptor.Inserted);
            Assert.True(confirmation.Success);
            using var verification = new OCPPCoreContext(options);
            var persisted = verification.ChargePaymentReservations.Single();
            Assert.Equal(PaymentReservationStatus.Authorized, persisted.Status);
            Assert.Equal(persisted.OcppIdTag, verification.ChargeTags.Single().TagId);
        }

        // ---- helpers ----

        /// <summary>A reservation exactly as the stale-pending sweep leaves it: never authorized, token only, armed.</summary>
        private static ChargePaymentReservation AddSweptReservation(OCPPCoreContext context, string checkoutReference)
        {
            const string cleanupMessage = "Auto-cancelled: stale reservation (background sweep)";
            var reservation = new ChargePaymentReservation
            {
                ReservationId = Guid.NewGuid(),
                ChargePointId = "CP-SWEPT",
                ConnectorId = 1,
                ChargeTagId = "TAG1",
                PaymentProvider = FakeProvider,
                StripeCheckoutSessionId = checkoutReference,
                Status = PaymentReservationStatus.Abandoned,
                MaxAmountCents = 450,
                Currency = "eur",
                CreatedAtUtc = Now.AddMinutes(-20),
                UpdatedAtUtc = Now,
                LastError = cleanupMessage,
                FailureCode = "CleanupTimeout",
                FailureMessage = cleanupMessage,
                AuthorizationReleaseState = PaymentAuthorizationReleaseState.Pending
            };
            context.ChargePaymentReservations.Add(reservation);
            context.SaveChanges();
            return reservation;
        }

        private static PaymentAuthorizationSnapshot CapturedAuthorization(Guid reservationId, string authorizationReference, long amountMinor) =>
            new()
            {
                AuthorizationReference = authorizationReference,
                State = PaymentAuthorizationState.Captured,
                RawStatus = "CAPTURED",
                AmountMinor = amountMinor,
                AmountReceivedMinor = amountMinor,
                Currency = "EUR",
                IsCancelable = false,
                OwnerReservationId = reservationId
            };

        private sealed class ThrowOnReservationUpdateInterceptor : SaveChangesInterceptor
        {
            public bool Armed { get; set; }

            public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
            {
                if (Armed && eventData.Context!.ChangeTracker.Entries<ChargePaymentReservation>().Any(entry => entry.State == EntityState.Modified))
                {
                    throw new DbUpdateException("Simulated database failure.");
                }

                return base.SavingChanges(eventData, result);
            }
        }

        /// <summary>Inserts the charge tag being saved through another connection first, as a racing request would.</summary>
        private sealed class ConcurrentChargeTagInsertInterceptor : SaveChangesInterceptor
        {
            private readonly DbContextOptions<OCPPCoreContext> _options;

            public ConcurrentChargeTagInsertInterceptor(DbContextOptions<OCPPCoreContext> options)
            {
                _options = options;
            }

            public bool Armed { get; set; }
            public bool Inserted { get; private set; }

            public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
            {
                var added = eventData.Context!.ChangeTracker.Entries<ChargeTag>()
                    .FirstOrDefault(entry => entry.State == EntityState.Added);
                if (Armed && !Inserted && added != null)
                {
                    using var racing = new OCPPCoreContext(_options);
                    racing.ChargeTags.Add(new ChargeTag { TagId = added.Entity.TagId, TagName = added.Entity.TagName });
                    racing.SaveChanges();
                    Inserted = true;
                }

                return base.SavingChanges(eventData, result);
            }
        }
    }
}
