using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OCPP.Core.Database;
using OCPP.Core.Server.Payments;
using OCPP.Core.Server.Payments.Providers;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    /// <summary>
    /// Cancel and start-timeout claims that arm the authorization release, settlement retries (outcome messages,
    /// an already cancelled hold, concurrent runs, retry-scoped idempotency keys) and relinking during a retry.
    /// </summary>
    public partial class PaymentCoordinatorProviderTests
    {
        // ---- Cancel and start-timeout claims arm the release ----

        [Fact]
        public void Cancel_ConfirmedProviderCancelMarksTheReleaseReleased()
        {
            using var context = CreateContext();
            var reservation = AddAuthorizedReservation(context, "fake_tx_cancel_confirmed");
            var fake = new FakePaymentGateway
            {
                AuthorizationResponse = Authorization(reservation.ReservationId, "fake_tx_cancel_confirmed", PaymentAuthorizationState.RequiresCapture, 450)
            };
            var coordinator = CreateCoordinator(fake);

            coordinator.CancelReservation(context, reservation.ReservationId, "Stopped by user before charging started.");

            Assert.Equal(PaymentReservationStatus.Cancelled, reservation.Status);
            Assert.Equal("Stopped by user before charging started.", reservation.LastError);
            Assert.Equal(PaymentAuthorizationReleaseState.Released, reservation.AuthorizationReleaseState);
            Assert.NotNull(reservation.AuthorizationReleasedAtUtc);
            Assert.Equal("fake_tx_cancel_confirmed", Assert.Single(fake.Cancels).Reference);
        }

        [Fact]
        public void Cancel_FailureAfterTheClaimLeavesAnArmedReleaseThatTheReconciliationFinishes()
        {
            using var database = new SqliteRaceDatabase();
            Guid reservationId;
            using (var setup = database.NewContext())
            {
                reservationId = AddAuthorizedReservation(setup, "fake_tx_claim_failure").ReservationId;
            }

            var fake = new FakePaymentGateway
            {
                AuthorizationResponse = Authorization(reservationId, "fake_tx_claim_failure", PaymentAuthorizationState.RequiresCapture, 450),
                // The provider does not confirm the cancel, and every later save of the reservation fails (a
                // database failure, or the process stopping, after the claim).
                CancelResponse = Authorization(reservationId, "fake_tx_claim_failure", PaymentAuthorizationState.RequiresCapture, 450)
            };
            var coordinator = CreateRaceCoordinator(fake);
            using (var context = database.NewContext(new FailReservationSaveInterceptor()))
            {
                Assert.Throws<DbUpdateException>(() =>
                    coordinator.CancelReservation(context, reservationId, "Stopped by user before charging started."));
            }

            using (var verification = database.NewContext())
            {
                var persisted = verification.ChargePaymentReservations.Single();
                Assert.Equal(PaymentReservationStatus.Cancelled, persisted.Status);
                Assert.Equal(PaymentAuthorizationReleaseState.Pending, persisted.AuthorizationReleaseState);
                // The first attempt waits one in-progress timeout, so the sweep cannot race the request's own cancel.
                Assert.Equal(Now.AddMinutes(5), persisted.AuthorizationReleaseNextAttemptAtUtc);
                Assert.Equal(0, persisted.AuthorizationReleaseAttemptCount);
            }

            // The cleanup sweep's release reconciliation finishes the release once that time has passed.
            fake.CancelResponse = null;
            var sweepCoordinator = CreateRaceCoordinator(fake, () => Now.AddMinutes(6));
            using (var sweepContext = database.NewContext())
            {
                var early = coordinator.ReconcileTerminalPaymentAuthorization(
                    sweepContext,
                    sweepContext.ChargePaymentReservations.Single(),
                    PaymentAuthorizationReleaseTrigger.CleanupSweep);
                Assert.Equal(PaymentAuthorizationReleaseOutcome.SkippedNotEligible, early.Outcome);
            }

            using (var sweepContext = database.NewContext())
            {
                var result = sweepCoordinator.ReconcileTerminalPaymentAuthorization(
                    sweepContext,
                    sweepContext.ChargePaymentReservations.Single(),
                    PaymentAuthorizationReleaseTrigger.CleanupSweep);
                Assert.Equal(PaymentAuthorizationReleaseOutcome.Released, result.Outcome);
            }

            Assert.Equal(2, fake.Cancels.Count);
            using var final = database.NewContext();
            Assert.Equal(PaymentAuthorizationReleaseState.Released, final.ChargePaymentReservations.Single().AuthorizationReleaseState);
        }

        [Fact]
        public async Task StartTimeout_FailureAfterTheClaimLeavesAnArmedReleaseForTheNextSweep()
        {
            using var database = new SqliteRaceDatabase();
            Guid reservationId;
            using (var setup = database.NewContext())
            {
                var seeded = AddAuthorizedReservation(setup, "fake_tx_timeout_claim");
                seeded.AuthorizedAtUtc = Now.AddMinutes(-8);
                seeded.StartDeadlineAtUtc = Now.AddMinutes(-1);
                setup.SaveChanges();
                reservationId = seeded.ReservationId;
            }

            var fake = new FakePaymentGateway
            {
                AuthorizationResponse = Authorization(reservationId, "fake_tx_timeout_claim", PaymentAuthorizationState.RequiresCapture, 450),
                CancelResponse = Authorization(reservationId, "fake_tx_timeout_claim", PaymentAuthorizationState.RequiresCapture, 450)
            };
            var coordinator = CreateRaceCoordinator(fake);
            using (var failingProvider = database.BuildSweepProvider(coordinator, new FailReservationSaveInterceptor()))
            {
                var failingSweep = new CleanupServiceHarness(failingProvider.GetRequiredService<IServiceScopeFactory>(), failingProvider.GetRequiredService<IConfiguration>(), utcNow: Now);
                await Assert.ThrowsAsync<DbUpdateException>(() => failingSweep.RunOnce());
            }

            using (var verification = database.NewContext())
            {
                var persisted = verification.ChargePaymentReservations.Single();
                Assert.Equal(PaymentReservationStatus.StartTimeout, persisted.Status);
                Assert.Equal(PaymentAuthorizationReleaseState.Pending, persisted.AuthorizationReleaseState);
            }

            fake.CancelResponse = null;
            using (var provider = database.BuildSweepProvider(coordinator))
            {
                var sweep = new CleanupServiceHarness(provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<IConfiguration>(), utcNow: Now.AddMinutes(1));
                await sweep.RunOnce();
            }

            using var final = database.NewContext();
            var released = final.ChargePaymentReservations.Single();
            Assert.Equal(PaymentReservationStatus.StartTimeout, released.Status);
            Assert.Equal(PaymentAuthorizationReleaseState.Released, released.AuthorizationReleaseState);
            Assert.Equal(2, fake.Cancels.Count);
        }

        [Fact]
        public async Task StartTimeout_ConfirmedCancelMarksTheReleaseReleased()
        {
            using var database = new SqliteRaceDatabase();
            Guid reservationId;
            using (var setup = database.NewContext())
            {
                var seeded = AddAuthorizedReservation(setup, "fake_tx_timeout_confirmed");
                seeded.AuthorizedAtUtc = Now.AddMinutes(-8);
                seeded.StartDeadlineAtUtc = Now.AddMinutes(-1);
                setup.SaveChanges();
                reservationId = seeded.ReservationId;
            }

            var fake = new FakePaymentGateway
            {
                AuthorizationResponse = Authorization(reservationId, "fake_tx_timeout_confirmed", PaymentAuthorizationState.RequiresCapture, 450)
            };
            var coordinator = CreateRaceCoordinator(fake);
            using (var provider = database.BuildSweepProvider(coordinator))
            {
                var sweep = new CleanupServiceHarness(provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<IConfiguration>(), utcNow: Now);
                await sweep.RunOnce();
            }

            using var verification = database.NewContext();
            var persisted = verification.ChargePaymentReservations.Single();
            Assert.Equal(PaymentReservationStatus.StartTimeout, persisted.Status);
            Assert.Equal("Start window expired without transaction.", persisted.LastError);
            Assert.Equal(PaymentAuthorizationReleaseState.Released, persisted.AuthorizationReleaseState);
            Assert.Single(fake.Cancels);
        }

        // ---- Settlement retries ----

        [Fact]
        public void Complete_RetriedNoChargeSettlementThatFindsTheHoldCancelledEndsReleased()
        {
            using var context = CreateContext();
            var reservation = AddChargingReservation(context, FakeProvider, "fake_tx_retry_cancelled");
            var transaction = AddStoppedTransaction(context, reservation, meterStopKwh: 0);
            var clock = Now;
            var fake = new FakePaymentGateway
            {
                AuthorizationResponse = Authorization(reservation.ReservationId, "fake_tx_retry_cancelled", PaymentAuthorizationState.RequiresCapture, 450),
                // The no-charge cancel is applied, but its answer is lost.
                CancelException = new PaymentProviderException(FakeProvider, "api_error", "Service unavailable", isTransient: true, httpStatusCode: 503)
            };
            var coordinator = CreateCoordinator(fake, now: () => clock);

            coordinator.CompleteReservation(context, transaction);
            Assert.Equal(PaymentReservationStatus.Charging, reservation.Status);
            Assert.Equal("settlement_retry_pending", reservation.FailureCode);

            clock = Now.AddMinutes(2);
            fake.CancelException = null;
            fake.AuthorizationResponse = Authorization(reservation.ReservationId, "fake_tx_retry_cancelled", PaymentAuthorizationState.Canceled, 450);
            coordinator.CompleteReservation(context, transaction);

            Assert.Equal(PaymentReservationStatus.Cancelled, reservation.Status);
            Assert.Equal(0, reservation.CapturedAmountCents);
            Assert.Null(reservation.FailureCode);
            Assert.Equal("No billable amount to capture; the payment authorization was already released.", reservation.LastError);
            Assert.Equal(PaymentAuthorizationReleaseState.Released, reservation.AuthorizationReleaseState);
            Assert.NotNull(reservation.AuthorizationReleasedAtUtc);
            Assert.Empty(fake.Cancels);
            Assert.Empty(fake.Captures);
        }

        [Fact]
        public void Complete_RetriedZeroAmountCancelKeepsItsOutcomeMessage()
        {
            using var context = CreateContext();
            var reservation = AddChargingReservation(context, FakeProvider, "fake_tx_retry_message");
            var transaction = AddStoppedTransaction(context, reservation, meterStopKwh: 0);
            var clock = Now;
            var fake = new FakePaymentGateway
            {
                GetAuthorizationException = new PaymentProviderException(FakeProvider, "inquire_not_found", "Transaction not found", isTransient: true)
            };
            var coordinator = CreateCoordinator(fake, now: () => clock);

            coordinator.CompleteReservation(context, transaction);
            Assert.Equal("settlement_retry_pending", reservation.FailureCode);

            clock = Now.AddMinutes(2);
            fake.GetAuthorizationException = null;
            fake.AuthorizationResponse = Authorization(reservation.ReservationId, "fake_tx_retry_message", PaymentAuthorizationState.RequiresCapture, 450);
            coordinator.CompleteReservation(context, transaction);

            Assert.Equal(PaymentReservationStatus.Cancelled, reservation.Status);
            Assert.Null(reservation.FailureCode);
            Assert.StartsWith("No billable amount to capture", reservation.LastError);
            var cancel = Assert.Single(fake.Cancels);
            Assert.Equal($"cancel:{reservation.ReservationId}:r7", cancel.IdempotencyKey);
        }

        [Fact]
        public void Complete_SettlementRetriesUseRetryScopedIdempotencyKeys()
        {
            using var context = CreateContext();
            var reservation = AddChargingReservation(context, FakeProvider, "fake_tx_retry_key");
            var transaction = AddStoppedTransaction(context, reservation, meterStopKwh: 2);
            var clock = Now;
            var fake = new FakePaymentGateway
            {
                AuthorizationResponse = Authorization(reservation.ReservationId, "fake_tx_retry_key", PaymentAuthorizationState.RequiresCapture, 450)
            };
            var gateway = new FailingCaptureGateway(fake)
            {
                // A 5xx after the capture started: Stripe stores it for the key and replays it for that key.
                CaptureException = new PaymentProviderException(FakeProvider, "api_error", "Internal server error", isTransient: true, httpStatusCode: 500)
            };
            var coordinator = CreateRaceCoordinator(gateway, () => clock);

            coordinator.CompleteReservation(context, transaction);
            clock = Now.AddMinutes(2);
            coordinator.CompleteReservation(context, transaction);
            Assert.Equal("settlement_retry_pending", reservation.FailureCode);

            clock = Now.AddMinutes(4);
            gateway.CaptureException = null;
            coordinator.CompleteReservation(context, transaction);

            Assert.Equal(PaymentReservationStatus.Completed, reservation.Status);
            Assert.Null(reservation.FailureCode);
            Assert.Null(reservation.LastError);
            Assert.Equal(130, reservation.CapturedAmountCents);
            Assert.Equal(
                new[]
                {
                    $"capture:{reservation.ReservationId}:130",
                    $"capture:{reservation.ReservationId}:130:r7",
                    $"capture:{reservation.ReservationId}:130:r9"
                },
                gateway.CaptureKeys);
            Assert.Single(fake.Captures);
        }

        [Fact]
        public void Race_ConcurrentSettlementFailureDoesNotStayOnTheCompletedReservation()
        {
            using var database = new SqliteRaceDatabase();
            using (var setup = database.NewContext())
            {
                setup.ChargePoints.Add(new ChargePoint { ChargePointId = "CP1" });
                setup.SaveChanges();
                var seeded = AddChargingReservation(setup, FakeProvider, "fake_tx_concurrent_settlement");
                AddStoppedTransaction(setup, seeded, meterStopKwh: 2);
            }

            using var contextA = database.NewContext();
            using var contextB = database.NewContext();
            var reservationId = contextA.ChargePaymentReservations.Single().ReservationId;
            var fakeB = new FakePaymentGateway
            {
                AuthorizationResponse = Authorization(reservationId, "fake_tx_concurrent_settlement", PaymentAuthorizationState.RequiresCapture, 450)
            };
            var gatewayB = new FailingCaptureGateway(fakeB)
            {
                CaptureException = new PaymentProviderException(FakeProvider, "idempotency_key_in_use", "Conflict", isTransient: true, httpStatusCode: 409)
            };
            var coordinatorB = CreateRaceCoordinator(gatewayB);
            var fakeA = new FakePaymentGateway
            {
                AuthorizationResponse = Authorization(reservationId, "fake_tx_concurrent_settlement", PaymentAuthorizationState.RequiresCapture, 450)
            };
            // The second settlement (for example the sweep's recovery) fails transiently while the first captures.
            var gatewayA = new FailingCaptureGateway(fakeA)
            {
                BeforeCapture = () => coordinatorB.CompleteReservation(contextB, contextB.Transactions.Single())
            };
            var coordinatorA = CreateRaceCoordinator(gatewayA);

            coordinatorA.CompleteReservation(contextA, contextA.Transactions.Single());

            using var verification = database.NewContext();
            var persisted = verification.ChargePaymentReservations.Single();
            Assert.Equal(PaymentReservationStatus.Completed, persisted.Status);
            Assert.Null(persisted.FailureCode);
            Assert.Null(persisted.LastError);
            Assert.Null(persisted.AuthorizationReleaseState);
            Assert.Equal(130, persisted.CapturedAmountCents);
            Assert.Single(fakeA.Captures);
            Assert.Empty(fakeB.Captures);
        }

        // ---- A new transaction with the same tag does not take over a settling reservation ----

        [Theory]
        [InlineData(PaymentReservationStatus.Charging, "settlement_retry_pending")]
        [InlineData(PaymentReservationStatus.WaitingForDisconnect, "settlement_retry_pending")]
        public void MarkTransactionStarted_DoesNotRelinkAReservationWhoseTransactionStopped(string status, string? failureCode)
        {
            using var context = CreateContext();
            var reservation = AddChargingReservation(context, FakeProvider, "fake_tx_no_relink");
            var stopped = AddStoppedTransaction(context, reservation, meterStopKwh: 2);
            reservation.Status = status;
            reservation.FailureCode = failureCode;
            context.SaveChanges();
            context.Transactions.Add(new Transaction
            {
                TransactionId = 89,
                ChargePointId = reservation.ChargePointId,
                ConnectorId = reservation.ConnectorId,
                StartTagId = reservation.ChargeTagId,
                StartTime = Now,
                MeterStart = 2
            });
            context.SaveChanges();
            var coordinator = CreateCoordinator(new FakePaymentGateway());

            coordinator.MarkTransactionStarted(context, reservation.ChargePointId, reservation.ConnectorId, reservation.ChargeTagId, 89);

            Assert.Equal(stopped.TransactionId, reservation.TransactionId);
            Assert.Equal(status, reservation.Status);
            Assert.Equal(failureCode, reservation.FailureCode);
        }

        // ---- helpers ----

        private static ChargePaymentReservation AddAuthorizedReservation(OCPPCoreContext context, string authorizationReference)
        {
            var reservation = AddPendingReservation(context, FakeProvider, $"{authorizationReference}_checkout");
            reservation.Status = PaymentReservationStatus.Authorized;
            reservation.StripePaymentIntentId = authorizationReference;
            reservation.OcppIdTag = "WEBCLAIM";
            reservation.AuthorizedAtUtc = Now.AddMinutes(-1);
            reservation.StartDeadlineAtUtc = Now.AddMinutes(6);
            context.SaveChanges();
            return reservation;
        }

        private static PaymentCoordinator CreateRaceCoordinator(IPaymentGateway gateway, Func<DateTime> now) =>
            new(
                new PaymentGatewayRegistry(new[] { gateway }, gateway.ProviderName),
                Settings(),
                Microsoft.Extensions.Options.Options.Create(new PaymentFlowOptions { StartWindowMinutes = 7 }),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<PaymentCoordinator>.Instance,
                now);

        /// <summary>Fails every save that changes a reservation (a database failure or a stop after the claim).</summary>
        private sealed class FailReservationSaveInterceptor : SaveChangesInterceptor
        {
            public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
            {
                ThrowWhenReservationChanges(eventData);
                return base.SavingChanges(eventData, result);
            }

            public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
            {
                ThrowWhenReservationChanges(eventData);
                return base.SavingChangesAsync(eventData, result, cancellationToken);
            }

            private static void ThrowWhenReservationChanges(DbContextEventData eventData)
            {
                if (eventData.Context!.ChangeTracker.Entries<ChargePaymentReservation>().Any(entry => entry.State == EntityState.Modified))
                {
                    throw new DbUpdateException("Simulated database failure after the claim.");
                }
            }
        }

        /// <summary>Records capture keys, can fail captures, and can run an action once before the next capture.</summary>
        private sealed class FailingCaptureGateway : IPaymentGateway
        {
            private readonly FakePaymentGateway _inner;

            public FailingCaptureGateway(FakePaymentGateway inner)
            {
                _inner = inner;
            }

            public Action? BeforeCapture { get; set; }
            public Exception? CaptureException { get; set; }
            public List<string> CaptureKeys { get; } = new();
            public string ProviderName => _inner.ProviderName;
            public bool IsConfigured => _inner.IsConfigured;
            public string? SuccessUrlSessionPlaceholder => _inner.SuccessUrlSessionPlaceholder;
            public bool SupportsMetadata => _inner.SupportsMetadata;
            public bool SupportsDeferredCheckoutLookup => _inner.SupportsDeferredCheckoutLookup;
            public TimeSpan? CheckoutLifetime => _inner.CheckoutLifetime;
            public bool CapturedCheckoutRequiresRefund => _inner.CapturedCheckoutRequiresRefund;
            public PaymentCheckoutSession CreateCheckout(PaymentCheckoutRequest request) => _inner.CreateCheckout(request);
            public PaymentCheckoutStatus GetCheckout(string sessionReference) => _inner.GetCheckout(sessionReference);
            public PaymentAuthorizationSnapshot GetAuthorization(string authorizationReference) => _inner.GetAuthorization(authorizationReference);
            public PaymentAuthorizationSnapshot? FindAuthorizationByReservation(Guid reservationId) => _inner.FindAuthorizationByReservation(reservationId);

            public PaymentAuthorizationSnapshot Capture(string authorizationReference, long amountMinor, string currency, string idempotencyKey)
            {
                CaptureKeys.Add(idempotencyKey);
                var hook = BeforeCapture;
                BeforeCapture = null;
                hook?.Invoke();
                if (CaptureException != null) throw CaptureException;
                return _inner.Capture(authorizationReference, amountMinor, currency, idempotencyKey);
            }

            public PaymentAuthorizationSnapshot Cancel(string authorizationReference, string idempotencyKey) => _inner.Cancel(authorizationReference, idempotencyKey);
            public IReadOnlyDictionary<string, string> MirrorMetadata(string sessionReference, string fallbackAuthorizationReference, Action<IDictionary<string, string>> mutate) =>
                _inner.MirrorMetadata(sessionReference, fallbackAuthorizationReference, mutate);
            public PaymentWebhookParseResult ParseWebhook(PaymentWebhookRequest request) => _inner.ParseWebhook(request);
            public string SanitizeProviderText(string text) => _inner.SanitizeProviderText(text);
        }
    }
}
