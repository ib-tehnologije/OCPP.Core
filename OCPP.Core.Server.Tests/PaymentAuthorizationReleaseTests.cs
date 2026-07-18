using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OCPP.Core.Database;
using OCPP.Core.Server.Payments;
using Stripe;
using Stripe.Checkout;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    public class PaymentAuthorizationReleaseTests
    {
        private static readonly DateTime Now = new(2026, 7, 18, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Reconcile_ReleasesOwnedRequiresCaptureAuthorization()
        {
            using var context = CreateContext();
            var reservation = AddReservation(context);
            var intents = OwnedIntentService(reservation, "requires_capture", amountCapturable: 500);
            var coordinator = CreateCoordinator(intents);

            var result = coordinator.ReconcileTerminalPaymentAuthorization(
                context,
                reservation,
                PaymentAuthorizationReleaseTrigger.CleanupSweep);

            Assert.Equal(PaymentAuthorizationReleaseOutcome.Released, result.Outcome);
            Assert.Equal(PaymentAuthorizationReleaseState.Released, reservation.AuthorizationReleaseState);
            Assert.Equal(Now, reservation.AuthorizationReleasedAtUtc);
            Assert.Null(reservation.AuthorizationReleaseNextAttemptAtUtc);
            Assert.Equal(1, intents.CancelCalls);
            Assert.Contains(reservation.ReservationId.ToString(), intents.LastCancelRequestOptions!.IdempotencyKey);
            Assert.EndsWith(":1", intents.LastCancelRequestOptions.IdempotencyKey);

            var attempt = context.PaymentAuthorizationReleaseAttempts.Single();
            Assert.Equal(1, attempt.AttemptNumber);
            Assert.Equal("requires_capture", attempt.ProviderStatus);
            Assert.Equal(500, attempt.AmountCapturableCents);
            Assert.Equal(PaymentAuthorizationReleaseOutcome.Released, attempt.Outcome);
            Assert.Equal(Now, attempt.FinishedAtUtc);
        }

        [Fact]
        public void Reconcile_TreatsAlreadyCanceledAsSuccessfulNoOp()
        {
            using var context = CreateContext();
            var reservation = AddReservation(context);
            var intents = OwnedIntentService(reservation, "canceled", amountCapturable: 0);
            var coordinator = CreateCoordinator(intents);

            var result = coordinator.ReconcileTerminalPaymentAuthorization(
                context,
                reservation,
                PaymentAuthorizationReleaseTrigger.CleanupSweep);

            Assert.Equal(PaymentAuthorizationReleaseOutcome.AlreadyReleased, result.Outcome);
            Assert.Equal(PaymentAuthorizationReleaseState.Released, reservation.AuthorizationReleaseState);
            Assert.Equal(Now, reservation.AuthorizationReleasedAtUtc);
            Assert.Equal(0, intents.CancelCalls);
        }

        [Fact]
        public void Reconcile_DoesNotArmHistoricalTerminalReservation()
        {
            using var context = CreateContext();
            var reservation = AddReservation(context, armed: false);
            var intents = OwnedIntentService(reservation, "requires_capture", amountCapturable: 500);
            var coordinator = CreateCoordinator(intents);

            var result = coordinator.ReconcileTerminalPaymentAuthorization(
                context,
                reservation,
                PaymentAuthorizationReleaseTrigger.CleanupSweep);

            Assert.Equal(PaymentAuthorizationReleaseOutcome.SkippedNotEligible, result.Outcome);
            Assert.Null(reservation.AuthorizationReleaseState);
            Assert.Equal(0, intents.GetCalls);
            Assert.Equal(0, intents.CancelCalls);
            Assert.Empty(context.PaymentAuthorizationReleaseAttempts);
        }

        [Fact]
        public void Reconcile_ExcludesActiveTransactionBeforeProviderRead()
        {
            using var context = CreateContext();
            var reservation = AddReservation(context);
            reservation.TransactionId = 42;
            context.Transactions.Add(new Transaction
            {
                TransactionId = 42,
                ChargePointId = reservation.ChargePointId,
                ConnectorId = reservation.ConnectorId,
                StartTagId = reservation.ChargeTagId,
                StartTime = Now.AddMinutes(-5)
            });
            context.SaveChanges();
            var intents = OwnedIntentService(reservation, "requires_capture", amountCapturable: 500);
            var coordinator = CreateCoordinator(intents);

            var result = coordinator.ReconcileTerminalPaymentAuthorization(
                context,
                reservation,
                PaymentAuthorizationReleaseTrigger.CleanupSweep);

            Assert.Equal(PaymentAuthorizationReleaseOutcome.ReviewRequired, result.Outcome);
            Assert.Equal(PaymentAuthorizationReleaseState.ReviewRequired, reservation.AuthorizationReleaseState);
            Assert.Equal(0, intents.GetCalls);
            Assert.Equal(0, intents.CancelCalls);
            Assert.Contains("active transaction", reservation.AuthorizationReleaseLastError, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Reconcile_ExcludesCapturedOrInvoicedReservationBeforeProviderRead()
        {
            using var capturedContext = CreateContext();
            var captured = AddReservation(capturedContext);
            captured.CapturedAmountCents = 100;
            captured.CapturedAtUtc = Now.AddMinutes(-1);
            capturedContext.SaveChanges();
            var capturedIntents = OwnedIntentService(captured, "requires_capture", amountCapturable: 500);

            var capturedResult = CreateCoordinator(capturedIntents)
                .ReconcileTerminalPaymentAuthorization(capturedContext, captured, PaymentAuthorizationReleaseTrigger.CleanupSweep);

            Assert.Equal(PaymentAuthorizationReleaseOutcome.ReviewRequired, capturedResult.Outcome);
            Assert.Equal(0, capturedIntents.GetCalls);

            using var invoicedContext = CreateContext();
            var invoiced = AddReservation(invoicedContext);
            invoicedContext.InvoiceSubmissionLogs.Add(new InvoiceSubmissionLog
            {
                ReservationId = invoiced.ReservationId,
                Provider = "test",
                Status = "Submitted",
                CreatedAtUtc = Now.AddMinutes(-1)
            });
            invoicedContext.SaveChanges();
            var invoicedIntents = OwnedIntentService(invoiced, "requires_capture", amountCapturable: 500);

            var invoicedResult = CreateCoordinator(invoicedIntents)
                .ReconcileTerminalPaymentAuthorization(invoicedContext, invoiced, PaymentAuthorizationReleaseTrigger.CleanupSweep);

            Assert.Equal(PaymentAuthorizationReleaseOutcome.ReviewRequired, invoicedResult.Outcome);
            Assert.Equal(0, invoicedIntents.GetCalls);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("00000000-0000-0000-0000-000000000001")]
        public void Reconcile_RequiresMatchingProviderOwnership(string? providerReservationId)
        {
            using var context = CreateContext();
            var reservation = AddReservation(context);
            var metadata = providerReservationId == null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string> { ["reservation_id"] = providerReservationId };
            var intents = new ReleasePaymentIntentService
            {
                GetResponse = new PaymentIntent
                {
                    Id = reservation.StripePaymentIntentId,
                    Status = "requires_capture",
                    AmountCapturable = 500,
                    Metadata = metadata
                }
            };
            var coordinator = CreateCoordinator(intents);

            var result = coordinator.ReconcileTerminalPaymentAuthorization(
                context,
                reservation,
                PaymentAuthorizationReleaseTrigger.CleanupSweep);

            Assert.Equal(PaymentAuthorizationReleaseOutcome.ReviewRequired, result.Outcome);
            Assert.Equal(0, intents.CancelCalls);
            Assert.Contains("ownership", reservation.AuthorizationReleaseLastError, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Reconcile_SucceededProviderStateRequiresReview()
        {
            using var context = CreateContext();
            var reservation = AddReservation(context);
            var intents = OwnedIntentService(reservation, "succeeded", amountCapturable: 0);
            var coordinator = CreateCoordinator(intents);

            var result = coordinator.ReconcileTerminalPaymentAuthorization(
                context,
                reservation,
                PaymentAuthorizationReleaseTrigger.CleanupSweep);

            Assert.Equal(PaymentAuthorizationReleaseOutcome.ReviewRequired, result.Outcome);
            Assert.Equal(PaymentAuthorizationReleaseState.ReviewRequired, reservation.AuthorizationReleaseState);
            Assert.Equal(0, intents.CancelCalls);
            Assert.Contains("succeeded", reservation.AuthorizationReleaseLastError, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Reconcile_TransientProviderFailureSchedulesSanitizedRetry()
        {
            using var context = CreateContext();
            var reservation = AddReservation(context);
            var intents = OwnedIntentService(reservation, "requires_capture", amountCapturable: 500);
            intents.CancelException = new StripeException(
                HttpStatusCode.ServiceUnavailable,
                new StripeError
                {
                    Type = "api_error",
                    Code = "temporary_provider_failure",
                    Message = "Retry pi_sensitive for driver@example.com"
                },
                "Retry pi_sensitive for driver@example.com");
            var coordinator = CreateCoordinator(intents);

            var result = coordinator.ReconcileTerminalPaymentAuthorization(
                context,
                reservation,
                PaymentAuthorizationReleaseTrigger.CleanupSweep);

            Assert.Equal(PaymentAuthorizationReleaseOutcome.RetryScheduled, result.Outcome);
            Assert.Equal(PaymentAuthorizationReleaseState.RetryScheduled, reservation.AuthorizationReleaseState);
            Assert.Equal(Now.AddMinutes(1), reservation.AuthorizationReleaseNextAttemptAtUtc);
            Assert.DoesNotContain("driver@example.com", reservation.AuthorizationReleaseLastError);
            Assert.DoesNotContain("pi_sensitive", reservation.AuthorizationReleaseLastError);
            Assert.Contains("[redacted-email]", reservation.AuthorizationReleaseLastError);
            Assert.Contains("[redacted-id]", reservation.AuthorizationReleaseLastError);

            var attempt = context.PaymentAuthorizationReleaseAttempts.Single();
            Assert.Equal("temporary_provider_failure", attempt.ErrorCode);
            Assert.Equal(PaymentAuthorizationReleaseOutcome.RetryScheduled, attempt.Outcome);
            Assert.Equal(Now.AddMinutes(1), attempt.NextRetryAtUtc);
        }

        [Fact]
        public void Reconcile_PermanentProviderFailureDoesNotRetry()
        {
            using var context = CreateContext();
            var reservation = AddReservation(context);
            var intents = OwnedIntentService(reservation, "requires_capture", amountCapturable: 500);
            intents.CancelException = new StripeException(
                HttpStatusCode.BadRequest,
                new StripeError { Type = "invalid_request_error", Code = "payment_intent_unexpected_state", Message = "wrong state" },
                "wrong state");
            var coordinator = CreateCoordinator(intents);

            var result = coordinator.ReconcileTerminalPaymentAuthorization(
                context,
                reservation,
                PaymentAuthorizationReleaseTrigger.CleanupSweep);

            Assert.Equal(PaymentAuthorizationReleaseOutcome.PermanentFailure, result.Outcome);
            Assert.Equal(PaymentAuthorizationReleaseState.PermanentFailure, reservation.AuthorizationReleaseState);
            Assert.Null(reservation.AuthorizationReleaseNextAttemptAtUtc);
        }

        [Fact]
        public void Reconcile_StopsAtConfiguredRetryBudget()
        {
            using var context = CreateContext();
            var reservation = AddReservation(context);
            reservation.AuthorizationReleaseAttemptCount = 2;
            context.SaveChanges();
            var intents = OwnedIntentService(reservation, "requires_capture", amountCapturable: 500);
            var coordinator = CreateCoordinator(intents, new Dictionary<string, string?>
            {
                ["Maintenance:AuthorizationReleaseMaxAttempts"] = "2"
            });

            var result = coordinator.ReconcileTerminalPaymentAuthorization(
                context,
                reservation,
                PaymentAuthorizationReleaseTrigger.CleanupSweep);

            Assert.Equal(PaymentAuthorizationReleaseOutcome.PermanentFailure, result.Outcome);
            Assert.Equal(PaymentAuthorizationReleaseState.PermanentFailure, reservation.AuthorizationReleaseState);
            Assert.Equal(0, intents.GetCalls);
            Assert.Equal(0, intents.CancelCalls);
        }

        private static OCPPCoreContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<OCPPCoreContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            return new OCPPCoreContext(options);
        }

        private static ChargePaymentReservation AddReservation(
            OCPPCoreContext context,
            bool armed = true,
            string status = PaymentReservationStatus.Abandoned)
        {
            var reservation = new ChargePaymentReservation
            {
                ReservationId = Guid.NewGuid(),
                ChargePointId = "CP1",
                ConnectorId = 1,
                ChargeTagId = "TAG1",
                StripePaymentIntentId = "pi_test",
                Status = status,
                Currency = "eur",
                CreatedAtUtc = Now.AddHours(-1),
                UpdatedAtUtc = Now.AddMinutes(-10),
                AuthorizationReleaseState = armed ? PaymentAuthorizationReleaseState.Pending : null
            };
            context.ChargePaymentReservations.Add(reservation);
            context.SaveChanges();
            return reservation;
        }

        private static ReleasePaymentIntentService OwnedIntentService(
            ChargePaymentReservation reservation,
            string status,
            long amountCapturable)
        {
            return new ReleasePaymentIntentService
            {
                GetResponse = new PaymentIntent
                {
                    Id = reservation.StripePaymentIntentId,
                    Status = status,
                    AmountCapturable = amountCapturable,
                    Metadata = new Dictionary<string, string>
                    {
                        ["reservation_id"] = reservation.ReservationId.ToString()
                    }
                }
            };
        }

        private static StripePaymentCoordinator CreateCoordinator(
            ReleasePaymentIntentService intents,
            IDictionary<string, string?>? settings = null)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(settings ?? new Dictionary<string, string?>())
                .Build();

            return new StripePaymentCoordinator(
                Options.Create(new StripeOptions
                {
                    Enabled = true,
                    ApiKey = "test",
                    ReturnBaseUrl = "https://return"
                }),
                Options.Create(new PaymentFlowOptions()),
                NullLogger<StripePaymentCoordinator>.Instance,
                new ReleaseSessionService(),
                intents,
                new ReleaseEventFactory(),
                () => Now,
                configuration: configuration);
        }
    }

    internal sealed class ReleasePaymentIntentService : IStripePaymentIntentService
    {
        public PaymentIntent GetResponse { get; set; } = new();
        public PaymentIntent CancelResponse { get; set; } = new() { Status = "canceled" };
        public StripeException? GetException { get; set; }
        public StripeException? CancelException { get; set; }
        public int GetCalls { get; private set; }
        public int CancelCalls { get; private set; }
        public RequestOptions? LastCancelRequestOptions { get; private set; }

        public PaymentIntent Get(string id)
        {
            GetCalls++;
            if (GetException != null) throw GetException;
            return GetResponse;
        }

        public PaymentIntent Update(string id, PaymentIntentUpdateOptions options, RequestOptions requestOptions = null!) =>
            throw new NotImplementedException();

        public PaymentIntent Capture(string id, PaymentIntentCaptureOptions options, RequestOptions requestOptions = null!) =>
            throw new NotImplementedException();

        public PaymentIntent Cancel(string id, RequestOptions requestOptions = null!)
        {
            CancelCalls++;
            LastCancelRequestOptions = requestOptions;
            if (CancelException != null) throw CancelException;
            CancelResponse.Id = id;
            return CancelResponse;
        }
    }

    internal sealed class ReleaseSessionService : IStripeSessionService
    {
        public Session Create(SessionCreateOptions options, RequestOptions requestOptions = null!) =>
            throw new NotImplementedException();

        public Session Get(string id) => throw new NotImplementedException();

        public Session Update(string id, SessionUpdateOptions options, RequestOptions requestOptions = null!) =>
            throw new NotImplementedException();
    }

    internal sealed class ReleaseEventFactory : IStripeEventFactory
    {
        public Event ConstructEvent(string payload, string signatureHeader, string webhookSecret, bool throwOnApiVersionMismatch = true) =>
            throw new NotImplementedException();
    }
}
