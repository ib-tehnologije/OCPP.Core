using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OCPP.Core.Database;
using OCPP.Core.Server.Payments;
using OCPP.Core.Server.Payments.Providers;
using Stripe;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    /// <summary>
    /// Provider-neutral coordinator flows against a Saferpay-like gateway (no success-URL placeholder,
    /// no metadata, no deferred checkout lookup), plus provider routing of existing reservations.
    /// </summary>
    public partial class PaymentCoordinatorProviderTests
    {
        private const string FakeProvider = "FakePay";
        private const string ReturnBaseUrl = "https://return.example.com";
        private static readonly DateTime Now = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void FakeGateway_CreateConfirmStartAndCapture_UsesActiveProviderEndToEnd()
        {
            using var context = CreateContext();
            context.ChargePoints.Add(new ChargePoint
            {
                ChargePointId = "CP-FAKE",
                MaxSessionKwh = 10,
                PricePerKwh = 0.40m,
                UserSessionFee = 0.50m
            });
            context.SaveChanges();
            var fake = new FakePaymentGateway();
            var coordinator = CreateCoordinator(fake);

            var created = coordinator.CreateCheckoutSession(context, new PaymentSessionRequest
            {
                ChargePointId = "CP-FAKE",
                ConnectorId = 1,
                ChargeTagId = "TAG-FAKE"
            });

            var reservation = created.Reservation;
            var reservationId = reservation.ReservationId;
            Assert.Equal(FakeProvider, reservation.PaymentProvider);
            Assert.Equal("fake_token_1", reservation.StripeCheckoutSessionId);
            Assert.Null(reservation.StripePaymentIntentId);
            Assert.Equal(450, reservation.MaxAmountCents);
            Assert.Equal(fake.CreateResponse.RedirectUrl, created.CheckoutUrl);
            var checkoutRequest = Assert.Single(fake.CheckoutRequests);
            Assert.Equal($"{ReturnBaseUrl}/Payments/Success?reservationId={reservationId}", checkoutRequest.SuccessUrl);
            Assert.Equal($"{ReturnBaseUrl}/Payments/Cancel?reservationId={reservationId}", checkoutRequest.CancelUrl);
            Assert.DoesNotContain("session_id", checkoutRequest.SuccessUrl);
            Assert.Equal(450, checkoutRequest.AmountMinor);
            Assert.Equal("eur", checkoutRequest.Currency);
            Assert.Equal($"checkout_create:{reservationId}", checkoutRequest.IdempotencyKey);
            Assert.Equal(reservationId, checkoutRequest.ReservationId);

            fake.CheckoutStatus = CompletedCheckout(reservationId, "fake_token_1", "fake_tx_1", inlineAuthorization: true, amountMinor: 450);
            var confirmation = coordinator.ConfirmReservation(context, reservationId, checkoutSessionId: null);

            Assert.True(confirmation.Success);
            Assert.Equal(PaymentReservationStatus.Authorized, confirmation.Status);
            Assert.Equal(PaymentReservationStatus.Authorized, reservation.Status);
            Assert.Equal("fake_tx_1", reservation.StripePaymentIntentId);
            Assert.Equal("payer@example.com", reservation.PayerEmail);
            Assert.Equal("Pat Payer", reservation.PayerName);
            Assert.Equal(Now.AddMinutes(7), reservation.StartDeadlineAtUtc);
            Assert.False(string.IsNullOrWhiteSpace(reservation.OcppIdTag));
            Assert.Contains("GetCheckout:fake_token_1", fake.Calls);
            Assert.DoesNotContain(fake.Calls, call => call.StartsWith("GetAuthorization", StringComparison.Ordinal));

            var transaction = new Transaction
            {
                TransactionId = 77,
                ChargePointId = "CP-FAKE",
                ConnectorId = 1,
                StartTagId = reservation.OcppIdTag,
                StartTime = Now.AddMinutes(1),
                MeterStart = 0
            };
            context.Transactions.Add(transaction);
            context.SaveChanges();

            coordinator.MarkTransactionStarted(context, "CP-FAKE", 1, reservation.OcppIdTag, 77);
            Assert.Equal(PaymentReservationStatus.Charging, reservation.Status);
            Assert.Equal(77, reservation.TransactionId);

            transaction.MeterStop = 2.5;
            transaction.StopTime = Now.AddMinutes(30);
            transaction.StopReason = "EVDisconnected";
            context.SaveChanges();
            fake.AuthorizationResponse = Authorization(reservationId, "fake_tx_1", PaymentAuthorizationState.RequiresCapture, 450);

            coordinator.CompleteReservation(context, transaction);

            // 2.5 kWh * 0.40 = 1.00 EUR energy + 0.50 EUR session fee = 150 minor units.
            Assert.Equal(PaymentReservationStatus.Completed, reservation.Status);
            var capture = Assert.Single(fake.Captures);
            Assert.Equal("fake_tx_1", capture.Reference);
            Assert.Equal(150, capture.AmountMinor);
            Assert.Equal("eur", capture.Currency);
            Assert.Equal($"capture:{reservationId}:150", capture.IdempotencyKey);
            Assert.Equal(150, reservation.CapturedAmountCents);
            Assert.Empty(fake.Cancels);
        }

        [Fact]
        public void FakeGateway_ZeroAmountSettlementCancelsAuthorization()
        {
            using var context = CreateContext();
            var reservation = AddChargingReservation(context, FakeProvider, "fake_tx_zero");
            var transaction = AddStoppedTransaction(context, reservation, meterStopKwh: 0);
            var fake = new FakePaymentGateway
            {
                AuthorizationResponse = Authorization(reservation.ReservationId, "fake_tx_zero", PaymentAuthorizationState.RequiresCapture, 450)
            };
            var coordinator = CreateCoordinator(fake);

            coordinator.CompleteReservation(context, transaction);

            Assert.Equal(PaymentReservationStatus.Cancelled, reservation.Status);
            Assert.Empty(fake.Captures);
            var cancel = Assert.Single(fake.Cancels);
            Assert.Equal("fake_tx_zero", cancel.Reference);
            Assert.Equal($"cancel:{reservation.ReservationId}", cancel.IdempotencyKey);
            Assert.Equal(0, reservation.CapturedAmountCents);
        }

        [Fact]
        public void FakeGateway_ConfirmCanceledCheckoutCancelsPendingReservation()
        {
            using var context = CreateContext();
            var reservation = AddPendingReservation(context, FakeProvider, "fake_token_cancel");
            var fake = new FakePaymentGateway
            {
                CheckoutStatus = new PaymentCheckoutStatus
                {
                    SessionReference = "fake_token_cancel",
                    State = PaymentCheckoutState.Canceled,
                    RawStatus = "ABORTED",
                    OwnerReservationId = reservation.ReservationId
                }
            };
            var coordinator = CreateCoordinator(fake);

            var result = coordinator.ConfirmReservation(context, reservation.ReservationId, null);

            Assert.False(result.Success);
            Assert.Equal(PaymentReservationStatus.Cancelled, result.Status);
            Assert.Equal(PaymentReservationStatus.Cancelled, reservation.Status);
            Assert.Equal("checkout_cancelled", reservation.LastError);
            Assert.Equal(Now, reservation.UpdatedAtUtc);
            Assert.Empty(fake.Cancels);
        }

        [Fact]
        public void FakeGateway_ConfirmFailedCheckoutFailsReservationWithSanitizedMessage()
        {
            using var context = CreateContext();
            var reservation = AddPendingReservation(context, FakeProvider, "fake_token_fail");
            var fake = new FakePaymentGateway
            {
                CheckoutStatus = new PaymentCheckoutStatus
                {
                    SessionReference = "fake_token_fail",
                    State = PaymentCheckoutState.Failed,
                    RawStatus = "FAILED",
                    FailureCode = "3DS_AUTHENTICATION_FAILED",
                    FailureMessage = "Card declined for payer@example.com",
                    OwnerReservationId = reservation.ReservationId
                }
            };
            var coordinator = CreateCoordinator(fake);

            var result = coordinator.ConfirmReservation(context, reservation.ReservationId, "");

            Assert.False(result.Success);
            Assert.Equal(PaymentReservationStatus.Failed, result.Status);
            Assert.Equal(PaymentReservationStatus.Failed, reservation.Status);
            Assert.Equal("3DS_AUTHENTICATION_FAILED", reservation.FailureCode);
            Assert.DoesNotContain("payer@example.com", reservation.LastError);
            Assert.Contains("[redacted-email]", reservation.LastError);
            Assert.Equal(reservation.LastError, reservation.FailureMessage);
            Assert.Equal(reservation.LastError, result.Error);
        }

        [Fact]
        public void FakeGateway_ConfirmTransientProviderFailureKeepsReservationPending()
        {
            using var context = CreateContext();
            var reservation = AddPendingReservation(context, FakeProvider, "fake_token_transient");
            var fake = new FakePaymentGateway
            {
                GetCheckoutException = new PaymentProviderException(FakeProvider, "timeout", "Provider timeout", isTransient: true, httpStatusCode: 504)
            };
            var coordinator = CreateCoordinator(fake);

            var result = coordinator.ConfirmReservation(context, reservation.ReservationId, null);

            Assert.False(result.Success);
            Assert.Equal($"{FakeProvider}Error", result.Status);
            Assert.Equal(PaymentReservationStatus.Pending, reservation.Status);
            Assert.Null(reservation.AuthorizationReleaseState);
        }

        [Fact]
        public void FakeGateway_ConfirmPermanentProviderFailureFailsReservationAndArmsRelease()
        {
            using var context = CreateContext();
            var reservation = AddPendingReservation(context, FakeProvider, "fake_token_permanent");
            var fake = new FakePaymentGateway
            {
                GetCheckoutException = new PaymentProviderException(FakeProvider, "PERMISSION_DENIED", "Permission denied", isTransient: false, httpStatusCode: 403)
            };
            var coordinator = CreateCoordinator(fake);

            var result = coordinator.ConfirmReservation(context, reservation.ReservationId, null);

            Assert.False(result.Success);
            Assert.Equal($"{FakeProvider}Error", result.Status);
            Assert.Equal(PaymentReservationStatus.Failed, reservation.Status);
            Assert.Equal(PaymentAuthorizationReleaseState.Pending, reservation.AuthorizationReleaseState);
            Assert.Null(reservation.AuthorizationReleaseNextAttemptAtUtc);
        }

        [Fact]
        public void FakeGateway_ConfirmRejectsSuppliedMismatchingCheckoutReference()
        {
            using var context = CreateContext();
            var reservation = AddPendingReservation(context, FakeProvider, "fake_token_owned");
            var fake = new FakePaymentGateway();
            var coordinator = CreateCoordinator(fake);

            var result = coordinator.ConfirmReservation(context, reservation.ReservationId, "fake_token_other");

            Assert.False(result.Success);
            Assert.Equal("SessionMismatch", result.Status);
            Assert.Equal(PaymentReservationStatus.Pending, reservation.Status);
            Assert.Empty(fake.Calls);
        }

        [Fact]
        public void FakeGateway_ResumeIsUnavailableWithoutResumeUrl()
        {
            using var context = CreateContext();
            var reservation = AddPendingReservation(context, FakeProvider, "fake_token_resume");
            var fake = new FakePaymentGateway
            {
                CheckoutStatus = new PaymentCheckoutStatus
                {
                    SessionReference = "fake_token_resume",
                    State = PaymentCheckoutState.Open,
                    RawStatus = "PENDING"
                }
            };
            var coordinator = CreateCoordinator(fake);

            var result = coordinator.ResumeReservation(context, reservation.ReservationId);

            Assert.False(result.Success);
            Assert.Equal("ResumeUnavailable", result.Status);
            Assert.Null(result.CheckoutUrl);
        }

        [Fact]
        public void StripeReservation_IsCapturedThroughStripeGateway_WhenAnotherProviderIsActive()
        {
            using var context = CreateContext();
            var reservation = AddChargingReservation(context, PaymentProviderNames.Stripe, "pi_legacy_stripe");
            var transaction = AddStoppedTransaction(context, reservation, meterStopKwh: 2.5);
            var fake = new FakePaymentGateway();
            var stripeIntents = new FakePaymentIntentService
            {
                GetResponse = new PaymentIntent { Id = "pi_legacy_stripe", Status = "requires_capture", Amount = 10_000 }
            };
            var coordinator = CreateCoordinator(fake, stripeIntents: stripeIntents);

            Assert.True(coordinator.IsEnabled);
            coordinator.CompleteReservation(context, transaction);

            Assert.Equal(PaymentReservationStatus.Completed, reservation.Status);
            Assert.True(stripeIntents.CaptureCalled);
            Assert.Equal(150, stripeIntents.LastCaptureOptions?.AmountToCapture);
            Assert.Equal($"capture:{reservation.ReservationId}:150", stripeIntents.LastCaptureRequestOptions?.IdempotencyKey);
            Assert.Empty(fake.Calls);
        }

        [Fact]
        public void StripeReservation_IsCancelledThroughStripeGateway_WhenAnotherProviderIsActive()
        {
            using var context = CreateContext();
            var reservation = AddChargingReservation(context, PaymentProviderNames.Stripe, "pi_legacy_cancel");
            reservation.Status = PaymentReservationStatus.Authorized;
            context.SaveChanges();
            var fake = new FakePaymentGateway();
            var stripeIntents = new FakePaymentIntentService
            {
                GetResponse = new PaymentIntent { Id = "pi_legacy_cancel", Status = "requires_capture", Amount = 450 }
            };
            var coordinator = CreateCoordinator(fake, stripeIntents: stripeIntents);

            coordinator.CancelPaymentIntentIfCancelable(context, reservation, "test");

            Assert.True(stripeIntents.CancelCalled);
            Assert.Equal($"cancel:{reservation.ReservationId}", stripeIntents.LastCancelRequestOptions?.IdempotencyKey);
            Assert.Empty(fake.Calls);
        }

        [Fact]
        public void UnknownProviderReservation_FailsClosedWithoutCallingAnyGateway()
        {
            using var context = CreateContext();
            var charging = AddChargingReservation(context, "RetiredPay", "retired_psp_ref");
            var transaction = AddStoppedTransaction(context, charging, meterStopKwh: 2.5);
            var pending = AddPendingReservation(context, "RetiredPay", "retired_session");
            pending.StripePaymentIntentId = "retired_pending_auth";
            context.SaveChanges();
            var abandoned = AddReleaseReservation(context, "RetiredPay", authorizationReference: "retired_auth");
            var fake = new FakePaymentGateway();
            var stripeIntents = new ReleasePaymentIntentService();
            var stripeSessions = new ReleaseSessionService();
            var coordinator = CreateCoordinator(fake, stripeSessions: stripeSessions, stripeIntents: stripeIntents);

            coordinator.CompleteReservation(context, transaction);
            var confirmation = coordinator.ConfirmReservation(context, pending.ReservationId, "retired_session");
            var resume = coordinator.ResumeReservation(context, pending.ReservationId);
            var release = coordinator.ReconcileTerminalPaymentAuthorization(context, abandoned, PaymentAuthorizationReleaseTrigger.CleanupSweep);
            coordinator.CancelPaymentIntentIfCancelable(context, pending, "test");

            Assert.Equal(PaymentReservationStatus.Failed, charging.Status);
            Assert.Contains("provider_not_registered", charging.LastError);
            Assert.Null(charging.CapturedAmountCents);
            Assert.Equal("ProviderUnavailable", confirmation.Status);
            Assert.False(confirmation.Success);
            Assert.Equal("ProviderUnavailable", resume.Status);
            Assert.Equal(PaymentReservationStatus.Pending, pending.Status);
            Assert.Contains("not registered", pending.LastError);
            Assert.Equal(PaymentAuthorizationReleaseOutcome.PermanentFailure, release.Outcome);
            Assert.Equal(PaymentAuthorizationReleaseState.PermanentFailure, abandoned.AuthorizationReleaseState);
            Assert.Equal("provider_not_registered", context.PaymentAuthorizationReleaseAttempts.Single().ErrorCode);
            Assert.Empty(fake.Calls);
            Assert.Equal(0, stripeIntents.GetCalls);
            Assert.Equal(0, stripeIntents.CancelCalls);
            Assert.Equal(0, stripeSessions.GetCalls);
        }

        [Fact]
        public async Task CheckoutNotification_AuthorizesPendingReservationAndReportsIt()
        {
            using var context = CreateContext();
            var reservation = AddPendingReservation(context, FakeProvider, "fake_token_notify");
            var fake = new FakePaymentGateway
            {
                CheckoutStatus = CompletedCheckout(reservation.ReservationId, "fake_token_notify", "fake_tx_notify", inlineAuthorization: false, amountMinor: 450),
                AuthorizationResponse = Authorization(reservation.ReservationId, "fake_tx_notify", PaymentAuthorizationState.RequiresCapture, 450),
                ParseResult = Notification(reservation.ReservationId, "notify-1")
            };
            var coordinator = CreateCoordinator(fake);

            var result = await coordinator.HandleWebhookAsync(context, NotifyRequest(), CancellationToken.None);

            Assert.Equal(200, result.StatusCode);
            Assert.Equal(FakeProvider, result.Provider);
            Assert.Equal(new[] { reservation.ReservationId }, result.AuthorizedReservationIds);
            Assert.Equal(PaymentReservationStatus.Authorized, reservation.Status);
            Assert.Equal("fake_tx_notify", reservation.StripePaymentIntentId);
            Assert.Equal(Now, reservation.AuthorizedAtUtc);
            Assert.Equal(Now.AddMinutes(7), reservation.StartDeadlineAtUtc);
            Assert.Equal("payer@example.com", reservation.PayerEmail);
            Assert.False(string.IsNullOrWhiteSpace(reservation.OcppIdTag));
            Assert.Contains("GetAuthorization:fake_tx_notify", fake.Calls);
            var processed = Assert.Single(context.StripeWebhookEvents);
            Assert.Equal("FakePay:notify-1", processed.EventId);
            Assert.Equal(reservation.ReservationId, processed.ReservationId);
        }

        [Fact]
        public async Task CheckoutNotification_DuplicateDeliveryIsIdempotent()
        {
            using var context = CreateContext();
            var reservation = AddPendingReservation(context, FakeProvider, "fake_token_dup");
            var fake = new FakePaymentGateway
            {
                CheckoutStatus = CompletedCheckout(reservation.ReservationId, "fake_token_dup", "fake_tx_dup", inlineAuthorization: true, amountMinor: 450),
                ParseResult = Notification(reservation.ReservationId, "notify-dup")
            };
            var clock = Now;
            var coordinator = CreateCoordinator(fake, now: () => clock);

            await coordinator.HandleWebhookAsync(context, NotifyRequest(), CancellationToken.None);
            clock = Now.AddMinutes(3);
            var duplicate = await coordinator.HandleWebhookAsync(context, NotifyRequest(), CancellationToken.None);

            Assert.Equal(200, duplicate.StatusCode);
            Assert.Empty(duplicate.AuthorizedReservationIds);
            Assert.Equal(1, fake.Calls.Count(call => call.StartsWith("GetCheckout", StringComparison.Ordinal)));
            Assert.Single(context.StripeWebhookEvents);
            Assert.Equal(Now, reservation.AuthorizedAtUtc);

            // A notification without a provider event id is re-evaluated, and stays a no-op once authorized.
            fake.ParseResult = Notification(reservation.ReservationId, eventId: null);
            var anonymous = await coordinator.HandleWebhookAsync(context, NotifyRequest(), CancellationToken.None);

            Assert.Equal(200, anonymous.StatusCode);
            Assert.Equal(new[] { reservation.ReservationId }, anonymous.AuthorizedReservationIds);
            Assert.Equal(PaymentReservationStatus.Authorized, reservation.Status);
            Assert.Equal(Now, reservation.AuthorizedAtUtc);
            Assert.Single(context.StripeWebhookEvents);
        }

        [Fact]
        public async Task CheckoutNotification_TransientProviderFailureAsksForRetry()
        {
            using var context = CreateContext();
            var reservation = AddPendingReservation(context, FakeProvider, "fake_token_retry");
            var fake = new FakePaymentGateway
            {
                GetCheckoutException = new PaymentProviderException(FakeProvider, "timeout", "Provider timeout", isTransient: true, httpStatusCode: 504),
                ParseResult = Notification(reservation.ReservationId, "notify-retry")
            };
            var coordinator = CreateCoordinator(fake);

            var transient = await coordinator.HandleWebhookAsync(context, NotifyRequest(), CancellationToken.None);

            Assert.Equal(503, transient.StatusCode);
            Assert.Empty(transient.AuthorizedReservationIds);
            Assert.Equal(PaymentReservationStatus.Pending, reservation.Status);
            Assert.Empty(context.StripeWebhookEvents);

            fake.GetCheckoutException = new PaymentProviderException(FakeProvider, "VALIDATION_FAILED", "Bad request", isTransient: false, httpStatusCode: 400);
            var permanent = await coordinator.HandleWebhookAsync(context, NotifyRequest(), CancellationToken.None);

            Assert.Equal(200, permanent.StatusCode);
            Assert.Equal(PaymentReservationStatus.Pending, reservation.Status);
            Assert.Empty(context.StripeWebhookEvents);
        }

        [Fact]
        public async Task CheckoutNotification_LateAuthorizationOfCancelledReservationIsReleased()
        {
            using var context = CreateContext();
            var reservation = AddPendingReservation(context, FakeProvider, "fake_token_late");
            reservation.Status = PaymentReservationStatus.Cancelled;
            reservation.LastError = "checkout_cancelled";
            context.SaveChanges();
            var fake = new FakePaymentGateway
            {
                CheckoutStatus = CompletedCheckout(reservation.ReservationId, "fake_token_late", "fake_tx_late", inlineAuthorization: true, amountMinor: 450),
                ParseResult = Notification(reservation.ReservationId, eventId: null)
            };
            var coordinator = CreateCoordinator(fake);

            var result = await coordinator.HandleWebhookAsync(context, NotifyRequest(), CancellationToken.None);

            Assert.Equal(200, result.StatusCode);
            Assert.Empty(result.AuthorizedReservationIds);
            Assert.Equal(PaymentReservationStatus.Cancelled, reservation.Status);
            Assert.Equal("fake_tx_late", reservation.StripePaymentIntentId);
            Assert.Equal("late_authorization_released", reservation.LastError);
            var cancel = Assert.Single(fake.Cancels);
            Assert.Equal("fake_tx_late", cancel.Reference);
            Assert.Equal($"cancel:{reservation.ReservationId}", cancel.IdempotencyKey);
            Assert.DoesNotContain(fake.Calls, call => call.StartsWith("Capture", StringComparison.Ordinal));

            // A repeated notification now sees the cancelled authorization and does nothing.
            fake.CheckoutStatus.Authorization = Authorization(reservation.ReservationId, "fake_tx_late", PaymentAuthorizationState.Canceled, 450);
            var repeated = await coordinator.HandleWebhookAsync(context, NotifyRequest(), CancellationToken.None);

            Assert.Equal(200, repeated.StatusCode);
            Assert.Single(fake.Cancels);
            Assert.Equal(PaymentReservationStatus.Cancelled, reservation.Status);
        }

        [Fact]
        public async Task CheckoutNotification_TransientFailureReleasingLateAuthorizationAsksForRetry()
        {
            using var context = CreateContext();
            var reservation = AddPendingReservation(context, FakeProvider, "fake_token_late_retry");
            reservation.Status = PaymentReservationStatus.Cancelled;
            context.SaveChanges();
            var fake = new FakePaymentGateway
            {
                CheckoutStatus = CompletedCheckout(reservation.ReservationId, "fake_token_late_retry", "fake_tx_late_retry", inlineAuthorization: true, amountMinor: 450),
                ParseResult = Notification(reservation.ReservationId, eventId: null),
                CancelException = new PaymentProviderException(FakeProvider, "timeout", "Provider timeout", isTransient: true, httpStatusCode: 504)
            };
            var coordinator = CreateCoordinator(fake);

            var transient = await coordinator.HandleWebhookAsync(context, NotifyRequest(), CancellationToken.None);

            Assert.Equal(503, transient.StatusCode);
            Assert.Equal(PaymentReservationStatus.Cancelled, reservation.Status);

            fake.CancelException = new PaymentProviderException(FakeProvider, "ACTION_NOT_SUPPORTED", "Cancel is not supported for this transaction", isTransient: false, httpStatusCode: 400);
            var permanent = await coordinator.HandleWebhookAsync(context, NotifyRequest(), CancellationToken.None);

            Assert.Equal(200, permanent.StatusCode);
            Assert.Equal(PaymentReservationStatus.Cancelled, reservation.Status);
            Assert.Equal("Cancel is not supported for this transaction", reservation.LastError);

            fake.CancelException = null;
            var retried = await coordinator.HandleWebhookAsync(context, NotifyRequest(), CancellationToken.None);

            Assert.Equal(200, retried.StatusCode);
            Assert.Equal("late_authorization_released", reservation.LastError);
            Assert.Single(fake.Cancels);
        }

        [Fact]
        public async Task CheckoutNotification_DoesNotReleaseAuthorizationOfActiveOrCompletedReservations()
        {
            using var context = CreateContext();
            var authorized = AddPendingReservation(context, FakeProvider, "fake_token_active");
            authorized.Status = PaymentReservationStatus.Authorized;
            authorized.StripePaymentIntentId = "fake_tx_active";
            var completed = AddPendingReservation(context, FakeProvider, "fake_token_done");
            completed.ChargePointId = "CP2";
            completed.Status = PaymentReservationStatus.Completed;
            completed.StripePaymentIntentId = "fake_tx_done";
            completed.CapturedAmountCents = 300;
            context.SaveChanges();
            var fake = new FakePaymentGateway
            {
                CheckoutStatus = CompletedCheckout(authorized.ReservationId, "fake_token_active", "fake_tx_active", inlineAuthorization: true, amountMinor: 450),
                ParseResult = Notification(authorized.ReservationId, eventId: null)
            };
            var coordinator = CreateCoordinator(fake);

            var activeResult = await coordinator.HandleWebhookAsync(context, NotifyRequest(), CancellationToken.None);

            fake.CheckoutStatus = CompletedCheckout(completed.ReservationId, "fake_token_done", "fake_tx_done", inlineAuthorization: true, amountMinor: 450);
            fake.CheckoutStatus.Authorization = Authorization(completed.ReservationId, "fake_tx_done", PaymentAuthorizationState.Captured, 450);
            fake.ParseResult = Notification(completed.ReservationId, eventId: null);
            var completedResult = await coordinator.HandleWebhookAsync(context, NotifyRequest(), CancellationToken.None);

            Assert.Equal(200, activeResult.StatusCode);
            Assert.Equal(200, completedResult.StatusCode);
            Assert.Empty(fake.Cancels);
            Assert.Equal(PaymentReservationStatus.Authorized, authorized.Status);
            Assert.Equal(PaymentReservationStatus.Completed, completed.Status);
        }

        [Fact]
        public async Task Webhook_RoutesByProviderAndPassesRejectionStatus()
        {
            using var context = CreateContext();
            var stripeOwned = AddPendingReservation(context, PaymentProviderNames.Stripe, "cs_stripe_owned");
            var fake = new FakePaymentGateway
            {
                ParseResult = PaymentWebhookParseResult.Rejected("signature_invalid", 403)
            };
            var coordinator = CreateCoordinator(fake);

            var unknown = await coordinator.HandleWebhookAsync(context, new PaymentWebhookRequest { Provider = "RetiredPay" }, CancellationToken.None);
            var rejected = await coordinator.HandleWebhookAsync(context, NotifyRequest(), CancellationToken.None);
            fake.ParseResult = Notification(stripeOwned.ReservationId, "notify-foreign");
            var foreign = await coordinator.HandleWebhookAsync(context, NotifyRequest(), CancellationToken.None);

            Assert.Equal(404, unknown.StatusCode);
            Assert.Equal(403, rejected.StatusCode);
            Assert.Equal(FakeProvider, rejected.Provider);
            Assert.Equal(200, foreign.StatusCode);
            Assert.Empty(foreign.AuthorizedReservationIds);
            Assert.Equal(PaymentReservationStatus.Pending, stripeOwned.Status);
            Assert.DoesNotContain(fake.Calls, call => call.StartsWith("GetCheckout", StringComparison.Ordinal));
        }

        [Fact]
        public void Release_LinksAuthorizationThroughCheckoutAndCancelsIt()
        {
            using var context = CreateContext();
            var reservation = AddReleaseReservation(context, FakeProvider, authorizationReference: null, checkoutReference: "fake_token_release");
            var fake = new FakePaymentGateway
            {
                CheckoutStatus = CompletedCheckout(reservation.ReservationId, "fake_token_release", "fake_tx_release", inlineAuthorization: false, amountMinor: 500),
                AuthorizationResponse = Authorization(reservation.ReservationId, "fake_tx_release", PaymentAuthorizationState.RequiresCapture, 500)
            };
            var coordinator = CreateCoordinator(fake);

            var result = coordinator.ReconcileTerminalPaymentAuthorization(context, reservation, PaymentAuthorizationReleaseTrigger.CleanupSweep);

            Assert.Equal(PaymentAuthorizationReleaseOutcome.Released, result.Outcome);
            Assert.Equal(PaymentAuthorizationReleaseState.Released, reservation.AuthorizationReleaseState);
            Assert.Equal("fake_tx_release", reservation.StripePaymentIntentId);
            var cancel = Assert.Single(fake.Cancels);
            Assert.Equal("fake_tx_release", cancel.Reference);
            Assert.Equal($"authorization_release:{reservation.ReservationId}:1", cancel.IdempotencyKey);
            var attempt = context.PaymentAuthorizationReleaseAttempts.Single();
            Assert.Equal("fake_tx_release", attempt.StripePaymentIntentId);
            Assert.Equal(500, attempt.AmountCapturableCents);
        }

        [Fact]
        public void Release_CanceledCheckoutWithoutAuthorizationIsAlreadyReleased()
        {
            using var context = CreateContext();
            var reservation = AddReleaseReservation(context, FakeProvider, authorizationReference: null, checkoutReference: "fake_token_abort");
            var fake = new FakePaymentGateway
            {
                CheckoutStatus = new PaymentCheckoutStatus
                {
                    SessionReference = "fake_token_abort",
                    State = PaymentCheckoutState.Canceled,
                    RawStatus = "ABORTED",
                    OwnerReservationId = reservation.ReservationId
                }
            };
            var coordinator = CreateCoordinator(fake);

            var result = coordinator.ReconcileTerminalPaymentAuthorization(context, reservation, PaymentAuthorizationReleaseTrigger.CleanupSweep);

            Assert.Equal(PaymentAuthorizationReleaseOutcome.AlreadyReleased, result.Outcome);
            Assert.Equal("ABORTED", result.ProviderStatus);
            Assert.Equal(PaymentAuthorizationReleaseState.Released, reservation.AuthorizationReleaseState);
            Assert.Null(reservation.StripePaymentIntentId);
            Assert.Empty(fake.Cancels);
            Assert.DoesNotContain(fake.Calls, call => call.StartsWith("GetAuthorization", StringComparison.Ordinal));
        }

        [Fact]
        public void Release_OpenCheckoutWithoutAuthorizationKeepsLinkagePendingRetry()
        {
            using var context = CreateContext();
            var reservation = AddReleaseReservation(context, FakeProvider, authorizationReference: null, checkoutReference: "fake_token_open");
            var fake = new FakePaymentGateway
            {
                CheckoutStatus = new PaymentCheckoutStatus
                {
                    SessionReference = "fake_token_open",
                    State = PaymentCheckoutState.Open,
                    RawStatus = "PENDING",
                    OwnerReservationId = reservation.ReservationId
                }
            };
            var coordinator = CreateCoordinator(fake);

            var result = coordinator.ReconcileTerminalPaymentAuthorization(context, reservation, PaymentAuthorizationReleaseTrigger.CleanupSweep);

            Assert.Equal(PaymentAuthorizationReleaseOutcome.RetryScheduled, result.Outcome);
            Assert.Equal("payment_intent_linkage_pending", context.PaymentAuthorizationReleaseAttempts.Single().ErrorCode);
            Assert.Empty(fake.Cancels);
        }

        [Fact]
        public void RequestR1Invoice_WithoutProviderMetadataKeepsDatabaseSnapshotAuthoritative()
        {
            using var context = CreateContext();
            context.ChargePoints.Add(new ChargePoint { ChargePointId = "CP1", Name = "Station A" });
            var reservation = AddPendingReservation(context, FakeProvider, "fake_token_r1");
            reservation.Status = PaymentReservationStatus.Authorized;
            reservation.PayerEmail = "billing@example.com";
            context.SaveChanges();
            var fake = new FakePaymentGateway();
            var jobs = new FakeBackgroundJobClient();
            var email = new FakeEmailNotificationService();
            var coordinator = CreateCoordinator(fake, backgroundJobClient: jobs, emailService: email);

            var result = coordinator.RequestR1Invoice(context, new PaymentR1InvoiceRequest
            {
                ReservationId = reservation.ReservationId,
                BuyerCompanyName = "Acme d.o.o.",
                BuyerOib = "12345678903",
                BuyerCountry = "HR",
                BuyerDataConfirmed = true
            });

            Assert.True(result.Success);
            Assert.Equal("Updated", result.Status);
            Assert.Equal("Acme d.o.o.", reservation.InvoiceBuyerCompanyName);
            Assert.True(reservation.InvoiceBuyerConfirmedAtUtc.HasValue);
            Assert.Null(jobs.CreatedJob);
            Assert.Empty(fake.Calls);
            Assert.Equal(1, email.R1InvoiceRequestedCount);
            Assert.Equal("billing@example.com", email.LastToEmail);
            Assert.True(coordinator.ReconcileR1BuyerMetadata(context, reservation.ReservationId));
            Assert.Empty(fake.Calls);
            Assert.Equal(1, email.R1InvoiceRequestedCount);
        }

        [Fact]
        public async Task IdleWarning_UsesPersistedPayerEmailForProvidersWithoutDeferredLookup()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Maintenance:IdleWarningSweepSeconds"] = "30"
                })
                .Build();
            services.AddSingleton<IConfiguration>(configuration);
            var databaseName = Guid.NewGuid().ToString();
            services.AddDbContext<OCPPCoreContext>(options => options.UseInMemoryDatabase(databaseName));
            var email = new FakeEmailNotificationService();
            services.AddSingleton<IEmailNotificationService>(email);
            var fake = new FakePaymentGateway();
            var coordinator = CreateCoordinator(fake);
            services.AddSingleton<IPaymentCoordinator>(coordinator);
            using var provider = services.BuildServiceProvider();

            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OCPPCoreContext>();
                db.ChargePaymentReservations.Add(new ChargePaymentReservation
                {
                    ReservationId = Guid.NewGuid(),
                    ChargePointId = "CP1",
                    ConnectorId = 1,
                    ChargeTagId = "TAG1",
                    PaymentProvider = FakeProvider,
                    StripeCheckoutSessionId = "fake_token_idle",
                    StripePaymentIntentId = "fake_tx_idle",
                    PayerEmail = "idle-payer@example.com",
                    Status = PaymentReservationStatus.Charging,
                    UsageFeeAnchorMinutes = 1,
                    UsageFeePerMinute = 0.20m,
                    StartUsageFeeAfterMinutes = 20,
                    MaxUsageFeeMinutes = 120,
                    Currency = "eur",
                    TransactionId = 993,
                    CreatedAtUtc = Now.AddMinutes(-30),
                    UpdatedAtUtc = Now.AddMinutes(-5)
                });
                db.Transactions.Add(new Transaction
                {
                    TransactionId = 993,
                    ChargePointId = "CP1",
                    ConnectorId = 1,
                    StartTagId = "TAG1",
                    StartTime = Now.AddHours(-1),
                    ChargingEndedAtUtc = Now.AddMinutes(-10)
                });
                db.SaveChanges();
            }

            var service = new IdleWarningHarness(
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<ILogger<IdleFeeWarningEmailService>>(),
                configuration,
                Options.Create(new NotificationOptions { EnableCustomerEmails = true, IdleWarningLeadMinutes = 15 }),
                Settings(),
                () => Now);

            await service.RunOnce();

            Assert.Equal(1, email.IdleFeeWarningCount);
            Assert.Equal("idle-payer@example.com", email.LastToEmail);
            Assert.StartsWith($"{ReturnBaseUrl}/Payments/Status?reservationId=", email.LastStatusUrl);
            Assert.Empty(fake.Calls);
        }

        private static OCPPCoreContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<OCPPCoreContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            return new OCPPCoreContext(options);
        }

        private static PaymentCheckoutSettings Settings() => new()
        {
            DefaultProvider = FakeProvider,
            ReturnBaseUrl = ReturnBaseUrl,
            Currency = "eur",
            ProductName = "Charging session"
        };

        internal static PaymentCoordinator CreateCoordinator(
            FakePaymentGateway fake,
            IStripeSessionService? stripeSessions = null,
            IStripePaymentIntentService? stripeIntents = null,
            Func<DateTime>? now = null,
            Hangfire.IBackgroundJobClient? backgroundJobClient = null,
            IEmailNotificationService? emailService = null,
            OCPP.Core.Server.Payments.Invoices.IInvoiceIntegrationService? invoiceIntegrationService = null,
            PaymentFlowOptions? flowOptions = null)
        {
            var stripeGateway = new StripePaymentGateway(
                new StripeOptions { Enabled = true, ApiKey = "test", ReturnBaseUrl = ReturnBaseUrl, WebhookSecret = "whsec_test" },
                stripeSessions ?? new FakeSessionService(),
                stripeIntents ?? new FakePaymentIntentService(),
                new FakeEventFactory(),
                NullLogger.Instance);
            var registry = new PaymentGatewayRegistry(new IPaymentGateway[] { fake, stripeGateway }, fake.ProviderName);
            return new PaymentCoordinator(
                registry,
                Settings(),
                Options.Create(flowOptions ?? new PaymentFlowOptions { StartWindowMinutes = 7 }),
                NullLogger<PaymentCoordinator>.Instance,
                now ?? (() => Now),
                emailNotificationService: emailService,
                backgroundJobClient: backgroundJobClient,
                invoiceIntegrationService: invoiceIntegrationService);
        }

        private static ChargePaymentReservation AddPendingReservation(OCPPCoreContext context, string provider, string checkoutReference)
        {
            var reservation = new ChargePaymentReservation
            {
                ReservationId = Guid.NewGuid(),
                ChargePointId = "CP1",
                ConnectorId = 1,
                ChargeTagId = "TAG1",
                PaymentProvider = provider,
                StripeCheckoutSessionId = checkoutReference,
                Status = PaymentReservationStatus.Pending,
                MaxAmountCents = 450,
                Currency = "eur",
                CreatedAtUtc = Now.AddMinutes(-2),
                UpdatedAtUtc = Now.AddMinutes(-2)
            };
            context.ChargePaymentReservations.Add(reservation);
            context.SaveChanges();
            return reservation;
        }

        private static ChargePaymentReservation AddChargingReservation(OCPPCoreContext context, string provider, string authorizationReference)
        {
            var reservation = new ChargePaymentReservation
            {
                ReservationId = Guid.NewGuid(),
                ChargePointId = "CP1",
                ConnectorId = 1,
                ChargeTagId = "TAG1",
                OcppIdTag = "TAG1",
                PaymentProvider = provider,
                StripeCheckoutSessionId = $"{authorizationReference}_checkout",
                StripePaymentIntentId = authorizationReference,
                Status = PaymentReservationStatus.Charging,
                PricePerKwh = 0.40m,
                UserSessionFee = 0.50m,
                MaxAmountCents = 450,
                Currency = "eur",
                TransactionId = 88,
                StartTransactionId = 88,
                CreatedAtUtc = Now.AddHours(-1),
                UpdatedAtUtc = Now.AddMinutes(-30)
            };
            context.ChargePaymentReservations.Add(reservation);
            context.SaveChanges();
            return reservation;
        }

        private static Transaction AddStoppedTransaction(OCPPCoreContext context, ChargePaymentReservation reservation, double meterStopKwh)
        {
            var transaction = new Transaction
            {
                TransactionId = reservation.TransactionId ?? 88,
                ChargePointId = reservation.ChargePointId,
                ConnectorId = reservation.ConnectorId,
                StartTagId = reservation.OcppIdTag,
                StartTime = Now.AddMinutes(-40),
                StopTime = Now.AddMinutes(-5),
                StopReason = "EVDisconnected",
                MeterStart = 0,
                MeterStop = meterStopKwh
            };
            context.Transactions.Add(transaction);
            context.SaveChanges();
            return transaction;
        }

        private static ChargePaymentReservation AddReleaseReservation(
            OCPPCoreContext context,
            string provider,
            string? authorizationReference,
            string checkoutReference = "release_checkout")
        {
            var reservation = new ChargePaymentReservation
            {
                ReservationId = Guid.NewGuid(),
                ChargePointId = "CP-RELEASE",
                ConnectorId = 1,
                ChargeTagId = "TAG1",
                OcppIdTag = "TAG1",
                PaymentProvider = provider,
                StripeCheckoutSessionId = checkoutReference,
                StripePaymentIntentId = authorizationReference,
                Status = PaymentReservationStatus.Abandoned,
                Currency = "eur",
                CreatedAtUtc = Now.AddHours(-1),
                AuthorizedAtUtc = Now.AddHours(-1),
                StartDeadlineAtUtc = Now.AddMinutes(10),
                UpdatedAtUtc = Now.AddMinutes(-10),
                AuthorizationReleaseState = PaymentAuthorizationReleaseState.Pending
            };
            context.ChargePaymentReservations.Add(reservation);
            context.SaveChanges();
            return reservation;
        }

        private static PaymentCheckoutStatus CompletedCheckout(
            Guid reservationId,
            string sessionReference,
            string authorizationReference,
            bool inlineAuthorization,
            long amountMinor) =>
            new()
            {
                SessionReference = sessionReference,
                State = PaymentCheckoutState.Complete,
                RawStatus = "AUTHORIZED",
                IsPaid = true,
                AuthorizationReference = authorizationReference,
                Authorization = inlineAuthorization
                    ? Authorization(reservationId, authorizationReference, PaymentAuthorizationState.RequiresCapture, amountMinor)
                    : null,
                CustomerEmail = "payer@example.com",
                CustomerName = "Pat Payer",
                OwnerReservationId = reservationId
            };

        private static PaymentAuthorizationSnapshot Authorization(
            Guid reservationId,
            string authorizationReference,
            PaymentAuthorizationState state,
            long amountMinor) =>
            new()
            {
                AuthorizationReference = authorizationReference,
                State = state,
                RawStatus = state == PaymentAuthorizationState.RequiresCapture ? "AUTHORIZED" : state.ToString().ToUpperInvariant(),
                AmountMinor = amountMinor,
                AmountCapturableMinor = state == PaymentAuthorizationState.RequiresCapture ? amountMinor : 0,
                Currency = "EUR",
                IsCancelable = state == PaymentAuthorizationState.RequiresCapture,
                OwnerReservationId = reservationId
            };

        private static PaymentWebhookParseResult Notification(Guid reservationId, string? eventId) =>
            new()
            {
                Accepted = true,
                Events = new[]
                {
                    new PaymentWebhookEvent
                    {
                        EventId = eventId,
                        EventType = "notify",
                        Kind = PaymentWebhookEventKind.CheckoutNotification,
                        ReservationId = reservationId
                    }
                }
            };

        private static PaymentWebhookRequest NotifyRequest() => new()
        {
            Provider = "fakepay",
            Method = "GET"
        };

        private sealed class IdleWarningHarness : IdleFeeWarningEmailService
        {
            public IdleWarningHarness(
                IServiceScopeFactory scopeFactory,
                ILogger<IdleFeeWarningEmailService> logger,
                IConfiguration configuration,
                IOptions<NotificationOptions> notificationOptions,
                PaymentCheckoutSettings checkoutSettings,
                Func<DateTime> utcNow)
                : base(scopeFactory, logger, configuration, notificationOptions, checkoutSettings, utcNow)
            {
            }

            public Task RunOnce(CancellationToken token = default) => SweepAsync(token);
        }
    }

    /// <summary>Saferpay-like test gateway: no success-URL placeholder, no metadata, no deferred lookup.</summary>
    internal sealed class FakePaymentGateway : IPaymentGateway
    {
        public FakePaymentGateway(string providerName = "FakePay")
        {
            ProviderName = providerName;
        }

        /// <summary>
        /// Models the Saferpay payment page: a 1 hour page lifetime without reliable late signals, captures at
        /// checkout that need a refund, and an authorization lookup by reservation (Inquire by OrderId).
        /// </summary>
        public static FakePaymentGateway SaferpayLike() => new()
        {
            CheckoutLifetime = TimeSpan.FromHours(1),
            CapturedCheckoutRequiresRefund = true
        };

        public string ProviderName { get; }
        public bool IsConfigured { get; set; } = true;
        public string? SuccessUrlSessionPlaceholder => null;
        public bool SupportsMetadata => false;
        public bool SupportsDeferredCheckoutLookup => false;
        public TimeSpan? CheckoutLifetime { get; set; }
        public bool CapturedCheckoutRequiresRefund { get; set; }

        /// <summary>Result of <see cref="FindAuthorizationByReservation"/>; null means none was found.</summary>
        public PaymentAuthorizationSnapshot? AuthorizationByReservation { get; set; }
        public Exception? GetAuthorizationException { get; set; }

        /// <summary>Snapshot returned by Cancel; null returns a Canceled snapshot.</summary>
        public PaymentAuthorizationSnapshot? CancelResponse { get; set; }

        /// <summary>Snapshot returned by Capture; null returns a Captured snapshot for the requested amount.</summary>
        public PaymentAuthorizationSnapshot? CaptureResponse { get; set; }

        public PaymentAuthorizationSnapshot? FindAuthorizationByReservation(Guid reservationId)
        {
            Calls.Add($"FindAuthorizationByReservation:{reservationId}");
            return AuthorizationByReservation;
        }

        public List<string> Calls { get; } = new();
        public List<PaymentCheckoutRequest> CheckoutRequests { get; } = new();
        public List<(string Reference, long AmountMinor, string Currency, string IdempotencyKey)> Captures { get; } = new();
        public List<(string Reference, string IdempotencyKey)> Cancels { get; } = new();

        public PaymentCheckoutSession CreateResponse { get; set; } = new()
        {
            SessionReference = "fake_token_1",
            RedirectUrl = "https://pay.example.com/checkout/fake_token_1"
        };

        public PaymentCheckoutStatus? CheckoutStatus { get; set; }
        public PaymentAuthorizationSnapshot? AuthorizationResponse { get; set; }
        public Exception? GetCheckoutException { get; set; }
        public PaymentWebhookParseResult ParseResult { get; set; } = PaymentWebhookParseResult.Rejected("not_configured");

        public PaymentCheckoutSession CreateCheckout(PaymentCheckoutRequest request)
        {
            Calls.Add("CreateCheckout");
            CheckoutRequests.Add(request);
            return CreateResponse;
        }

        public PaymentCheckoutStatus GetCheckout(string sessionReference)
        {
            Calls.Add($"GetCheckout:{sessionReference}");
            if (GetCheckoutException != null) throw GetCheckoutException;
            return CheckoutStatus!;
        }

        public PaymentAuthorizationSnapshot GetAuthorization(string authorizationReference)
        {
            Calls.Add($"GetAuthorization:{authorizationReference}");
            if (GetAuthorizationException != null) throw GetAuthorizationException;
            return AuthorizationResponse!;
        }

        public PaymentAuthorizationSnapshot Capture(string authorizationReference, long amountMinor, string currency, string idempotencyKey)
        {
            Calls.Add($"Capture:{authorizationReference}");
            Captures.Add((authorizationReference, amountMinor, currency, idempotencyKey));
            if (CaptureResponse != null) return CaptureResponse;
            return new PaymentAuthorizationSnapshot
            {
                AuthorizationReference = authorizationReference,
                State = PaymentAuthorizationState.Captured,
                RawStatus = "CAPTURED",
                AmountMinor = amountMinor,
                AmountReceivedMinor = amountMinor,
                CaptureReference = $"{authorizationReference}_capture"
            };
        }

        public Exception? CancelException { get; set; }

        public PaymentAuthorizationSnapshot Cancel(string authorizationReference, string idempotencyKey)
        {
            Calls.Add($"Cancel:{authorizationReference}");
            if (CancelException != null) throw CancelException;
            Cancels.Add((authorizationReference, idempotencyKey));
            if (CancelResponse != null) return CancelResponse;
            return new PaymentAuthorizationSnapshot
            {
                AuthorizationReference = authorizationReference,
                State = PaymentAuthorizationState.Canceled,
                RawStatus = "CANCELED"
            };
        }

        public IReadOnlyDictionary<string, string> MirrorMetadata(
            string sessionReference,
            string fallbackAuthorizationReference,
            Action<IDictionary<string, string>> mutate)
        {
            Calls.Add("MirrorMetadata");
            throw new NotSupportedException("The fake gateway has no metadata.");
        }

        public PaymentWebhookParseResult ParseWebhook(PaymentWebhookRequest request)
        {
            Calls.Add("ParseWebhook");
            return ParseResult;
        }

        public string SanitizeProviderText(string text) => text;
    }
}
