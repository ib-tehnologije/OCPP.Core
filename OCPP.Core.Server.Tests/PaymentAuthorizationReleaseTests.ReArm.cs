using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OCPP.Core.Database;
using OCPP.Core.Server.Payments;
using OCPP.Core.Server.Payments.Providers;
using Stripe;
using Stripe.Checkout;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    /// <summary>
    /// Stripe: re-armed releases get a new retry budget, late authorizations are released on the provider signal
    /// even after the release retries were used up, and abandoned or expired sessions finish their release.
    /// </summary>
    public partial class PaymentAuthorizationReleaseTests
    {
        [Fact]
        public void CheckoutCompletedWebhook_ReArmedExhaustedReleaseCancelsTheLateAuthorizationOnRedelivery()
        {
            using var context = CreateContext();
            var reservation = AddReservation(context, armed: false, status: PaymentReservationStatus.Abandoned);
            // Stale pending row the sweep abandoned; its linkage retries ran out while the session was still open.
            reservation.StripeCheckoutSessionId = "sess_late";
            reservation.StripePaymentIntentId = null;
            reservation.OcppIdTag = null;
            reservation.AuthorizedAtUtc = null;
            reservation.StartDeadlineAtUtc = null;
            reservation.AuthorizationReleaseState = PaymentAuthorizationReleaseState.PermanentFailure;
            reservation.AuthorizationReleaseAttemptCount = 4;
            context.SaveChanges();
            var intents = new ReleasePaymentIntentService
            {
                GetResponse = new PaymentIntent
                {
                    Id = "pi_late",
                    Status = "requires_capture",
                    Amount = 500,
                    AmountCapturable = 500,
                    Currency = "eur",
                    Metadata = new Dictionary<string, string> { ["reservation_id"] = reservation.ReservationId.ToString() }
                },
                CancelException = new StripeException(
                    HttpStatusCode.ServiceUnavailable,
                    new StripeError { Type = "api_error", Code = "temporary_provider_failure", Message = "try again" },
                    "try again")
            };
            var stripeEvent = new Event
            {
                Id = "evt_late",
                Type = EventTypes.CheckoutSessionCompleted,
                Data = new EventData
                {
                    Object = new Session { Id = "sess_late", PaymentIntentId = "pi_late", PaymentStatus = "paid" }
                }
            };
            var coordinator = CreateCoordinator(intents, stripeEvent: stripeEvent);

            var first = coordinator
                .HandleWebhookAsync(context, new PaymentWebhookRequest { Body = "payload" }, CancellationToken.None)
                .GetAwaiter().GetResult();

            Assert.Equal(503, first.StatusCode);
            Assert.Equal(PaymentAuthorizationReleaseState.Pending, reservation.AuthorizationReleaseState);
            Assert.Equal(0, reservation.AuthorizationReleaseAttemptCount);
            Assert.Equal(1, intents.CancelCalls);

            // Stripe redelivers the event and is healthy again.
            intents.CancelException = null;
            var second = coordinator
                .HandleWebhookAsync(context, new PaymentWebhookRequest { Body = "payload" }, CancellationToken.None)
                .GetAwaiter().GetResult();

            Assert.Equal(200, second.StatusCode);
            Assert.Equal(2, intents.CancelCalls);
            Assert.Equal(PaymentAuthorizationReleaseState.Released, reservation.AuthorizationReleaseState);
            Assert.Equal(1, reservation.AuthorizationReleaseAttemptCount);
            Assert.Equal(
                PaymentAuthorizationReleaseOutcome.Released,
                context.PaymentAuthorizationReleaseAttempts.Single().Outcome);
        }

        [Theory]
        [InlineData(EventTypes.CheckoutSessionCompleted)]
        [InlineData("payment_intent.amount_capturable_updated")]
        public void Webhook_ArmedReleaseWithoutRetriesLeftStillCancelsTheLateAuthorization(string eventType)
        {
            using var context = CreateContext();
            var reservation = AddReservation(context);
            reservation.StripeCheckoutSessionId = "sess_exhausted";
            reservation.AuthorizationReleaseState = PaymentAuthorizationReleaseState.RetryScheduled;
            reservation.AuthorizationReleaseAttemptCount = 4;
            context.SaveChanges();
            var intents = OwnedIntentService(reservation, "requires_capture", amountCapturable: 500);
            var stripeEvent = new Event
            {
                Id = "evt_exhausted",
                Type = eventType,
                Data = new EventData
                {
                    Object = eventType == EventTypes.CheckoutSessionCompleted
                        ? new Session { Id = "sess_exhausted", PaymentIntentId = reservation.StripePaymentIntentId, PaymentStatus = "paid" }
                        : intents.GetResponse
                }
            };
            var coordinator = CreateCoordinator(intents, stripeEvent: stripeEvent);

            coordinator.HandleWebhookEvent(context, "payload", "signature");

            Assert.Equal(1, intents.CancelCalls);
            Assert.Equal($"cancel:{reservation.ReservationId}", intents.LastCancelRequestOptions!.IdempotencyKey);
            Assert.Equal(PaymentAuthorizationReleaseState.Released, reservation.AuthorizationReleaseState);
            Assert.Equal("late_authorization_released", reservation.LastError);
        }

        [Theory]
        [InlineData(PaymentAuthorizationReleaseState.PermanentFailure)]
        [InlineData(PaymentAuthorizationReleaseState.RetryScheduled)]
        public void CheckoutExpiredWebhook_FinishesTheReleaseOfAnAbandonedUnpaidSession(string releaseState)
        {
            using var context = CreateContext();
            var reservation = AddReservation(context, armed: false, status: PaymentReservationStatus.Abandoned);
            reservation.StripeCheckoutSessionId = "sess_expired";
            reservation.StripePaymentIntentId = null;
            reservation.OcppIdTag = null;
            reservation.AuthorizedAtUtc = null;
            reservation.StartDeadlineAtUtc = null;
            reservation.AuthorizationReleaseState = releaseState;
            reservation.AuthorizationReleaseAttemptCount = 4;
            reservation.AuthorizationReleaseNextAttemptAtUtc = releaseState == PaymentAuthorizationReleaseState.RetryScheduled ? Now.AddMinutes(8) : null;
            reservation.AuthorizationReleaseLastError = "Authorization release retry budget exhausted before payment authorization linkage completed.";
            context.SaveChanges();
            var intents = new ReleasePaymentIntentService();
            var stripeEvent = new Event
            {
                Id = "evt_expired",
                Type = EventTypes.CheckoutSessionExpired,
                Data = new EventData
                {
                    Object = new Session { Id = "sess_expired", Status = "expired", PaymentStatus = "unpaid" }
                }
            };
            var coordinator = CreateCoordinator(intents, stripeEvent: stripeEvent);

            coordinator.HandleWebhookEvent(context, "payload", "signature");

            Assert.Equal(PaymentReservationStatus.Abandoned, reservation.Status);
            Assert.Equal(PaymentAuthorizationReleaseState.Released, reservation.AuthorizationReleaseState);
            Assert.Null(reservation.AuthorizationReleaseLastError);
            Assert.Null(reservation.AuthorizationReleaseNextAttemptAtUtc);
            Assert.Equal(0, intents.GetCalls);
            Assert.Equal(0, intents.CancelCalls);
        }

        [Fact]
        public void CheckoutCompletedWebhook_ReleasesALateAuthorizationAfterTheAbandonedSessionWasReleased()
        {
            using var context = CreateContext();
            var reservation = AddReservation(context, armed: false, status: PaymentReservationStatus.Abandoned);
            reservation.StripeCheckoutSessionId = "sess_paid_later";
            reservation.StripePaymentIntentId = null;
            reservation.OcppIdTag = null;
            reservation.AuthorizedAtUtc = null;
            reservation.StartDeadlineAtUtc = null;
            // The sweep found the session open and unpaid: nothing was held then.
            reservation.AuthorizationReleaseState = PaymentAuthorizationReleaseState.Released;
            context.SaveChanges();
            var intents = OwnedIntentService(reservation, "requires_capture", amountCapturable: 500);
            intents.GetResponse.Id = "pi_paid_later";
            var stripeEvent = new Event
            {
                Id = "evt_paid_later",
                Type = EventTypes.CheckoutSessionCompleted,
                Data = new EventData
                {
                    Object = new Session { Id = "sess_paid_later", PaymentIntentId = "pi_paid_later", PaymentStatus = "paid" }
                }
            };
            var coordinator = CreateCoordinator(intents, stripeEvent: stripeEvent);

            coordinator.HandleWebhookEvent(context, "payload", "signature");

            Assert.Equal(PaymentReservationStatus.Abandoned, reservation.Status);
            Assert.Equal(1, intents.CancelCalls);
            Assert.Equal("late_authorization_released", reservation.LastError);
            Assert.Equal(PaymentAuthorizationReleaseState.Released, reservation.AuthorizationReleaseState);
        }

        [Fact]
        public void Confirm_StripeReturnThatLosesToTheWebhookStillPreparesTheTagAndEmailsThePayer()
        {
            using var keepAlive = new SqliteConnection($"Data Source=stripe-race-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
            keepAlive.Open();
            var options = new DbContextOptionsBuilder<OCPPCoreContext>().UseSqlite(keepAlive.ConnectionString).Options;
            using var browserContext = new OCPPCoreContext(options);
            browserContext.Database.EnsureCreated();
            var reservation = AddReservation(browserContext, armed: false, status: PaymentReservationStatus.Pending);
            reservation.StripeCheckoutSessionId = "sess_race";
            reservation.StripePaymentIntentId = null;
            reservation.OcppIdTag = null;
            reservation.AuthorizedAtUtc = null;
            reservation.StartDeadlineAtUtc = null;
            browserContext.SaveChanges();
            var paidSession = new Session
            {
                Id = "sess_race",
                Status = "complete",
                PaymentStatus = "paid",
                PaymentIntentId = "pi_race",
                Metadata = new Dictionary<string, string> { ["reservation_id"] = reservation.ReservationId.ToString() }
            };
            var intents = OwnedIntentService(reservation, "requires_capture", amountCapturable: 500);
            intents.GetResponse.Id = "pi_race";
            var sessions = new HookedReleaseSessionService { GetResponse = paidSession };
            var email = new FakeEmailNotificationService();
            var stripeEvent = new Event
            {
                Id = "evt_race",
                Type = EventTypes.CheckoutSessionCompleted,
                Data = new EventData { Object = paidSession }
            };
            var coordinator = new StripePaymentCoordinator(
                Options.Create(new StripeOptions { Enabled = true, ApiKey = "test", ReturnBaseUrl = "https://return", WebhookSecret = "whsec_test" }),
                Options.Create(new PaymentFlowOptions()),
                NullLogger<StripePaymentCoordinator>.Instance,
                sessions,
                intents,
                new ReleaseEventFactory { EventToReturn = stripeEvent },
                () => Now,
                email);
            // The webhook authorizes the reservation while the browser return is reading the session.
            sessions.AfterNextGet = () =>
            {
                using var webhookContext = new OCPPCoreContext(options);
                coordinator.HandleWebhookEvent(webhookContext, "payload", "signature");
            };

            var confirmation = coordinator.ConfirmReservation(browserContext, reservation.ReservationId, "sess_race");

            Assert.True(confirmation.Success);
            Assert.Equal(PaymentReservationStatus.Authorized, confirmation.Status);
            Assert.Equal(1, email.PaymentAuthorizedCount);
            using var verification = new OCPPCoreContext(options);
            var persisted = verification.ChargePaymentReservations.Single();
            Assert.Equal(PaymentReservationStatus.Authorized, persisted.Status);
            Assert.Equal("pi_race", persisted.StripePaymentIntentId);
            Assert.False(string.IsNullOrEmpty(persisted.OcppIdTag));
            Assert.Equal(persisted.OcppIdTag, verification.ChargeTags.Single().TagId);
            Assert.Equal(0, intents.CancelCalls);
        }

        private sealed class HookedReleaseSessionService : IStripeSessionService
        {
            public Session? GetResponse { get; set; }
            public Action? AfterNextGet { get; set; }

            public Session Create(SessionCreateOptions options, RequestOptions requestOptions = null!) =>
                throw new NotImplementedException();

            public Session Get(string id)
            {
                var response = GetResponse!;
                var hook = AfterNextGet;
                AfterNextGet = null;
                hook?.Invoke();
                return response;
            }

            public Session Update(string id, SessionUpdateOptions options, RequestOptions requestOptions = null!) =>
                throw new NotImplementedException();
        }
    }
}
