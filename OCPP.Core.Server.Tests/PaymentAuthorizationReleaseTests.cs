using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OCPP.Core.Database;
using OCPP.Core.Server.Payments;
using OCPP.Core.Server.Payments.Invoices;
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
            Assert.Equal("canceled", attempt.ProviderStatus);
            Assert.Equal(500, attempt.AmountCapturableCents);
            Assert.Equal(PaymentAuthorizationReleaseOutcome.Released, attempt.Outcome);
            Assert.Equal(Now, attempt.FinishedAtUtc);
        }

        [Fact]
        public void Reconcile_DoesNotClaimReleaseWhenProviderReturnsNoCancellationState()
        {
            using var context = CreateContext();
            var reservation = AddReservation(context);
            var intents = OwnedIntentService(reservation, "requires_capture", amountCapturable: 500);
            intents.CancelResponse = null!;
            var coordinator = CreateCoordinator(intents);

            var result = coordinator.ReconcileTerminalPaymentAuthorization(
                context,
                reservation,
                PaymentAuthorizationReleaseTrigger.CleanupSweep);

            Assert.Equal(PaymentAuthorizationReleaseOutcome.RetryScheduled, result.Outcome);
            Assert.Equal(PaymentAuthorizationReleaseState.RetryScheduled, reservation.AuthorizationReleaseState);
            Assert.Null(reservation.AuthorizationReleasedAtUtc);
            Assert.Contains("missing", reservation.AuthorizationReleaseLastError, StringComparison.OrdinalIgnoreCase);
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
        public void Reconcile_RequiresReviewWhenCanceledProviderIntentContainsReceivedFunds()
        {
            using var context = CreateContext();
            var reservation = AddReservation(context);
            var intents = OwnedIntentService(reservation, "canceled", amountCapturable: 0);
            intents.GetResponse.AmountReceived = 100;
            var coordinator = CreateCoordinator(intents);

            var result = coordinator.ReconcileTerminalPaymentAuthorization(
                context,
                reservation,
                PaymentAuthorizationReleaseTrigger.CleanupSweep);

            Assert.Equal(PaymentAuthorizationReleaseOutcome.ReviewRequired, result.Outcome);
            Assert.Equal(PaymentAuthorizationReleaseState.ReviewRequired, reservation.AuthorizationReleaseState);
            Assert.Equal(0, intents.CancelCalls);
            Assert.Contains("received", reservation.AuthorizationReleaseLastError, StringComparison.OrdinalIgnoreCase);
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
        public void Reconcile_ExcludesUnlinkedActiveTransactionUsingOcppTagBeforeProviderRead()
        {
            using var context = CreateContext();
            var reservation = AddReservation(context);
            reservation.OcppIdTag = "PAYMENT-OCPP-TAG";
            context.Transactions.Add(new Transaction
            {
                TransactionId = 43,
                ChargePointId = reservation.ChargePointId,
                ConnectorId = reservation.ConnectorId,
                StartTagId = reservation.OcppIdTag,
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
        public void Reconcile_RequiresReviewWhenProviderReportsAmountReceived()
        {
            using var context = CreateContext();
            var reservation = AddReservation(context);
            var intents = OwnedIntentService(reservation, "requires_capture", amountCapturable: 500);
            intents.GetResponse.AmountReceived = 100;
            var coordinator = CreateCoordinator(intents);

            var result = coordinator.ReconcileTerminalPaymentAuthorization(
                context,
                reservation,
                PaymentAuthorizationReleaseTrigger.CleanupSweep);

            Assert.Equal(PaymentAuthorizationReleaseOutcome.ReviewRequired, result.Outcome);
            Assert.Equal(PaymentAuthorizationReleaseState.ReviewRequired, reservation.AuthorizationReleaseState);
            Assert.Equal(0, intents.CancelCalls);
            Assert.Contains("received", reservation.AuthorizationReleaseLastError, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Reconcile_SkipsFreshInProgressLease()
        {
            using var context = CreateContext();
            var reservation = AddReservation(context);
            reservation.AuthorizationReleaseState = PaymentAuthorizationReleaseState.InProgress;
            reservation.AuthorizationReleaseAttemptCount = 1;
            reservation.AuthorizationReleaseLastAttemptAtUtc = Now;
            context.SaveChanges();
            var intents = OwnedIntentService(reservation, "requires_capture", amountCapturable: 500);
            var coordinator = CreateCoordinator(intents);

            var result = coordinator.ReconcileTerminalPaymentAuthorization(
                context,
                reservation,
                PaymentAuthorizationReleaseTrigger.AmountCapturableWebhook);

            Assert.Equal(PaymentAuthorizationReleaseOutcome.SkippedNotEligible, result.Outcome);
            Assert.Equal(PaymentAuthorizationReleaseState.InProgress, reservation.AuthorizationReleaseState);
            Assert.Equal(0, intents.GetCalls);
            Assert.Empty(context.PaymentAuthorizationReleaseAttempts);
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
            Assert.Equal(1, intents.GetCalls);
            Assert.Equal(0, intents.CancelCalls);
        }

        [Fact]
        public void Reconcile_FinalVerificationRecoversCanceledProviderStateAfterLastTransientFailure()
        {
            using var context = CreateContext();
            var reservation = AddReservation(context);
            reservation.AuthorizationReleaseAttemptCount = 1;
            context.SaveChanges();
            var intents = OwnedIntentService(reservation, "requires_capture", amountCapturable: 500);
            intents.CancelException = new StripeException(
                HttpStatusCode.ServiceUnavailable,
                new StripeError { Type = "api_error", Code = "timeout", Message = "provider timeout" },
                "provider timeout");
            var currentTime = Now;
            var coordinator = CreateCoordinator(
                intents,
                new Dictionary<string, string?>
                {
                    ["Maintenance:AuthorizationReleaseMaxAttempts"] = "2"
                },
                now: () => currentTime);

            var failedResult = coordinator.ReconcileTerminalPaymentAuthorization(
                context,
                reservation,
                PaymentAuthorizationReleaseTrigger.CleanupSweep);

            Assert.Equal(PaymentAuthorizationReleaseOutcome.RetryScheduled, failedResult.Outcome);
            Assert.Equal(2, reservation.AuthorizationReleaseAttemptCount);

            intents.CancelException = null;
            intents.GetResponse.Status = "canceled";
            intents.GetResponse.AmountCapturable = 0;
            currentTime = Now.AddMinutes(3);

            var verificationResult = coordinator.ReconcileTerminalPaymentAuthorization(
                context,
                reservation,
                PaymentAuthorizationReleaseTrigger.CleanupSweep);

            Assert.Equal(PaymentAuthorizationReleaseOutcome.AlreadyReleased, verificationResult.Outcome);
            Assert.Equal(PaymentAuthorizationReleaseState.Released, reservation.AuthorizationReleaseState);
            Assert.Equal(2, reservation.AuthorizationReleaseAttemptCount);
            Assert.Equal(1, intents.CancelCalls);

            var attempt = context.PaymentAuthorizationReleaseAttempts.Single();
            Assert.Equal(PaymentAuthorizationReleaseOutcome.AlreadyReleased, attempt.Outcome);
            Assert.Equal("canceled", attempt.ProviderStatus);
            Assert.Equal(currentTime, attempt.FinishedAtUtc);
            Assert.Null(attempt.NextRetryAtUtc);
        }

        [Fact]
        public void CheckoutCompletedWebhook_ReconcilesCleanupBeforeWebhookRace()
        {
            using var context = CreateContext();
            var reservation = AddReservation(context);
            reservation.StripeCheckoutSessionId = "sess_late";
            reservation.StripePaymentIntentId = null;
            context.SaveChanges();

            var intents = OwnedIntentService(reservation, "requires_capture", amountCapturable: 500);
            intents.GetResponse.Id = "pi_late";
            var sessions = new ReleaseSessionService
            {
                GetResponse = new Session
                {
                    Id = "sess_late",
                    Metadata = new Dictionary<string, string>
                    {
                        ["reservation_id"] = reservation.ReservationId.ToString()
                    }
                }
            };
            var stripeEvent = new Event
            {
                Id = "evt_checkout_late",
                Type = EventTypes.CheckoutSessionCompleted,
                Data = new EventData
                {
                    Object = new Session
                    {
                        Id = "sess_late",
                        PaymentIntentId = "pi_late",
                        PaymentStatus = "paid"
                    }
                }
            };
            var coordinator = CreateCoordinator(intents, stripeEvent: stripeEvent, sessions: sessions);

            var cleanupResult = coordinator.ReconcileTerminalPaymentAuthorization(
                context,
                reservation,
                PaymentAuthorizationReleaseTrigger.CleanupSweep);

            Assert.Equal(PaymentAuthorizationReleaseOutcome.RetryScheduled, cleanupResult.Outcome);
            Assert.Equal(PaymentAuthorizationReleaseState.RetryScheduled, reservation.AuthorizationReleaseState);
            Assert.Single(context.PaymentAuthorizationReleaseAttempts);

            coordinator.HandleWebhookEvent(context, "payload", "signature");

            Assert.Equal(PaymentReservationStatus.Abandoned, reservation.Status);
            Assert.Equal("pi_late", reservation.StripePaymentIntentId);
            Assert.Equal(PaymentAuthorizationReleaseState.Released, reservation.AuthorizationReleaseState);
            Assert.Equal(1, intents.CancelCalls);
            Assert.Equal(PaymentAuthorizationReleaseTrigger.CheckoutCompletedWebhook,
                context.PaymentAuthorizationReleaseAttempts.OrderBy(attempt => attempt.AttemptNumber).Last().Trigger);
            Assert.Equal(2, context.PaymentAuthorizationReleaseAttempts.Count());
            Assert.Single(context.StripeWebhookEvents);
        }

        [Fact]
        public void Reconcile_RecoversMissingWebhookByLinkingOwnedCheckoutSession()
        {
            using var context = CreateContext();
            var reservation = AddReservation(context);
            reservation.StripeCheckoutSessionId = "sess_missed_webhook";
            reservation.StripePaymentIntentId = null;
            context.SaveChanges();
            var intents = OwnedIntentService(reservation, "requires_capture", amountCapturable: 500);
            intents.GetResponse.Id = "pi_from_session";
            var sessions = new ReleaseSessionService
            {
                GetResponse = new Session
                {
                    Id = reservation.StripeCheckoutSessionId,
                    PaymentIntentId = "pi_from_session",
                    Metadata = new Dictionary<string, string>
                    {
                        ["reservation_id"] = reservation.ReservationId.ToString()
                    }
                }
            };
            var coordinator = CreateCoordinator(intents, sessions: sessions);

            var result = coordinator.ReconcileTerminalPaymentAuthorization(
                context,
                reservation,
                PaymentAuthorizationReleaseTrigger.CleanupSweep);

            Assert.Equal(PaymentAuthorizationReleaseOutcome.Released, result.Outcome);
            Assert.Equal("pi_from_session", reservation.StripePaymentIntentId);
            Assert.Equal(1, sessions.GetCalls);
            Assert.Equal(1, intents.CancelCalls);
        }

        [Fact]
        public void InvoiceLookup_StrictVariantReportsQueryFailureInsteadOfNoInvoice()
        {
            using var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            var options = new DbContextOptionsBuilder<OCPPCoreContext>().UseSqlite(connection).Options;
            using var context = new OCPPCoreContext(options);
            context.Database.EnsureCreated();
            connection.Close();

            var lookupSucceeded = InvoiceSubmissionLogLookup.TryHasSubmittedOrExternalInvoice(
                context,
                Guid.NewGuid(),
                NullLogger.Instance,
                "strict release test",
                out var hasInvoice);

            Assert.False(lookupSucceeded);
            Assert.False(hasInvoice);
        }

        [Fact]
        public void Reconcile_RequiresReviewWithoutProviderReadWhenInvoiceQueryFails()
        {
            using var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            var options = new DbContextOptionsBuilder<OCPPCoreContext>()
                .UseSqlite(connection)
                .AddInterceptors(new ThrowingInvoiceQueryInterceptor())
                .Options;
            using var context = new OCPPCoreContext(options);
            context.Database.EnsureCreated();
            var reservation = AddReservation(context);
            var intents = OwnedIntentService(reservation, "requires_capture", amountCapturable: 500);
            var coordinator = CreateCoordinator(intents);

            var result = coordinator.ReconcileTerminalPaymentAuthorization(
                context,
                reservation,
                PaymentAuthorizationReleaseTrigger.CleanupSweep);

            Assert.Equal(PaymentAuthorizationReleaseOutcome.ReviewRequired, result.Outcome);
            Assert.Equal(PaymentAuthorizationReleaseState.ReviewRequired, reservation.AuthorizationReleaseState);
            Assert.Contains("invoice exclusion", reservation.AuthorizationReleaseLastError, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, intents.GetCalls);
            Assert.Equal(0, intents.CancelCalls);
            Assert.Equal(
                PaymentAuthorizationReleaseOutcome.ReviewRequired,
                context.PaymentAuthorizationReleaseAttempts.Single().Outcome);
        }

        [Fact]
        public void CheckoutCompletedWebhook_PreservesNormalPendingAuthorizationFlow()
        {
            using var context = CreateContext();
            var reservation = AddReservation(context, armed: false, status: PaymentReservationStatus.Pending);
            reservation.StripeCheckoutSessionId = "sess_normal";
            reservation.StripePaymentIntentId = null;
            context.SaveChanges();
            var intents = new ReleasePaymentIntentService();
            var stripeEvent = new Event
            {
                Id = "evt_checkout_normal",
                Type = EventTypes.CheckoutSessionCompleted,
                Data = new EventData
                {
                    Object = new Session
                    {
                        Id = "sess_normal",
                        PaymentIntentId = "pi_normal",
                        PaymentStatus = "paid"
                    }
                }
            };
            var coordinator = CreateCoordinator(intents, stripeEvent: stripeEvent);

            coordinator.HandleWebhookEvent(context, "payload", "signature");

            Assert.Equal(PaymentReservationStatus.Authorized, reservation.Status);
            Assert.Equal("pi_normal", reservation.StripePaymentIntentId);
            Assert.Null(reservation.AuthorizationReleaseState);
            Assert.Equal(0, intents.GetCalls);
            Assert.Equal(0, intents.CancelCalls);
        }

        [Fact]
        public void AmountCapturableWebhook_ReconcilesArmedTerminalReservation()
        {
            using var context = CreateContext();
            var reservation = AddReservation(context);
            var intents = OwnedIntentService(reservation, "requires_capture", amountCapturable: 500);
            var stripeEvent = new Event
            {
                Id = "evt_capturable",
                Type = "payment_intent.amount_capturable_updated",
                Data = new EventData
                {
                    Object = new PaymentIntent
                    {
                        Id = reservation.StripePaymentIntentId,
                        Status = "requires_capture",
                        AmountCapturable = 500,
                        Metadata = new Dictionary<string, string>
                        {
                            ["reservation_id"] = reservation.ReservationId.ToString()
                        }
                    }
                }
            };
            var coordinator = CreateCoordinator(intents, stripeEvent: stripeEvent);

            coordinator.HandleWebhookEvent(context, "payload", "signature");

            Assert.Equal(PaymentAuthorizationReleaseState.Released, reservation.AuthorizationReleaseState);
            Assert.Equal(1, intents.CancelCalls);
            Assert.Equal(PaymentAuthorizationReleaseTrigger.AmountCapturableWebhook,
                context.PaymentAuthorizationReleaseAttempts.Single().Trigger);
        }

        [Fact]
        public void AmountCapturableWebhook_BeforeCleanupDoesNotReleaseActiveReservation()
        {
            using var context = CreateContext();
            var reservation = AddReservation(context, armed: false, status: PaymentReservationStatus.Pending);
            var intents = OwnedIntentService(reservation, "requires_capture", amountCapturable: 500);
            var stripeEvent = new Event
            {
                Id = "evt_capturable_early",
                Type = "payment_intent.amount_capturable_updated",
                Data = new EventData
                {
                    Object = intents.GetResponse
                }
            };
            var coordinator = CreateCoordinator(intents, stripeEvent: stripeEvent);

            coordinator.HandleWebhookEvent(context, "payload", "signature");

            Assert.Equal(PaymentReservationStatus.Pending, reservation.Status);
            Assert.Null(reservation.AuthorizationReleaseState);
            Assert.Equal(0, intents.GetCalls);
            Assert.Equal(0, intents.CancelCalls);
        }

        [Fact]
        public void AmountCapturableWebhook_DuplicateDeliveryDoesNotRepeatRelease()
        {
            using var context = CreateContext();
            var reservation = AddReservation(context);
            var intents = OwnedIntentService(reservation, "requires_capture", amountCapturable: 500);
            var stripeEvent = new Event
            {
                Id = "evt_capturable_duplicate",
                Type = "payment_intent.amount_capturable_updated",
                Data = new EventData { Object = intents.GetResponse }
            };
            var coordinator = CreateCoordinator(intents, stripeEvent: stripeEvent);

            coordinator.HandleWebhookEvent(context, "payload", "signature");
            coordinator.HandleWebhookEvent(context, "payload", "signature");

            Assert.Equal(1, intents.CancelCalls);
            Assert.Single(context.PaymentAuthorizationReleaseAttempts);
            Assert.Single(context.StripeWebhookEvents);
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
            IDictionary<string, string?>? settings = null,
            Event? stripeEvent = null,
            ReleaseSessionService? sessions = null,
            Func<DateTime>? now = null)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(settings ?? new Dictionary<string, string?>())
                .Build();

            return new StripePaymentCoordinator(
                Options.Create(new StripeOptions
                {
                    Enabled = true,
                    ApiKey = "test",
                    ReturnBaseUrl = "https://return",
                    WebhookSecret = "whsec_test"
                }),
                Options.Create(new PaymentFlowOptions()),
                NullLogger<StripePaymentCoordinator>.Instance,
                sessions ?? new ReleaseSessionService(),
                intents,
                new ReleaseEventFactory { EventToReturn = stripeEvent ?? new Event() },
                now ?? (() => Now),
                configuration: configuration);
        }
    }

    internal sealed class ThrowingInvoiceQueryInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public override Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<DbDataReader> result)
        {
            if (command.CommandText.Contains("InvoiceSubmissionLog", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Simulated invoice query failure.");
            }

            return result;
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
            if (CancelResponse != null)
            {
                CancelResponse.Id = id;
            }
            return CancelResponse!;
        }
    }

    internal sealed class ReleaseSessionService : IStripeSessionService
    {
        public Session? GetResponse { get; set; }
        public StripeException? GetException { get; set; }
        public int GetCalls { get; private set; }

        public Session Create(SessionCreateOptions options, RequestOptions requestOptions = null!) =>
            throw new NotImplementedException();

        public Session Get(string id)
        {
            GetCalls++;
            if (GetException != null) throw GetException;
            return GetResponse!;
        }

        public Session Update(string id, SessionUpdateOptions options, RequestOptions requestOptions = null!) =>
            throw new NotImplementedException();
    }

    internal sealed class ReleaseEventFactory : IStripeEventFactory
    {
        public Event EventToReturn { get; set; } = new();

        public Event ConstructEvent(string payload, string signatureHeader, string webhookSecret, bool throwOnApiVersionMismatch = true) =>
            EventToReturn;
    }
}
