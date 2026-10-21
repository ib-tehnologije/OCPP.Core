using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OCPP.Core.Database;
using OCPP.Core.Server.Payments;
using OCPP.Core.Server.Payments.Providers;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    /// <summary>
    /// Two requests acting on one reservation at the same time, on a real (SQLite) database with one context per
    /// request. The money-safe outcomes: the reservation is either authorized and its hold is not cancelled at the
    /// provider, or it ended and its hold is released; never authorized on a cancelled hold.
    /// </summary>
    public partial class PaymentCoordinatorProviderTests
    {
        [Fact]
        public void Race_StaleSweepEndsTheReservationWhileConfirmWaitsForTheProvider_ConfirmDoesNotAuthorize()
        {
            using var database = new SqliteRaceDatabase();
            using var confirmContext = database.NewContext();
            var reservation = AddStalePendingReservation(confirmContext, "fake_token_sweep_first");
            var fake = FakePaymentGateway.SaferpayLike();
            fake.CheckoutStatus = CompletedCheckout(reservation.ReservationId, "fake_token_sweep_first", "fake_tx_sweep_first", inlineAuthorization: true, amountMinor: 450);
            var gateway = new HookedGateway(fake);
            var coordinator = CreateRaceCoordinator(gateway);
            using var provider = database.BuildSweepProvider(coordinator);
            var sweep = new CleanupServiceHarness(provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<IConfiguration>(), utcNow: Now);
            // The payer finished the page just as the reservation became stale: the sweep runs while the browser
            // return is waiting for the provider's answer.
            gateway.AfterNextGetCheckout = () => sweep.RunOnce().GetAwaiter().GetResult();

            var confirmation = coordinator.ConfirmReservation(confirmContext, reservation.ReservationId, null);

            Assert.False(confirmation.Success);
            Assert.Equal(PaymentReservationStatus.Abandoned, confirmation.Status);
            using var verification = database.NewContext();
            var persisted = verification.ChargePaymentReservations.Single();
            Assert.Equal(PaymentReservationStatus.Abandoned, persisted.Status);
            Assert.Equal(PaymentAuthorizationReleaseState.Released, persisted.AuthorizationReleaseState);
            Assert.Null(persisted.AuthorizedAtUtc);
            Assert.True(string.IsNullOrEmpty(persisted.OcppIdTag));
            Assert.Equal("fake_tx_sweep_first", Assert.Single(fake.Cancels).Reference);
            Assert.Empty(verification.ChargeTags);
        }

        [Fact]
        public async Task Race_ConfirmAuthorizesBetweenTheSweepsReadAndItsWrite_SweepLeavesTheReservation()
        {
            using var database = new SqliteRaceDatabase();
            using var confirmContext = database.NewContext();
            var reservation = AddStalePendingReservation(confirmContext, "fake_token_confirm_first");
            confirmContext.ChangeTracker.Clear();
            var fake = FakePaymentGateway.SaferpayLike();
            fake.CheckoutStatus = CompletedCheckout(reservation.ReservationId, "fake_token_confirm_first", "fake_tx_confirm_first", inlineAuthorization: true, amountMinor: 450);
            var coordinator = CreateRaceCoordinator(fake);
            var interceptor = new BeforeReservationUpdateInterceptor();
            using var provider = database.BuildSweepProvider(coordinator, interceptor);
            PaymentConfirmationResult? confirmation = null;
            interceptor.Action = () => confirmation = coordinator.ConfirmReservation(confirmContext, reservation.ReservationId, null);
            var sweep = new CleanupServiceHarness(provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<IConfiguration>(), utcNow: Now);

            await sweep.RunOnce();

            Assert.True(interceptor.Fired);
            Assert.NotNull(confirmation);
            Assert.True(confirmation!.Success);
            using var verification = database.NewContext();
            var persisted = verification.ChargePaymentReservations.Single();
            Assert.Equal(PaymentReservationStatus.Authorized, persisted.Status);
            Assert.Null(persisted.AuthorizationReleaseState);
            Assert.False(string.IsNullOrEmpty(persisted.OcppIdTag));
            Assert.Equal(persisted.OcppIdTag, verification.ChargeTags.Single().TagId);
            Assert.Empty(fake.Cancels);
        }

        [Fact]
        public void Race_CancelWhileConfirmWaitsForTheProvider_ConfirmDoesNotAuthorizeTheReleasedHold()
        {
            using var database = new SqliteRaceDatabase();
            using var confirmContext = database.NewContext();
            using var cancelContext = database.NewContext();
            var reservation = AddPendingReservation(confirmContext, FakeProvider, "fake_token_cancel_race");
            var fake = FakePaymentGateway.SaferpayLike();
            fake.CheckoutStatus = CompletedCheckout(reservation.ReservationId, "fake_token_cancel_race", "fake_tx_cancel_race", inlineAuthorization: true, amountMinor: 450);
            var gateway = new HookedGateway(fake);
            var coordinator = CreateRaceCoordinator(gateway);
            // The payer cancels the earlier attempt on the start page while the paid page returns in another tab.
            gateway.AfterNextGetCheckout = () => coordinator.CancelReservation(cancelContext, reservation.ReservationId, "checkout_cancelled");

            var confirmation = coordinator.ConfirmReservation(confirmContext, reservation.ReservationId, null);

            Assert.False(confirmation.Success);
            Assert.Equal(PaymentReservationStatus.Cancelled, confirmation.Status);
            using var verification = database.NewContext();
            var persisted = verification.ChargePaymentReservations.Single();
            Assert.Equal(PaymentReservationStatus.Cancelled, persisted.Status);
            Assert.Equal(PaymentAuthorizationReleaseState.Released, persisted.AuthorizationReleaseState);
            Assert.Equal("fake_tx_cancel_race", persisted.StripePaymentIntentId);
            Assert.True(string.IsNullOrEmpty(persisted.OcppIdTag));
            var cancel = Assert.Single(fake.Cancels);
            Assert.Equal($"cancel:{reservation.ReservationId}", cancel.IdempotencyKey);
            Assert.Empty(verification.ChargeTags);
        }

        [Fact]
        public async Task Race_CancelWhileTheNotificationWaitsForTheProvider_NotificationDoesNotAuthorizeOrStart()
        {
            using var database = new SqliteRaceDatabase();
            using var notifyContext = database.NewContext();
            using var cancelContext = database.NewContext();
            var reservation = AddPendingReservation(notifyContext, FakeProvider, "fake_token_notify_race");
            var fake = FakePaymentGateway.SaferpayLike();
            fake.CheckoutStatus = CompletedCheckout(reservation.ReservationId, "fake_token_notify_race", "fake_tx_notify_race", inlineAuthorization: true, amountMinor: 450);
            fake.ParseResult = Notification(reservation.ReservationId, eventId: null);
            var gateway = new HookedGateway(fake);
            var coordinator = CreateRaceCoordinator(gateway);
            gateway.AfterNextGetCheckout = () => coordinator.CancelReservation(cancelContext, reservation.ReservationId, "checkout_cancelled");

            var result = await coordinator.HandleWebhookAsync(notifyContext, NotifyRequest(), CancellationToken.None);

            Assert.Equal(200, result.StatusCode);
            Assert.Empty(result.AuthorizedReservationIds);
            using var verification = database.NewContext();
            var persisted = verification.ChargePaymentReservations.Single();
            Assert.Equal(PaymentReservationStatus.Cancelled, persisted.Status);
            Assert.Equal(PaymentAuthorizationReleaseState.Released, persisted.AuthorizationReleaseState);
            Assert.True(string.IsNullOrEmpty(persisted.OcppIdTag));
            Assert.Single(fake.Cancels);
            Assert.Empty(verification.ChargeTags);
        }

        [Fact]
        public void Race_ReconcileDecidesOnTheCurrentRowNotOnACopyLoadedBefore()
        {
            using var database = new SqliteRaceDatabase();
            using var staleContext = database.NewContext();
            var reservation = AddReleaseReservation(staleContext, FakeProvider, authorizationReference: "fake_tx_reload");
            var fake = new FakePaymentGateway
            {
                AuthorizationResponse = Authorization(reservation.ReservationId, "fake_tx_reload", PaymentAuthorizationState.RequiresCapture, 450)
            };
            var coordinator = CreateRaceCoordinator(fake);
            using (var otherContext = database.NewContext())
            {
                // Another sweep (or a provider signal) released the hold after this copy was read.
                var current = otherContext.ChargePaymentReservations.Single();
                Assert.Equal(PaymentAuthorizationReleaseOutcome.Released, coordinator.ReconcileTerminalPaymentAuthorization(otherContext, current, PaymentAuthorizationReleaseTrigger.CleanupSweep).Outcome);
            }

            var result = coordinator.ReconcileTerminalPaymentAuthorization(staleContext, reservation, PaymentAuthorizationReleaseTrigger.CleanupSweep);

            Assert.Equal(PaymentAuthorizationReleaseOutcome.SkippedNotEligible, result.Outcome);
            Assert.Equal(PaymentAuthorizationReleaseState.Released, reservation.AuthorizationReleaseState);
            Assert.Single(fake.Cancels);
            using var verification = database.NewContext();
            Assert.Single(verification.PaymentAuthorizationReleaseAttempts);
        }

        [Fact]
        public void Race_TwoReconciliationsOfOneRow_OnlyTheLeaseHolderCallsTheProvider()
        {
            using var database = new SqliteRaceDatabase();
            var interceptor = new BeforeReservationUpdateInterceptor();
            using var firstContext = database.NewContext(interceptor);
            var reservation = AddReleaseReservation(firstContext, FakeProvider, authorizationReference: "fake_tx_lease");
            var fake = new FakePaymentGateway
            {
                AuthorizationResponse = Authorization(reservation.ReservationId, "fake_tx_lease", PaymentAuthorizationState.RequiresCapture, 450)
            };
            var coordinator = CreateRaceCoordinator(fake);
            PaymentAuthorizationReleaseResult? other = null;
            // The second reconciliation runs completely after the first read the row, before it takes the lease.
            interceptor.Action = () =>
            {
                using var secondContext = database.NewContext();
                other = coordinator.ReconcileTerminalPaymentAuthorization(secondContext, secondContext.ChargePaymentReservations.Single(), PaymentAuthorizationReleaseTrigger.CleanupSweep);
            };

            var first = coordinator.ReconcileTerminalPaymentAuthorization(firstContext, reservation, PaymentAuthorizationReleaseTrigger.CleanupSweep);

            Assert.True(interceptor.Fired);
            Assert.Equal(PaymentAuthorizationReleaseOutcome.Released, other!.Outcome);
            Assert.Equal(PaymentAuthorizationReleaseOutcome.SkippedNotEligible, first.Outcome);
            Assert.Single(fake.Cancels);
            using var verification = database.NewContext();
            Assert.Equal(PaymentAuthorizationReleaseState.Released, verification.ChargePaymentReservations.Single().AuthorizationReleaseState);
            Assert.Single(verification.PaymentAuthorizationReleaseAttempts);
        }

        [Fact]
        public void Confirm_FailingToCreateTheChargeTagLeavesTheReservationPendingWithoutATag()
        {
            using var database = new SqliteRaceDatabase();
            var interceptor = new FailChargeTagInsertInterceptor();
            using var context = database.NewContext(interceptor);
            var reservation = AddPendingReservation(context, FakeProvider, "fake_token_tag_failure");
            var fake = new FakePaymentGateway
            {
                CheckoutStatus = CompletedCheckout(reservation.ReservationId, "fake_token_tag_failure", "fake_tx_tag_failure", inlineAuthorization: true, amountMinor: 450)
            };
            var coordinator = CreateRaceCoordinator(fake);
            interceptor.Armed = true;

            Assert.Throws<DbUpdateException>(() => coordinator.ConfirmReservation(context, reservation.ReservationId, null));

            using var verification = database.NewContext();
            var persisted = verification.ChargePaymentReservations.Single();
            Assert.Equal(PaymentReservationStatus.Pending, persisted.Status);
            Assert.True(string.IsNullOrEmpty(persisted.OcppIdTag));
            Assert.Null(persisted.AuthorizedAtUtc);
            Assert.Null(persisted.StartDeadlineAtUtc);
        }

        [Fact]
        public void Confirm_OnSqliteKeepsTheActiveConnectorKeyOfAnEndedReservationInStep()
        {
            using var database = new SqliteRaceDatabase();
            using var context = database.NewContext();
            var reservation = AddPendingReservation(context, FakeProvider, "fake_token_active_key");
            var fake = new FakePaymentGateway
            {
                CheckoutStatus = new PaymentCheckoutStatus
                {
                    SessionReference = "fake_token_active_key",
                    State = PaymentCheckoutState.Canceled,
                    RawStatus = "TRANSACTION_ABORTED",
                    OwnerReservationId = reservation.ReservationId
                }
            };
            var coordinator = CreateRaceCoordinator(fake);

            var confirmation = coordinator.ConfirmReservation(context, reservation.ReservationId, null);

            Assert.Equal(PaymentReservationStatus.Cancelled, confirmation.Status);
            using var verification = database.NewContext();
            var persisted = verification.ChargePaymentReservations.Single();
            Assert.Equal(PaymentReservationStatus.Cancelled, persisted.Status);
            Assert.Equal(
                reservation.ReservationId.ToString().ToUpperInvariant(),
                verification.Entry(persisted).Property<string>("ActiveConnectorKey").CurrentValue);
            // The connector can take a new reservation.
            verification.ChargePaymentReservations.Add(new ChargePaymentReservation
            {
                ReservationId = Guid.NewGuid(),
                ChargePointId = persisted.ChargePointId,
                ConnectorId = persisted.ConnectorId,
                ChargeTagId = "TAG2",
                PaymentProvider = FakeProvider,
                Status = PaymentReservationStatus.Pending,
                Currency = "eur",
                CreatedAtUtc = Now,
                UpdatedAtUtc = Now
            });
            verification.SaveChanges();
        }

        [Fact]
        public void Resume_OnSqliteLeavesAStartableAuthorizedReservation()
        {
            using var database = new SqliteRaceDatabase();
            using var context = database.NewContext();
            var reservation = AddPendingReservation(context, FakeProvider, "fake_token_resume_startable");
            var fake = FakePaymentGateway.SaferpayLike();
            fake.CheckoutStatus = CompletedCheckout(reservation.ReservationId, "fake_token_resume_startable", "fake_tx_resume_startable", inlineAuthorization: true, amountMinor: 450);
            var coordinator = CreateRaceCoordinator(fake);

            var result = coordinator.ResumeReservation(context, reservation.ReservationId);

            Assert.True(result.Success);
            Assert.Equal("Status", result.Status);
            using var verification = database.NewContext();
            var persisted = verification.ChargePaymentReservations.Single();
            Assert.Equal(PaymentReservationStatus.Authorized, persisted.Status);
            Assert.Equal(Now.AddMinutes(7), persisted.StartDeadlineAtUtc);
            Assert.False(string.IsNullOrEmpty(persisted.OcppIdTag));
            Assert.Equal(persisted.OcppIdTag, verification.ChargeTags.Single().TagId);
            Assert.Equal(reservation.OcppIdTag, persisted.OcppIdTag);
        }

        [Fact]
        public async Task Race_LateStartTransactionLinksBeforeTheStartTimeoutIsWritten_SweepKeepsTheHold()
        {
            using var database = new SqliteRaceDatabase();
            using (var seed = database.NewContext())
            {
                seed.ChargePoints.Add(new ChargePoint { ChargePointId = "CP-LATE-START", Name = "Late start" });
                seed.ChargePaymentReservations.Add(new ChargePaymentReservation
                {
                    ReservationId = Guid.NewGuid(),
                    ChargePointId = "CP-LATE-START",
                    ConnectorId = 1,
                    ChargeTagId = "TAG1",
                    OcppIdTag = "RLATESTART00000001",
                    PaymentProvider = FakeProvider,
                    StripeCheckoutSessionId = "fake_token_late_start",
                    StripePaymentIntentId = "fake_tx_late_start",
                    Status = PaymentReservationStatus.Authorized,
                    MaxAmountCents = 450,
                    Currency = "eur",
                    CreatedAtUtc = Now.AddMinutes(-10),
                    AuthorizedAtUtc = Now.AddMinutes(-8),
                    StartDeadlineAtUtc = Now.AddMinutes(-1),
                    UpdatedAtUtc = Now.AddMinutes(-8)
                });
                seed.Transactions.Add(new Transaction
                {
                    TransactionId = 91,
                    ChargePointId = "CP-LATE-START",
                    ConnectorId = 1,
                    StartTagId = "RLATESTART00000001",
                    StartTime = Now.AddSeconds(-30),
                    MeterStart = 0
                });
                seed.SaveChanges();
            }

            var fake = new FakePaymentGateway();
            var reservationId = Guid.Empty;
            using (var read = database.NewContext())
            {
                reservationId = read.ChargePaymentReservations.Single().ReservationId;
                fake.AuthorizationResponse = Authorization(reservationId, "fake_tx_late_start", PaymentAuthorizationState.RequiresCapture, 450);
            }

            var coordinator = CreateRaceCoordinator(fake);
            var interceptor = new BeforeReservationUpdateInterceptor();
            using var provider = database.BuildSweepProvider(coordinator, interceptor);
            // The charger's StartTransaction for the reservation arrives as its start window closes.
            interceptor.Action = () =>
            {
                using var ocppContext = database.NewContext();
                coordinator.MarkTransactionStarted(ocppContext, "CP-LATE-START", 1, "RLATESTART00000001", 91);
            };
            var sweep = new CleanupServiceHarness(provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<IConfiguration>(), utcNow: Now);

            await sweep.RunOnce();

            Assert.True(interceptor.Fired);
            using var verification = database.NewContext();
            var persisted = verification.ChargePaymentReservations.Single();
            Assert.Equal(PaymentReservationStatus.Charging, persisted.Status);
            Assert.Equal(91, persisted.TransactionId);
            Assert.Null(persisted.AuthorizationReleaseState);
            Assert.Empty(fake.Cancels);
        }

        [Fact]
        public void Race_StartupMaintenanceLeavesAPendingReservationThatWasAuthorizedMeanwhile()
        {
            using var database = new SqliteRaceDatabase();
            Guid reservationId;
            using (var seed = database.NewContext())
            {
                reservationId = AddStalePendingReservation(seed, "fake_token_startup_race").ReservationId;
            }

            var fake = new FakePaymentGateway
            {
                CheckoutStatus = CompletedCheckout(reservationId, "fake_token_startup_race", "fake_tx_startup_race", inlineAuthorization: true, amountMinor: 450)
            };
            var coordinator = CreateRaceCoordinator(fake);
            var interceptor = new BeforeReservationUpdateInterceptor();
            using var startupContext = database.NewContext(interceptor);
            // Another server instance confirms the reservation while this one starts up.
            interceptor.Action = () =>
            {
                using var otherInstance = database.NewContext();
                Assert.True(coordinator.ConfirmReservation(otherInstance, reservationId, null).Success);
            };
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Maintenance:PendingPaymentTimeoutMinutes"] = "15" })
                .Build();

            StartupMaintenance.Run(startupContext, NullLogger.Instance, configuration, () => Now);

            Assert.True(interceptor.Fired);
            using var verification = database.NewContext();
            var persisted = verification.ChargePaymentReservations.Single();
            Assert.Equal(PaymentReservationStatus.Authorized, persisted.Status);
            Assert.Null(persisted.AuthorizationReleaseState);
        }

        // ---- helpers ----

        private static ChargePaymentReservation AddStalePendingReservation(OCPPCoreContext context, string checkoutReference)
        {
            var reservation = AddPendingReservation(context, FakeProvider, checkoutReference);
            reservation.CreatedAtUtc = Now.AddMinutes(-20);
            reservation.UpdatedAtUtc = Now.AddMinutes(-20);
            context.SaveChanges();
            return reservation;
        }

        private static PaymentCoordinator CreateRaceCoordinator(IPaymentGateway gateway) =>
            new(
                new PaymentGatewayRegistry(new[] { gateway }, gateway.ProviderName),
                Settings(),
                Options.Create(new PaymentFlowOptions { StartWindowMinutes = 7 }),
                NullLogger<PaymentCoordinator>.Instance,
                () => Now);

        /// <summary>A shared-cache in-memory SQLite database; each context is one request's connection.</summary>
        private sealed class SqliteRaceDatabase : IDisposable
        {
            private readonly SqliteConnection _keepAlive;

            public SqliteRaceDatabase()
            {
                ConnectionString = $"Data Source=race-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
                _keepAlive = new SqliteConnection(ConnectionString);
                _keepAlive.Open();
                using var context = NewContext();
                context.Database.EnsureCreated();
            }

            public string ConnectionString { get; }

            public OCPPCoreContext NewContext(IInterceptor? interceptor = null)
            {
                var builder = new DbContextOptionsBuilder<OCPPCoreContext>().UseSqlite(ConnectionString);
                if (interceptor != null)
                {
                    builder.AddInterceptors(interceptor);
                }

                return new OCPPCoreContext(builder.Options);
            }

            public ServiceProvider BuildSweepProvider(IPaymentCoordinator coordinator, IInterceptor? interceptor = null)
            {
                var services = new ServiceCollection();
                services.AddLogging();
                services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Maintenance:PendingPaymentTimeoutMinutes"] = "15",
                        ["Maintenance:CleanupIntervalSeconds"] = "30"
                    })
                    .Build());
                services.AddDbContext<OCPPCoreContext>(options =>
                {
                    options.UseSqlite(ConnectionString);
                    if (interceptor != null)
                    {
                        options.AddInterceptors(interceptor);
                    }
                });
                services.AddSingleton<IPaymentCoordinator>(coordinator);
                return services.BuildServiceProvider();
            }

            public void Dispose() => _keepAlive.Dispose();
        }

        /// <summary>Runs an action once, after the next checkout read returned and before the caller continues.</summary>
        private sealed class HookedGateway : IPaymentGateway
        {
            private readonly FakePaymentGateway _inner;

            public HookedGateway(FakePaymentGateway inner)
            {
                _inner = inner;
            }

            public Action? AfterNextGetCheckout { get; set; }
            public string ProviderName => _inner.ProviderName;
            public bool IsConfigured => _inner.IsConfigured;
            public string? SuccessUrlSessionPlaceholder => _inner.SuccessUrlSessionPlaceholder;
            public bool SupportsMetadata => _inner.SupportsMetadata;
            public bool SupportsDeferredCheckoutLookup => _inner.SupportsDeferredCheckoutLookup;
            public TimeSpan? CheckoutLifetime => _inner.CheckoutLifetime;
            public bool CapturedCheckoutRequiresRefund => _inner.CapturedCheckoutRequiresRefund;
            public PaymentCheckoutSession CreateCheckout(PaymentCheckoutRequest request) => _inner.CreateCheckout(request);

            public PaymentCheckoutStatus GetCheckout(string sessionReference)
            {
                var checkout = _inner.GetCheckout(sessionReference);
                var hook = AfterNextGetCheckout;
                AfterNextGetCheckout = null;
                hook?.Invoke();
                return checkout;
            }

            public PaymentAuthorizationSnapshot GetAuthorization(string authorizationReference) => _inner.GetAuthorization(authorizationReference);
            public PaymentAuthorizationSnapshot? FindAuthorizationByReservation(Guid reservationId) => _inner.FindAuthorizationByReservation(reservationId);
            public PaymentAuthorizationSnapshot Capture(string authorizationReference, long amountMinor, string currency, string idempotencyKey) =>
                _inner.Capture(authorizationReference, amountMinor, currency, idempotencyKey);
            public PaymentAuthorizationSnapshot Cancel(string authorizationReference, string idempotencyKey) => _inner.Cancel(authorizationReference, idempotencyKey);
            public IReadOnlyDictionary<string, string> MirrorMetadata(string sessionReference, string fallbackAuthorizationReference, Action<IDictionary<string, string>> mutate) =>
                _inner.MirrorMetadata(sessionReference, fallbackAuthorizationReference, mutate);
            public PaymentWebhookParseResult ParseWebhook(PaymentWebhookRequest request) => _inner.ParseWebhook(request);
            public string SanitizeProviderText(string text) => _inner.SanitizeProviderText(text);
        }

        /// <summary>Runs an action once, just before the first UPDATE of a reservation row is sent to the database.</summary>
        private sealed class BeforeReservationUpdateInterceptor : DbCommandInterceptor
        {
            public Action? Action { get; set; }
            public bool Fired { get; private set; }

            public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
            {
                Fire(command);
                return result;
            }

            public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
            {
                Fire(command);
                return ValueTask.FromResult(result);
            }

            public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
            {
                Fire(command);
                return result;
            }

            public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
            {
                Fire(command);
                return ValueTask.FromResult(result);
            }

            private void Fire(DbCommand command)
            {
                var text = command.CommandText.TrimStart();
                if (Fired ||
                    Action == null ||
                    !text.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase) ||
                    !text.Contains("ChargePaymentReservation", StringComparison.Ordinal))
                {
                    return;
                }

                Fired = true;
                Action();
            }
        }

        private sealed class FailChargeTagInsertInterceptor : SaveChangesInterceptor
        {
            public bool Armed { get; set; }

            public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
            {
                if (Armed && eventData.Context!.ChangeTracker.Entries<ChargeTag>().Any(entry => entry.State == EntityState.Added))
                {
                    throw new DbUpdateException("Simulated database failure while creating the charge tag.");
                }

                return base.SavingChanges(eventData, result);
            }
        }
    }
}
