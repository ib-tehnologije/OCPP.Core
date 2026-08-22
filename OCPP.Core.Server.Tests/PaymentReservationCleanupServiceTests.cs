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
using Stripe;
using Stripe.Checkout;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    public class PaymentReservationCleanupServiceTests
    {
        [Fact]
        public async Task CleanupAsync_AbandonsStalePendingReservation_AndReconcilesAfterArming()
        {
            var coordinator = new RecordingPaymentCoordinator();
            using var provider = BuildProvider(
                coordinator,
                new Dictionary<string, string?>
                {
                    ["Maintenance:PendingPaymentTimeoutMinutes"] = "1",
                    ["Maintenance:CleanupIntervalSeconds"] = "30"
                });

            var reservationId = Guid.NewGuid();
            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OCPPCoreContext>();
                db.ChargePaymentReservations.Add(new ChargePaymentReservation
                {
                    ReservationId = reservationId,
                    ChargePointId = "CP1",
                    ConnectorId = 1,
                    ChargeTagId = "TAG1",
                    StripePaymentIntentId = "pi_stale",
                    Status = PaymentReservationStatus.Pending,
                    Currency = "eur",
                    CreatedAtUtc = DateTime.UtcNow.AddHours(-2),
                    UpdatedAtUtc = DateTime.UtcNow.AddHours(-2)
                });
                db.SaveChanges();
            }

            var service = new CleanupServiceHarness(
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<IConfiguration>());

            await service.RunOnce();

            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OCPPCoreContext>();
                var reservation = db.ChargePaymentReservations.Single(r => r.ReservationId == reservationId);

                Assert.Equal(PaymentReservationStatus.Abandoned, reservation.Status);
                Assert.Equal("CleanupTimeout", reservation.FailureCode);
                Assert.Contains("Auto-cancelled", reservation.LastError);
                Assert.Equal(PaymentAuthorizationReleaseState.Pending, reservation.AuthorizationReleaseState);
            }

            Assert.Empty(coordinator.CancelCalls);
            Assert.Single(coordinator.ReconcileCalls);
            Assert.Equal(reservationId, coordinator.ReconcileCalls[0].ReservationId);
            Assert.Equal(PaymentAuthorizationReleaseTrigger.CleanupSweep, coordinator.ReconcileCalls[0].Trigger);
        }

        [Fact]
        public async Task CleanupAsync_PreservesSpecificProviderErrorWhenReservationBecomesAbandoned()
        {
            var coordinator = new RecordingPaymentCoordinator
            {
                ReconcileErrorToRecord = "Detailed provider timeout while releasing authorization."
            };
            using var provider = BuildProvider(
                coordinator,
                new Dictionary<string, string?>
                {
                    ["Maintenance:PendingPaymentTimeoutMinutes"] = "1",
                    ["Maintenance:CleanupIntervalSeconds"] = "30"
                });

            var reservationId = Guid.NewGuid();
            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OCPPCoreContext>();
                db.ChargePaymentReservations.Add(new ChargePaymentReservation
                {
                    ReservationId = reservationId,
                    ChargePointId = "CP1",
                    ConnectorId = 1,
                    ChargeTagId = "TAG1",
                    StripePaymentIntentId = "pi_stale_error",
                    Status = PaymentReservationStatus.Pending,
                    Currency = "eur",
                    CreatedAtUtc = DateTime.UtcNow.AddHours(-2),
                    UpdatedAtUtc = DateTime.UtcNow.AddHours(-2)
                });
                db.SaveChanges();
            }

            var service = new CleanupServiceHarness(
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<IConfiguration>());

            await service.RunOnce();

            using var verificationScope = provider.CreateScope();
            var verificationDb = verificationScope.ServiceProvider.GetRequiredService<OCPPCoreContext>();
            var reservation = verificationDb.ChargePaymentReservations.Single(r => r.ReservationId == reservationId);
            Assert.Equal(PaymentReservationStatus.Abandoned, reservation.Status);
            Assert.Contains("Auto-cancelled", reservation.LastError);
            Assert.Contains("Auto-cancelled", reservation.FailureMessage);
            Assert.Equal(PaymentAuthorizationReleaseState.Pending, reservation.AuthorizationReleaseState);
            Assert.Equal("Detailed provider timeout while releasing authorization.", reservation.AuthorizationReleaseLastError);
        }

        [Fact]
        public async Task CleanupAsync_RetriesOnlyArmedDueTerminalReservations()
        {
            var coordinator = new RecordingPaymentCoordinator();
            using var provider = BuildProvider(
                coordinator,
                new Dictionary<string, string?>
                {
                    ["Maintenance:PendingPaymentTimeoutMinutes"] = "15",
                    ["Maintenance:CleanupIntervalSeconds"] = "30"
                });

            Guid dueId;
            Guid futureId;
            Guid historicalId;
            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OCPPCoreContext>();
                var due = NewTerminalReservation("pi_due", PaymentAuthorizationReleaseState.RetryScheduled, DateTime.UtcNow.AddMinutes(-1));
                var future = NewTerminalReservation("pi_future", PaymentAuthorizationReleaseState.RetryScheduled, DateTime.UtcNow.AddMinutes(10));
                var historical = NewTerminalReservation("pi_historical", null, null);
                db.ChargePaymentReservations.AddRange(due, future, historical);
                db.SaveChanges();
                dueId = due.ReservationId;
                futureId = future.ReservationId;
                historicalId = historical.ReservationId;
            }

            var service = new CleanupServiceHarness(
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<IConfiguration>());

            await service.RunOnce();

            Assert.Single(coordinator.ReconcileCalls);
            Assert.Equal(dueId, coordinator.ReconcileCalls[0].ReservationId);
            Assert.DoesNotContain(coordinator.ReconcileCalls, call => call.ReservationId == futureId);
            Assert.DoesNotContain(coordinator.ReconcileCalls, call => call.ReservationId == historicalId);
        }

        [Fact]
        public async Task CleanupAsync_RecoversOnlyExpiredInProgressLease()
        {
            var coordinator = new RecordingPaymentCoordinator();
            using var provider = BuildProvider(
                coordinator,
                new Dictionary<string, string?>
                {
                    ["Maintenance:PendingPaymentTimeoutMinutes"] = "15",
                    ["Maintenance:CleanupIntervalSeconds"] = "30",
                    ["Maintenance:AuthorizationReleaseInProgressTimeoutMinutes"] = "5"
                });
            Guid freshId;
            Guid expiredId;

            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OCPPCoreContext>();
                var fresh = NewTerminalReservation("pi_fresh_lease", PaymentAuthorizationReleaseState.InProgress, null);
                fresh.AuthorizationReleaseLastAttemptAtUtc = DateTime.UtcNow;
                var expired = NewTerminalReservation("pi_expired_lease", PaymentAuthorizationReleaseState.InProgress, null);
                expired.AuthorizationReleaseLastAttemptAtUtc = DateTime.UtcNow.AddMinutes(-10);
                db.AddRange(fresh, expired);
                db.SaveChanges();
                freshId = fresh.ReservationId;
                expiredId = expired.ReservationId;
            }

            var service = new CleanupServiceHarness(
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<IConfiguration>());
            await service.RunOnce();

            Assert.Single(coordinator.ReconcileCalls);
            Assert.Equal(expiredId, coordinator.ReconcileCalls[0].ReservationId);
            Assert.DoesNotContain(coordinator.ReconcileCalls, call => call.ReservationId == freshId);
        }

        [Fact]
        public async Task CleanupAsync_LateCheckoutWebhookReleasesAuthorizationAfterMissingIntentLinkage()
        {
            var settings = new Dictionary<string, string?>
            {
                ["Maintenance:PendingPaymentTimeoutMinutes"] = "1",
                ["Maintenance:CleanupIntervalSeconds"] = "30"
            };
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            var intents = new ReleasePaymentIntentService();
            var eventFactory = new ReleaseEventFactory();
            var sessions = new ReleaseSessionService();
            var coordinator = new StripePaymentCoordinator(
                Options.Create(new StripeOptions
                {
                    Enabled = true,
                    ApiKey = "test",
                    ReturnBaseUrl = "https://return",
                    WebhookSecret = "whsec_test"
                }),
                Options.Create(new PaymentFlowOptions()),
                NullLogger<StripePaymentCoordinator>.Instance,
                sessions,
                intents,
                eventFactory,
                () => DateTime.UtcNow,
                configuration: configuration);
            using var provider = BuildProvider(coordinator, settings);
            var reservationId = Guid.NewGuid();
            sessions.GetResponse = new Session
            {
                Id = "sess_late_cleanup",
                Metadata = new Dictionary<string, string>
                {
                    ["reservation_id"] = reservationId.ToString()
                }
            };

            using (var setupScope = provider.CreateScope())
            {
                var db = setupScope.ServiceProvider.GetRequiredService<OCPPCoreContext>();
                db.ChargePaymentReservations.Add(new ChargePaymentReservation
                {
                    ReservationId = reservationId,
                    ChargePointId = "CP-LATE",
                    ConnectorId = 1,
                    ChargeTagId = "TAG-LATE",
                    OcppIdTag = "TAG-LATE",
                    StripeCheckoutSessionId = "sess_late_cleanup",
                    Status = PaymentReservationStatus.Pending,
                    Currency = "eur",
                    CreatedAtUtc = DateTime.UtcNow.AddHours(-2),
                    StartDeadlineAtUtc = DateTime.UtcNow.AddHours(-1),
                    UpdatedAtUtc = DateTime.UtcNow.AddHours(-2)
                });
                db.SaveChanges();
            }

            var service = new CleanupServiceHarness(
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<IConfiguration>());
            await service.RunOnce();

            using (var armedScope = provider.CreateScope())
            {
                var armedDb = armedScope.ServiceProvider.GetRequiredService<OCPPCoreContext>();
                var armed = armedDb.ChargePaymentReservations.Single(r => r.ReservationId == reservationId);
                Assert.Equal(PaymentReservationStatus.Abandoned, armed.Status);
                Assert.Equal(PaymentAuthorizationReleaseState.RetryScheduled, armed.AuthorizationReleaseState);
                Assert.Single(armedDb.PaymentAuthorizationReleaseAttempts);
            }

            intents.GetResponse = new PaymentIntent
            {
                Id = "pi_late_cleanup",
                Status = "requires_capture",
                AmountCapturable = 500,
                Metadata = new Dictionary<string, string>
                {
                    ["reservation_id"] = reservationId.ToString()
                }
            };
            eventFactory.EventToReturn = new Event
            {
                Id = "evt_late_cleanup",
                Type = EventTypes.CheckoutSessionCompleted,
                Data = new EventData
                {
                    Object = new Session
                    {
                        Id = "sess_late_cleanup",
                        PaymentIntentId = "pi_late_cleanup",
                        PaymentStatus = "paid"
                    }
                }
            };

            using (var webhookScope = provider.CreateScope())
            {
                var webhookDb = webhookScope.ServiceProvider.GetRequiredService<OCPPCoreContext>();
                coordinator.HandleWebhookEvent(webhookDb, "payload", "signature");
            }

            using var verificationScope = provider.CreateScope();
            var verificationDb = verificationScope.ServiceProvider.GetRequiredService<OCPPCoreContext>();
            var released = verificationDb.ChargePaymentReservations.Single(r => r.ReservationId == reservationId);
            Assert.Equal(PaymentAuthorizationReleaseState.Released, released.AuthorizationReleaseState);
            Assert.Equal(1, intents.CancelCalls);
            Assert.Equal(
                PaymentAuthorizationReleaseTrigger.CheckoutCompletedWebhook,
                verificationDb.PaymentAuthorizationReleaseAttempts.OrderBy(attempt => attempt.AttemptNumber).Last().Trigger);
            Assert.Equal(2, verificationDb.PaymentAuthorizationReleaseAttempts.Count());
        }

        [Fact]
        public async Task CleanupAsync_MarksStartTimeout_WhenStartWindowExpired()
        {
            var coordinator = new RecordingPaymentCoordinator();
            using var provider = BuildProvider(
                coordinator,
                new Dictionary<string, string?>
                {
                    ["Maintenance:PendingPaymentTimeoutMinutes"] = "15",
                    ["Maintenance:CleanupIntervalSeconds"] = "30"
                });

            var reservationId = Guid.NewGuid();
            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OCPPCoreContext>();
                db.ChargePaymentReservations.Add(new ChargePaymentReservation
                {
                    ReservationId = reservationId,
                    ChargePointId = "CP1",
                    ConnectorId = 1,
                    ChargeTagId = "TAG2",
                    StripePaymentIntentId = "pi_start_timeout",
                    Status = PaymentReservationStatus.Authorized,
                    StartDeadlineAtUtc = DateTime.UtcNow.AddMinutes(-1),
                    Currency = "eur",
                    CreatedAtUtc = DateTime.UtcNow.AddMinutes(-20),
                    UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-20)
                });
                db.SaveChanges();
            }

            var service = new CleanupServiceHarness(
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<IConfiguration>());

            await service.RunOnce();

            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OCPPCoreContext>();
                var reservation = db.ChargePaymentReservations.Single(r => r.ReservationId == reservationId);

                Assert.Equal(PaymentReservationStatus.StartTimeout, reservation.Status);
                Assert.Equal("StartTimeout", reservation.FailureCode);
                Assert.Equal("Start window expired without transaction.", reservation.LastError);
                Assert.Equal("Start window expired without transaction.", reservation.FailureMessage);
            }

            Assert.Single(coordinator.CancelCalls);
            Assert.Equal(reservationId, coordinator.CancelCalls[0].ReservationId);
            Assert.Equal("Start window expired", coordinator.CancelCalls[0].Reason);
        }

        [Fact]
        public async Task CleanupAsync_KeepsReservationValidAtNineMinutesFiftyNineSeconds()
        {
            var now = new DateTime(2026, 7, 19, 20, 0, 0, DateTimeKind.Utc);
            var authorizedAt = now.AddMinutes(-9).AddSeconds(-59);
            var existingDeadline = authorizedAt.AddMinutes(10);
            var coordinator = new RecordingPaymentCoordinator();
            using var provider = BuildProvider(
                coordinator,
                new Dictionary<string, string?>
                {
                    ["Maintenance:PendingPaymentTimeoutMinutes"] = "15",
                    ["Maintenance:CleanupIntervalSeconds"] = "30"
                });

            var reservationId = Guid.NewGuid();
            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OCPPCoreContext>();
                db.ChargePaymentReservations.Add(new ChargePaymentReservation
                {
                    ReservationId = reservationId,
                    ChargePointId = "CP-BEFORE-DEADLINE",
                    ConnectorId = 1,
                    ChargeTagId = "TAG-BEFORE-DEADLINE",
                    StripePaymentIntentId = "pi_before_deadline",
                    Status = PaymentReservationStatus.Authorized,
                    AuthorizedAtUtc = authorizedAt,
                    StartDeadlineAtUtc = existingDeadline,
                    Currency = "eur",
                    CreatedAtUtc = authorizedAt,
                    UpdatedAtUtc = authorizedAt
                });
                db.SaveChanges();
            }

            var service = new CleanupServiceHarness(
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<IConfiguration>(),
                utcNow: now);

            await service.RunOnce();

            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OCPPCoreContext>();
                var reservation = db.ChargePaymentReservations.Single(r => r.ReservationId == reservationId);

                Assert.Equal(PaymentReservationStatus.Authorized, reservation.Status);
                Assert.Equal(existingDeadline, reservation.StartDeadlineAtUtc);
            }

            Assert.Empty(coordinator.CancelCalls);
        }

        [Fact]
        public async Task CleanupAsync_MarksStartTimeout_ExactlyAtDeadline_OnlyOnce()
        {
            var now = new DateTime(2026, 7, 19, 20, 0, 0, DateTimeKind.Utc);
            var coordinator = new RecordingPaymentCoordinator();
            using var provider = BuildProvider(
                coordinator,
                new Dictionary<string, string?>
                {
                    ["Maintenance:PendingPaymentTimeoutMinutes"] = "15",
                    ["Maintenance:CleanupIntervalSeconds"] = "30"
                });

            var reservationId = Guid.NewGuid();
            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OCPPCoreContext>();
                db.ChargePaymentReservations.Add(new ChargePaymentReservation
                {
                    ReservationId = reservationId,
                    ChargePointId = "CP-DEADLINE",
                    ConnectorId = 1,
                    ChargeTagId = "TAG-DEADLINE",
                    StripePaymentIntentId = "pi_deadline",
                    Status = PaymentReservationStatus.Authorized,
                    AuthorizedAtUtc = now.AddMinutes(-10),
                    StartDeadlineAtUtc = now,
                    Currency = "eur",
                    CreatedAtUtc = now.AddMinutes(-10),
                    UpdatedAtUtc = now.AddMinutes(-10)
                });
                db.SaveChanges();
            }

            var service = new CleanupServiceHarness(
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<IConfiguration>(),
                utcNow: now);

            await service.RunOnce();
            await service.RunOnce();

            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OCPPCoreContext>();
                var reservation = db.ChargePaymentReservations.Single(r => r.ReservationId == reservationId);

                Assert.Equal(PaymentReservationStatus.StartTimeout, reservation.Status);
                Assert.Equal("StartTimeout", reservation.FailureCode);
            }

            Assert.Single(coordinator.CancelCalls);
            Assert.Equal(reservationId, coordinator.CancelCalls[0].ReservationId);
        }

        [Fact]
        public async Task CleanupAsync_ClosesChargingReservation_WhenConnectorPersistedAvailable()
        {
            var coordinator = new RecordingPaymentCoordinator();
            using var provider = BuildProvider(
                coordinator,
                new Dictionary<string, string?>
                {
                    ["Maintenance:PendingPaymentTimeoutMinutes"] = "15",
                    ["Maintenance:CleanupIntervalSeconds"] = "30",
                    ["Maintenance:AvailableStatusOpenTransactionGraceMinutes"] = "1"
                });

            var reservationId = Guid.NewGuid();
            var availableAt = DateTime.UtcNow.AddMinutes(-5);

            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OCPPCoreContext>();
                db.Transactions.Add(new Transaction
                {
                    TransactionId = 9001,
                    ChargePointId = "CP1",
                    ConnectorId = 2,
                    StartTagId = "TAG3",
                    StartTime = availableAt.AddHours(-1),
                    MeterStart = 10.0
                });
                db.ConnectorStatuses.Add(new ConnectorStatus
                {
                    ChargePointId = "CP1",
                    ConnectorId = 2,
                    LastStatus = "Available",
                    LastStatusTime = availableAt,
                    LastMeter = 12.5,
                    LastMeterTime = availableAt.AddMinutes(-1)
                });
                db.ChargePaymentReservations.Add(new ChargePaymentReservation
                {
                    ReservationId = reservationId,
                    ChargePointId = "CP1",
                    ConnectorId = 2,
                    ChargeTagId = "TAG3",
                    OcppIdTag = "TAG3",
                    StripePaymentIntentId = "pi_available",
                    Status = PaymentReservationStatus.Charging,
                    TransactionId = 9001,
                    Currency = "eur",
                    CreatedAtUtc = availableAt.AddHours(-1),
                    UpdatedAtUtc = availableAt.AddHours(-1)
                });
                db.SaveChanges();
            }

            var service = new CleanupServiceHarness(
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<IConfiguration>());

            await service.RunOnce();

            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OCPPCoreContext>();
                var transaction = db.Transactions.Single(t => t.TransactionId == 9001);
                var reservation = db.ChargePaymentReservations.Single(r => r.ReservationId == reservationId);

                Assert.Equal(availableAt, transaction.StopTime);
                Assert.Equal(12.5, transaction.MeterStop);
                Assert.Equal("ConnectorAvailableWithoutStopTransaction", transaction.StopReason);
                Assert.Equal(availableAt, transaction.ChargingEndedAtUtc);
                Assert.Equal(PaymentReservationStatus.Completed, reservation.Status);
                Assert.Equal(availableAt, reservation.StopTransactionAtUtc);
                Assert.Equal(availableAt, reservation.DisconnectedAtUtc);
            }

            Assert.Equal(new[] { 9001 }, coordinator.CompleteCalls);
        }

        [Fact]
        public async Task CleanupAsync_CompletesChargingReservation_WhenMatchingTransactionAlreadyStoppedAndConnectorLaterAvailable()
        {
            var now = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
            var stoppedAt = now.AddMinutes(-5);
            var availableAt = stoppedAt.AddSeconds(3);
            var coordinator = new RecordingPaymentCoordinator();
            var logger = new RecordingLogger<PaymentReservationCleanupService>();
            using var provider = BuildProvider(
                coordinator,
                new Dictionary<string, string?>
                {
                    ["Maintenance:PendingPaymentTimeoutMinutes"] = "15",
                    ["Maintenance:CleanupIntervalSeconds"] = "30",
                    ["Maintenance:AvailableStatusOpenTransactionGraceMinutes"] = "1"
                });

            var reservationId = Guid.NewGuid();
            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OCPPCoreContext>();
                db.Transactions.Add(new Transaction
                {
                    TransactionId = 9004,
                    ChargePointId = "CP-CLOSED",
                    ConnectorId = 1,
                    StartTagId = "TAG-CLOSED",
                    StartTime = stoppedAt.AddHours(-1),
                    StopTime = stoppedAt,
                    StopReason = "Local",
                    MeterStart = 10.0,
                    MeterStop = 12.0,
                    ChargingEndedAtUtc = stoppedAt
                });
                db.ConnectorStatuses.Add(new ConnectorStatus
                {
                    ChargePointId = "CP-CLOSED",
                    ConnectorId = 1,
                    LastStatus = OcppConnectorStatus.Available,
                    LastStatusTime = availableAt,
                    LastMeter = 12.0,
                    LastMeterTime = availableAt
                });
                db.ChargePaymentReservations.Add(new ChargePaymentReservation
                {
                    ReservationId = reservationId,
                    ChargePointId = "CP-CLOSED",
                    ConnectorId = 1,
                    ChargeTagId = "TAG-CLOSED",
                    OcppIdTag = "TAG-CLOSED",
                    StripePaymentIntentId = "pi_closed",
                    Status = PaymentReservationStatus.Charging,
                    TransactionId = 9004,
                    Currency = "eur",
                    CreatedAtUtc = stoppedAt.AddHours(-1),
                    UpdatedAtUtc = stoppedAt
                });
                db.SaveChanges();
            }

            var service = new CleanupServiceHarness(
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<IConfiguration>(),
                logger,
                utcNow: now);

            await service.RunOnce();

            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OCPPCoreContext>();
                var reservation = db.ChargePaymentReservations.Single(r => r.ReservationId == reservationId);
                var transaction = db.Transactions.Single(t => t.TransactionId == 9004);

                Assert.Equal(PaymentReservationStatus.Completed, reservation.Status);
                Assert.Equal(stoppedAt, reservation.StopTransactionAtUtc);
                Assert.Equal(stoppedAt, reservation.DisconnectedAtUtc);
                Assert.Equal(stoppedAt, transaction.StopTime);
                Assert.Equal("Local", transaction.StopReason);
            }

            Assert.Equal(new[] { 9004 }, coordinator.CompleteCalls);
            Assert.Contains(logger.Entries, entry =>
                entry.Level == LogLevel.Warning &&
                entry.Message.Contains("Completed Charging recovery candidate", StringComparison.Ordinal) &&
                entry.Message.Contains("cp=CP-CLOSED", StringComparison.Ordinal) &&
                entry.Message.Contains("connector=1", StringComparison.Ordinal) &&
                entry.Message.Contains("tx=9004", StringComparison.Ordinal) &&
                entry.Message.Contains("reservation=", StringComparison.Ordinal) &&
                !entry.Message.Contains("TAG-CLOSED", StringComparison.Ordinal) &&
                !entry.Message.Contains("pi_closed", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData("CP-OTHER", 1)]
        [InlineData("CP-IDENTITY", 2)]
        public async Task CleanupAsync_DoesNotCompleteClosedChargingReservation_WhenTransactionIdentityDoesNotMatch(
            string transactionChargePointId,
            int transactionConnectorId)
        {
            var now = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
            var stoppedAt = now.AddMinutes(-5);
            var coordinator = new RecordingPaymentCoordinator();
            using var provider = BuildProvider(
                coordinator,
                new Dictionary<string, string?>
                {
                    ["Maintenance:PendingPaymentTimeoutMinutes"] = "15",
                    ["Maintenance:CleanupIntervalSeconds"] = "30",
                    ["Maintenance:AvailableStatusOpenTransactionGraceMinutes"] = "1"
                });

            var reservationId = Guid.NewGuid();
            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OCPPCoreContext>();
                db.Transactions.Add(new Transaction
                {
                    TransactionId = 9005,
                    ChargePointId = transactionChargePointId,
                    ConnectorId = transactionConnectorId,
                    StartTagId = "TAG-IDENTITY",
                    StartTime = stoppedAt.AddHours(-1),
                    StopTime = stoppedAt,
                    StopReason = "Local",
                    MeterStart = 10.0,
                    MeterStop = 12.0
                });
                db.ConnectorStatuses.Add(new ConnectorStatus
                {
                    ChargePointId = "CP-IDENTITY",
                    ConnectorId = 1,
                    LastStatus = OcppConnectorStatus.Available,
                    LastStatusTime = stoppedAt.AddSeconds(3)
                });
                db.ChargePaymentReservations.Add(new ChargePaymentReservation
                {
                    ReservationId = reservationId,
                    ChargePointId = "CP-IDENTITY",
                    ConnectorId = 1,
                    ChargeTagId = "TAG-IDENTITY",
                    OcppIdTag = "TAG-IDENTITY",
                    StripePaymentIntentId = "pi_identity",
                    Status = PaymentReservationStatus.Charging,
                    TransactionId = 9005,
                    Currency = "eur",
                    CreatedAtUtc = stoppedAt.AddHours(-1),
                    UpdatedAtUtc = stoppedAt
                });
                db.SaveChanges();
            }

            var service = new CleanupServiceHarness(
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<IConfiguration>(),
                utcNow: now);

            await service.RunOnce();

            using var verificationScope = provider.CreateScope();
            var reservation = verificationScope.ServiceProvider
                .GetRequiredService<OCPPCoreContext>()
                .ChargePaymentReservations
                .Single(r => r.ReservationId == reservationId);
            Assert.Equal(PaymentReservationStatus.Charging, reservation.Status);
            Assert.Empty(coordinator.CompleteCalls);
        }

        [Theory]
        [InlineData(PaymentReservationStatus.Charging, "Occupied", 297)]
        [InlineData(PaymentReservationStatus.Charging, OcppConnectorStatus.Available, null)]
        [InlineData(PaymentReservationStatus.Charging, OcppConnectorStatus.Available, 301)]
        [InlineData(PaymentReservationStatus.Charging, OcppConnectorStatus.Available, 30)]
        [InlineData(PaymentReservationStatus.Completed, OcppConnectorStatus.Available, 297)]
        public async Task CleanupAsync_DoesNotCompleteClosedChargingReservation_WithoutAllRecoveryEvidence(
            string reservationStatus,
            string connectorStatus,
            int? connectorStatusAgeSeconds)
        {
            var now = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
            var stoppedAt = now.AddMinutes(-5);
            var coordinator = new RecordingPaymentCoordinator();
            using var provider = BuildProvider(
                coordinator,
                new Dictionary<string, string?>
                {
                    ["Maintenance:PendingPaymentTimeoutMinutes"] = "15",
                    ["Maintenance:CleanupIntervalSeconds"] = "30",
                    ["Maintenance:AvailableStatusOpenTransactionGraceMinutes"] = "1"
                });

            var reservationId = Guid.NewGuid();
            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OCPPCoreContext>();
                db.Transactions.Add(new Transaction
                {
                    TransactionId = 9006,
                    ChargePointId = "CP-GUARD",
                    ConnectorId = 1,
                    StartTagId = "TAG-GUARD",
                    StartTime = stoppedAt.AddHours(-1),
                    StopTime = stoppedAt,
                    StopReason = "Local",
                    MeterStart = 10.0,
                    MeterStop = 12.0
                });
                db.ConnectorStatuses.Add(new ConnectorStatus
                {
                    ChargePointId = "CP-GUARD",
                    ConnectorId = 1,
                    LastStatus = connectorStatus,
                    LastStatusTime = connectorStatusAgeSeconds.HasValue
                        ? now.AddSeconds(-connectorStatusAgeSeconds.Value)
                        : null
                });
                db.ChargePaymentReservations.Add(new ChargePaymentReservation
                {
                    ReservationId = reservationId,
                    ChargePointId = "CP-GUARD",
                    ConnectorId = 1,
                    ChargeTagId = "TAG-GUARD",
                    OcppIdTag = "TAG-GUARD",
                    StripePaymentIntentId = "pi_guard",
                    Status = reservationStatus,
                    TransactionId = 9006,
                    Currency = "eur",
                    CreatedAtUtc = stoppedAt.AddHours(-1),
                    UpdatedAtUtc = stoppedAt
                });
                db.SaveChanges();
            }

            var service = new CleanupServiceHarness(
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<IConfiguration>(),
                utcNow: now);

            await service.RunOnce();

            using var verificationScope = provider.CreateScope();
            var reservation = verificationScope.ServiceProvider
                .GetRequiredService<OCPPCoreContext>()
                .ChargePaymentReservations
                .Single(r => r.ReservationId == reservationId);
            Assert.Equal(reservationStatus, reservation.Status);
            Assert.Empty(coordinator.CompleteCalls);
        }

        [Fact]
        public async Task CleanupAsync_RecoversOnlyAvailableConnector_WhenAnotherConnectorHasOpenTransaction()
        {
            var now = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
            var stoppedAt = now.AddMinutes(-5);
            var availableAt = stoppedAt.AddSeconds(3);
            var coordinator = new RecordingPaymentCoordinator();
            using var provider = BuildProvider(
                coordinator,
                new Dictionary<string, string?>
                {
                    ["Maintenance:PendingPaymentTimeoutMinutes"] = "15",
                    ["Maintenance:CleanupIntervalSeconds"] = "30",
                    ["Maintenance:AvailableStatusOpenTransactionGraceMinutes"] = "1"
                });

            var recoveredReservationId = Guid.NewGuid();
            var activeReservationId = Guid.NewGuid();
            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OCPPCoreContext>();
                db.Transactions.AddRange(
                    new Transaction
                    {
                        TransactionId = 9007,
                        ChargePointId = "CP-MULTI",
                        ConnectorId = 1,
                        StartTagId = "TAG-RECOVERED",
                        StartTime = stoppedAt.AddHours(-1),
                        StopTime = stoppedAt,
                        StopReason = "Local",
                        MeterStart = 10.0,
                        MeterStop = 12.0
                    },
                    new Transaction
                    {
                        TransactionId = 9008,
                        ChargePointId = "CP-MULTI",
                        ConnectorId = 2,
                        StartTagId = "TAG-ACTIVE",
                        StartTime = now.AddMinutes(-10),
                        MeterStart = 20.0
                    });
                db.ConnectorStatuses.AddRange(
                    new ConnectorStatus
                    {
                        ChargePointId = "CP-MULTI",
                        ConnectorId = 1,
                        LastStatus = OcppConnectorStatus.Available,
                        LastStatusTime = availableAt,
                        LastMeter = 12.0,
                        LastMeterTime = availableAt
                    },
                    new ConnectorStatus
                    {
                        ChargePointId = "CP-MULTI",
                        ConnectorId = 2,
                        LastStatus = "Occupied",
                        LastStatusTime = now.AddMinutes(-1),
                        LastMeter = 21.0,
                        LastMeterTime = now.AddMinutes(-1)
                    });
                db.ChargePaymentReservations.AddRange(
                    new ChargePaymentReservation
                    {
                        ReservationId = recoveredReservationId,
                        ChargePointId = "CP-MULTI",
                        ConnectorId = 1,
                        ChargeTagId = "TAG-RECOVERED",
                        OcppIdTag = "TAG-RECOVERED",
                        StripePaymentIntentId = "pi_recovered",
                        Status = PaymentReservationStatus.Charging,
                        TransactionId = 9007,
                        Currency = "eur",
                        CreatedAtUtc = stoppedAt.AddHours(-1),
                        UpdatedAtUtc = stoppedAt
                    },
                    new ChargePaymentReservation
                    {
                        ReservationId = activeReservationId,
                        ChargePointId = "CP-MULTI",
                        ConnectorId = 2,
                        ChargeTagId = "TAG-ACTIVE",
                        OcppIdTag = "TAG-ACTIVE",
                        StripePaymentIntentId = "pi_active",
                        Status = PaymentReservationStatus.Charging,
                        TransactionId = 9008,
                        Currency = "eur",
                        CreatedAtUtc = now.AddMinutes(-10),
                        UpdatedAtUtc = now.AddMinutes(-1)
                    });
                db.SaveChanges();
            }

            var service = new CleanupServiceHarness(
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<IConfiguration>(),
                utcNow: now);

            await service.RunOnce();

            using var verificationScope = provider.CreateScope();
            var verificationDb = verificationScope.ServiceProvider.GetRequiredService<OCPPCoreContext>();
            Assert.Equal(
                PaymentReservationStatus.Completed,
                verificationDb.ChargePaymentReservations.Single(r => r.ReservationId == recoveredReservationId).Status);
            Assert.Equal(
                PaymentReservationStatus.Charging,
                verificationDb.ChargePaymentReservations.Single(r => r.ReservationId == activeReservationId).Status);
            Assert.Null(verificationDb.Transactions.Single(t => t.TransactionId == 9008).StopTime);
            Assert.Equal(
                "Occupied",
                verificationDb.ConnectorStatuses.Single(c => c.ChargePointId == "CP-MULTI" && c.ConnectorId == 2).LastStatus);
            Assert.Equal(new[] { 9007 }, coordinator.CompleteCalls);
        }

        [Fact]
        public async Task CleanupAsync_RetriesClosedChargingRecoveryAfterDatabaseFull_WithoutDuplicateProviderCompletion()
        {
            var now = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
            var stoppedAt = now.AddMinutes(-5);
            var availableAt = stoppedAt.AddSeconds(3);
            var settings = new Dictionary<string, string?>
            {
                ["Maintenance:PendingPaymentTimeoutMinutes"] = "15",
                ["Maintenance:CleanupIntervalSeconds"] = "30",
                ["Maintenance:AvailableStatusOpenTransactionGraceMinutes"] = "1"
            };
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            var paymentIntents = new IdempotentCapturePaymentIntentService
            {
                Current = new PaymentIntent
                {
                    Id = "pi_disk_full",
                    Status = "requires_capture",
                    Amount = 1_000,
                    AmountCapturable = 1_000,
                    Currency = "eur"
                }
            };
            var coordinator = new StripePaymentCoordinator(
                Options.Create(new StripeOptions
                {
                    Enabled = true,
                    ApiKey = "test",
                    ReturnBaseUrl = "https://return"
                }),
                Options.Create(new PaymentFlowOptions
                {
                    MinimumSessionFeeKwh = 1.0m,
                    MinimumChargeAmountCents = 50
                }),
                NullLogger<StripePaymentCoordinator>.Instance,
                new FakeSessionService(),
                paymentIntents,
                new FakeEventFactory(),
                () => now,
                configuration: configuration);
            var databaseFull = new DatabaseFullAfterProviderCaptureInterceptor();
            using var provider = BuildProvider(
                coordinator,
                settings,
                databaseFull);

            var reservationId = Guid.NewGuid();
            var activeReservationId = Guid.NewGuid();
            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OCPPCoreContext>();
                db.Transactions.AddRange(
                    new Transaction
                    {
                        TransactionId = 9009,
                        ChargePointId = "CP-DISK-FULL",
                        ConnectorId = 1,
                        StartTagId = "TAG-DISK-FULL",
                        StartTime = stoppedAt.AddHours(-1),
                        StopTime = stoppedAt,
                        StopReason = "Local",
                        MeterStart = 10.0,
                        MeterStop = 12.0
                    },
                    new Transaction
                    {
                        TransactionId = 9010,
                        ChargePointId = "CP-DISK-FULL",
                        ConnectorId = 2,
                        StartTagId = "TAG-STILL-ACTIVE",
                        StartTime = now.AddMinutes(-10),
                        MeterStart = 20.0
                    });
                db.ConnectorStatuses.AddRange(
                    new ConnectorStatus
                    {
                        ChargePointId = "CP-DISK-FULL",
                        ConnectorId = 1,
                        LastStatus = OcppConnectorStatus.Available,
                        LastStatusTime = availableAt,
                        LastMeter = 12.0,
                        LastMeterTime = availableAt
                    },
                    new ConnectorStatus
                    {
                        ChargePointId = "CP-DISK-FULL",
                        ConnectorId = 2,
                        LastStatus = "Occupied",
                        LastStatusTime = now.AddMinutes(-1),
                        LastMeter = 21.0,
                        LastMeterTime = now.AddMinutes(-1)
                    });
                db.ChargePaymentReservations.AddRange(
                    new ChargePaymentReservation
                    {
                        ReservationId = reservationId,
                        ChargePointId = "CP-DISK-FULL",
                        ConnectorId = 1,
                        ChargeTagId = "TAG-DISK-FULL",
                        OcppIdTag = "TAG-DISK-FULL",
                        StripePaymentIntentId = "pi_disk_full",
                        Status = PaymentReservationStatus.Charging,
                        TransactionId = 9009,
                        PricePerKwh = 0.50m,
                        UserSessionFee = 0m,
                        UsageFeePerMinute = 0m,
                        StartUsageFeeAfterMinutes = 0,
                        MaxUsageFeeMinutes = 0,
                        UsageFeeAnchorMinutes = 0,
                        Currency = "eur",
                        CreatedAtUtc = stoppedAt.AddHours(-1),
                        UpdatedAtUtc = stoppedAt
                    },
                    new ChargePaymentReservation
                    {
                        ReservationId = activeReservationId,
                        ChargePointId = "CP-DISK-FULL",
                        ConnectorId = 2,
                        ChargeTagId = "TAG-STILL-ACTIVE",
                        OcppIdTag = "TAG-STILL-ACTIVE",
                        StripePaymentIntentId = "pi_still_active",
                        Status = PaymentReservationStatus.Charging,
                        TransactionId = 9010,
                        PricePerKwh = 0.50m,
                        Currency = "eur",
                        CreatedAtUtc = now.AddMinutes(-10),
                        UpdatedAtUtc = now.AddMinutes(-1)
                    });
                db.SaveChanges();
            }

            databaseFull.Arm();

            var service = new CleanupServiceHarness(
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<IConfiguration>(),
                utcNow: now);

            var exception = await Assert.ThrowsAsync<DbUpdateException>(() => service.RunOnce());
            Assert.Contains("database or disk is full", exception.Message, StringComparison.Ordinal);
            Assert.Equal(1, paymentIntents.CaptureCalls);
            Assert.Equal(1, paymentIntents.ProviderCaptureEffects);
            Assert.Contains(reservationId.ToString(), paymentIntents.CaptureIdempotencyKeys.Single(), StringComparison.Ordinal);

            using (var failedScope = provider.CreateScope())
            {
                var failedReservation = failedScope.ServiceProvider
                    .GetRequiredService<OCPPCoreContext>()
                    .ChargePaymentReservations
                    .Single(r => r.ReservationId == reservationId);
                Assert.Equal(PaymentReservationStatus.Charging, failedReservation.Status);
            }

            databaseFull.AllowSaves();
            await service.RunOnce();

            using var verificationScope = provider.CreateScope();
            var reservation = verificationScope.ServiceProvider
                .GetRequiredService<OCPPCoreContext>()
                .ChargePaymentReservations
                .Single(r => r.ReservationId == reservationId);
            Assert.Equal(PaymentReservationStatus.Completed, reservation.Status);
            Assert.Equal(100, reservation.CapturedAmountCents);
            Assert.Equal(2, paymentIntents.GetCalls);
            Assert.Equal(1, paymentIntents.CaptureCalls);
            Assert.Equal(1, paymentIntents.ProviderCaptureEffects);
            Assert.Equal(
                PaymentReservationStatus.Charging,
                verificationScope.ServiceProvider
                    .GetRequiredService<OCPPCoreContext>()
                    .ChargePaymentReservations
                    .Single(r => r.ReservationId == activeReservationId)
                    .Status);
            Assert.Null(
                verificationScope.ServiceProvider
                    .GetRequiredService<OCPPCoreContext>()
                    .Transactions
                    .Single(t => t.TransactionId == 9010)
                    .StopTime);
        }

        [Fact]
        public async Task CleanupAsync_CompletesWaitingForDisconnectReservation_WhenConnectorPersistedAvailable()
        {
            var coordinator = new RecordingPaymentCoordinator();
            using var provider = BuildProvider(
                coordinator,
                new Dictionary<string, string?>
                {
                    ["Maintenance:PendingPaymentTimeoutMinutes"] = "15",
                    ["Maintenance:CleanupIntervalSeconds"] = "30",
                    ["Maintenance:AvailableStatusOpenTransactionGraceMinutes"] = "1"
                });

            var reservationId = Guid.NewGuid();
            var stoppedAt = DateTime.UtcNow.AddMinutes(-8);
            var availableAt = stoppedAt.AddSeconds(3);

            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OCPPCoreContext>();
                db.Transactions.Add(new Transaction
                {
                    TransactionId = 9003,
                    ChargePointId = "CP-WAITING",
                    ConnectorId = 1,
                    StartTagId = "TAG-WAITING",
                    StartTime = stoppedAt.AddHours(-1),
                    StopTime = stoppedAt,
                    StopReason = "EVDisconnected",
                    MeterStart = 20.0,
                    MeterStop = 24.0,
                    ChargingEndedAtUtc = stoppedAt
                });
                db.ConnectorStatuses.Add(new ConnectorStatus
                {
                    ChargePointId = "CP-WAITING",
                    ConnectorId = 1,
                    LastStatus = "Available",
                    LastStatusTime = availableAt,
                    LastMeter = 24.0,
                    LastMeterTime = availableAt
                });
                db.ChargePaymentReservations.Add(new ChargePaymentReservation
                {
                    ReservationId = reservationId,
                    ChargePointId = "CP-WAITING",
                    ConnectorId = 1,
                    ChargeTagId = "TAG-WAITING",
                    OcppIdTag = "TAG-WAITING",
                    StripePaymentIntentId = "pi_waiting",
                    Status = PaymentReservationStatus.WaitingForDisconnect,
                    TransactionId = 9003,
                    StopTransactionAtUtc = stoppedAt,
                    Currency = "eur",
                    CreatedAtUtc = stoppedAt.AddHours(-1),
                    UpdatedAtUtc = stoppedAt
                });
                db.SaveChanges();
            }

            var service = new CleanupServiceHarness(
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<IConfiguration>());

            await service.RunOnce();

            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OCPPCoreContext>();
                var reservation = db.ChargePaymentReservations.Single(r => r.ReservationId == reservationId);

                Assert.Equal(PaymentReservationStatus.Completed, reservation.Status);
                Assert.Equal(stoppedAt, reservation.StopTransactionAtUtc);
                Assert.Equal(stoppedAt, reservation.DisconnectedAtUtc);
            }

            Assert.Equal(new[] { 9003 }, coordinator.CompleteCalls);
        }

        [Fact]
        public async Task CleanupAsync_LogsRecoveryDiagnostic_WhenConnectorPersistedAvailable()
        {
            var coordinator = new RecordingPaymentCoordinator();
            var logger = new RecordingLogger<PaymentReservationCleanupService>();
            using var provider = BuildProvider(
                coordinator,
                new Dictionary<string, string?>
                {
                    ["Maintenance:PendingPaymentTimeoutMinutes"] = "15",
                    ["Maintenance:CleanupIntervalSeconds"] = "30",
                    ["Maintenance:AvailableStatusOpenTransactionGraceMinutes"] = "1"
                });

            var reservationId = Guid.NewGuid();
            var availableAt = DateTime.UtcNow.AddMinutes(-5);

            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OCPPCoreContext>();
                db.Transactions.Add(new Transaction
                {
                    TransactionId = 9002,
                    ChargePointId = "CP-DIAG",
                    ConnectorId = 1,
                    StartTagId = "TAG-DIAG",
                    StartTime = availableAt.AddHours(-2),
                    MeterStart = 30.0
                });
                db.ConnectorStatuses.Add(new ConnectorStatus
                {
                    ChargePointId = "CP-DIAG",
                    ConnectorId = 1,
                    LastStatus = "Available",
                    LastStatusTime = availableAt,
                    LastMeter = 34.75,
                    LastMeterTime = availableAt.AddMinutes(-1)
                });
                db.ChargePaymentReservations.Add(new ChargePaymentReservation
                {
                    ReservationId = reservationId,
                    ChargePointId = "CP-DIAG",
                    ConnectorId = 1,
                    ChargeTagId = "TAG-DIAG",
                    OcppIdTag = "TAG-DIAG",
                    StripePaymentIntentId = "pi_diag",
                    Status = PaymentReservationStatus.Charging,
                    TransactionId = 9002,
                    Currency = "eur",
                    CreatedAtUtc = availableAt.AddHours(-2),
                    UpdatedAtUtc = availableAt.AddHours(-2)
                });
                db.SaveChanges();
            }

            var service = new CleanupServiceHarness(
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<IConfiguration>(),
                logger);

            await service.RunOnce();

            Assert.Contains(logger.Entries, entry =>
                entry.Level == LogLevel.Warning &&
                entry.Message.Contains("Open transaction recovery candidate", StringComparison.Ordinal) &&
                entry.Message.Contains("cp=CP-DIAG", StringComparison.Ordinal) &&
                entry.Message.Contains("connector=1", StringComparison.Ordinal) &&
                entry.Message.Contains("tx=9002", StringComparison.Ordinal) &&
                entry.Message.Contains("reservation=", StringComparison.Ordinal) &&
                entry.Message.Contains("status=Available", StringComparison.Ordinal) &&
                entry.Message.Contains("reservationStatus=Charging", StringComparison.Ordinal));
        }

        [Fact]
        public async Task CleanupAsync_DoesNotChangeReservations_ThatAreStillValid()
        {
            var coordinator = new RecordingPaymentCoordinator();
            using var provider = BuildProvider(
                coordinator,
                new Dictionary<string, string?>
                {
                    ["Maintenance:PendingPaymentTimeoutMinutes"] = "5",
                    ["Maintenance:CleanupIntervalSeconds"] = "30"
                });

            Guid pendingId;
            Guid authorizedId;
            Guid startedId;

            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OCPPCoreContext>();

                var pending = new ChargePaymentReservation
                {
                    ReservationId = Guid.NewGuid(),
                    ChargePointId = "CP1",
                    ConnectorId = 1,
                    ChargeTagId = "TAG1",
                    Status = PaymentReservationStatus.Pending,
                    Currency = "eur",
                    CreatedAtUtc = DateTime.UtcNow.AddMinutes(-1),
                    UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-1)
                };

                var authorized = new ChargePaymentReservation
                {
                    ReservationId = Guid.NewGuid(),
                    ChargePointId = "CP1",
                    ConnectorId = 2,
                    ChargeTagId = "TAG2",
                    Status = PaymentReservationStatus.Authorized,
                    StartDeadlineAtUtc = DateTime.UtcNow.AddMinutes(5),
                    Currency = "eur",
                    CreatedAtUtc = DateTime.UtcNow.AddMinutes(-1),
                    UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-1)
                };

                var started = new ChargePaymentReservation
                {
                    ReservationId = Guid.NewGuid(),
                    ChargePointId = "CP1",
                    ConnectorId = 3,
                    ChargeTagId = "TAG3",
                    Status = PaymentReservationStatus.StartRequested,
                    StartDeadlineAtUtc = DateTime.UtcNow.AddMinutes(-5),
                    TransactionId = 1001,
                    Currency = "eur",
                    CreatedAtUtc = DateTime.UtcNow.AddMinutes(-10),
                    UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-10)
                };

                db.ChargePaymentReservations.AddRange(pending, authorized, started);
                db.SaveChanges();

                pendingId = pending.ReservationId;
                authorizedId = authorized.ReservationId;
                startedId = started.ReservationId;
            }

            var service = new CleanupServiceHarness(
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<IConfiguration>());

            await service.RunOnce();

            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OCPPCoreContext>();
                var pending = db.ChargePaymentReservations.Single(r => r.ReservationId == pendingId);
                var authorized = db.ChargePaymentReservations.Single(r => r.ReservationId == authorizedId);
                var started = db.ChargePaymentReservations.Single(r => r.ReservationId == startedId);

                Assert.Equal(PaymentReservationStatus.Pending, pending.Status);
                Assert.Equal(PaymentReservationStatus.Authorized, authorized.Status);
                Assert.Equal(PaymentReservationStatus.StartRequested, started.Status);
            }

            Assert.Empty(coordinator.CancelCalls);
        }

        private static ServiceProvider BuildProvider(
            IPaymentCoordinator coordinator,
            IDictionary<string, string?> configurationData,
            Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor? saveChangesInterceptor = null)
        {
            var dbName = Guid.NewGuid().ToString();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(
                new ConfigurationBuilder()
                    .AddInMemoryCollection(configurationData)
                    .Build());
            services.AddDbContext<OCPPCoreContext>(options =>
            {
                options.UseInMemoryDatabase(dbName);
                if (saveChangesInterceptor != null)
                {
                    options.AddInterceptors(saveChangesInterceptor);
                }
            });
            services.AddSingleton<IPaymentCoordinator>(coordinator);
            return services.BuildServiceProvider();
        }

        private static ChargePaymentReservation NewTerminalReservation(
            string paymentIntentId,
            string? releaseState,
            DateTime? nextAttemptAtUtc)
        {
            return new ChargePaymentReservation
            {
                ReservationId = Guid.NewGuid(),
                ChargePointId = "CP1",
                ConnectorId = Math.Abs(paymentIntentId.GetHashCode()) % 10000 + 1,
                ChargeTagId = paymentIntentId,
                StripePaymentIntentId = paymentIntentId,
                Status = PaymentReservationStatus.Abandoned,
                Currency = "eur",
                CreatedAtUtc = DateTime.UtcNow.AddHours(-1),
                UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-5),
                AuthorizationReleaseState = releaseState,
                AuthorizationReleaseNextAttemptAtUtc = nextAttemptAtUtc
            };
        }
    }

    internal sealed class CleanupServiceHarness : PaymentReservationCleanupService
    {
        private readonly DateTime? _utcNow;

        public CleanupServiceHarness(
            IServiceScopeFactory scopeFactory,
            IConfiguration configuration,
            ILogger<PaymentReservationCleanupService>? logger = null,
            DateTime? utcNow = null)
            : base(
                scopeFactory,
                logger ?? NullLogger<PaymentReservationCleanupService>.Instance,
                configuration,
                Options.Create(new PaymentFlowOptions { StartWindowMinutes = 7 }))
        {
            _utcNow = utcNow;
        }

        public Task RunOnce(CancellationToken token = default) => CleanupAsync(token);

        protected override DateTime UtcNow => _utcNow ?? base.UtcNow;
    }

    internal sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception), exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose()
            {
            }
        }
    }

    internal class RecordingPaymentCoordinator : IPaymentCoordinator
    {
        public bool IsEnabled => true;
        public List<(Guid ReservationId, string Reason)> CancelCalls { get; } = new();
        public List<(Guid ReservationId, string Trigger)> ReconcileCalls { get; } = new();
        public List<int> CompleteCalls { get; } = new();
        public string? ReconcileErrorToRecord { get; set; }
        public string ReconcileOutcome { get; set; } = PaymentAuthorizationReleaseOutcome.SkippedNotEligible;

        public PaymentSessionResult CreateCheckoutSession(OCPPCoreContext dbContext, PaymentSessionRequest request) =>
            throw new NotImplementedException();

        public PaymentConfirmationResult ConfirmReservation(OCPPCoreContext dbContext, Guid reservationId, string checkoutSessionId) =>
            throw new NotImplementedException();

        public PaymentResumeResult ResumeReservation(OCPPCoreContext dbContext, Guid reservationId) =>
            throw new NotImplementedException();

        public PaymentR1InvoiceResult RequestR1Invoice(OCPPCoreContext dbContext, PaymentR1InvoiceRequest request) =>
            throw new NotImplementedException();

        public void CancelReservation(OCPPCoreContext dbContext, Guid reservationId, string reason) =>
            throw new NotImplementedException();

        public void CancelPaymentIntentIfCancelable(OCPPCoreContext dbContext, ChargePaymentReservation reservation, string reason)
        {
            CancelCalls.Add((reservation?.ReservationId ?? Guid.Empty, reason));
        }

        public PaymentAuthorizationReleaseResult ReconcileTerminalPaymentAuthorization(
            OCPPCoreContext dbContext,
            ChargePaymentReservation reservation,
            string trigger)
        {
            ReconcileCalls.Add((reservation?.ReservationId ?? Guid.Empty, trigger));
            if (reservation != null && !string.IsNullOrWhiteSpace(ReconcileErrorToRecord))
            {
                reservation.AuthorizationReleaseLastError = ReconcileErrorToRecord;
                dbContext.SaveChanges();
            }
            return new PaymentAuthorizationReleaseResult
            {
                Outcome = ReconcileOutcome
            };
        }

        public void MarkTransactionStarted(OCPPCoreContext dbContext, string chargePointId, int connectorId, string chargeTagId, int transactionId) =>
            throw new NotImplementedException();

        public virtual void CompleteReservation(OCPPCoreContext dbContext, Transaction transaction)
        {
            CompleteCalls.Add(transaction.TransactionId);

            var reservation = dbContext.ChargePaymentReservations.SingleOrDefault(r => r.TransactionId == transaction.TransactionId);
            if (reservation == null)
            {
                return;
            }

            reservation.Status = PaymentReservationStatus.Completed;
            reservation.StopTransactionAtUtc = transaction.StopTime;
            reservation.DisconnectedAtUtc = transaction.StopTime;
            reservation.UpdatedAtUtc = transaction.StopTime ?? DateTime.UtcNow;
            dbContext.SaveChanges();
        }

        public void HandleConnectorAvailable(OCPPCoreContext dbContext, string chargePointId, int connectorId, DateTime disconnectedAtUtc) =>
            throw new NotImplementedException();

        public void HandleWebhookEvent(OCPPCoreContext dbContext, string payload, string signatureHeader) =>
            throw new NotImplementedException();
    }

    internal sealed class IdempotentCapturePaymentIntentService : IStripePaymentIntentService
    {
        public PaymentIntent Current { get; set; } = new();
        public int GetCalls { get; private set; }
        public int CaptureCalls { get; private set; }
        public int ProviderCaptureEffects { get; private set; }
        public List<string> CaptureIdempotencyKeys { get; } = new();

        public PaymentIntent Get(string id)
        {
            GetCalls++;
            return Current;
        }

        public PaymentIntent Update(
            string id,
            PaymentIntentUpdateOptions options,
            RequestOptions requestOptions = null!)
        {
            Current.Metadata = options?.Metadata ?? Current.Metadata;
            return Current;
        }

        public PaymentIntent Capture(
            string id,
            PaymentIntentCaptureOptions options,
            RequestOptions requestOptions = null!)
        {
            CaptureCalls++;
            CaptureIdempotencyKeys.Add(requestOptions?.IdempotencyKey ?? string.Empty);

            if (string.Equals(Current.Status, "requires_capture", StringComparison.OrdinalIgnoreCase))
            {
                ProviderCaptureEffects++;
                Current = new PaymentIntent
                {
                    Id = id,
                    Status = "succeeded",
                    Amount = Current.Amount,
                    AmountReceived = options.AmountToCapture ?? 0,
                    Currency = Current.Currency
                };
            }

            return Current;
        }

        public PaymentIntent Cancel(string id, RequestOptions requestOptions = null!) =>
            throw new InvalidOperationException("Cancellation is outside this completion regression.");
    }

    internal sealed class DatabaseFullAfterProviderCaptureInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        private bool _armed;
        private int _synchronousSaves;

        public void Arm()
        {
            _armed = true;
            _synchronousSaves = 0;
        }

        public void AllowSaves()
        {
            _armed = false;
        }

        public override Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> SavingChanges(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result)
        {
            if (_armed && ++_synchronousSaves >= 2)
            {
                throw DatabaseFullException();
            }

            return base.SavingChanges(eventData, result);
        }

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (_armed)
            {
                throw DatabaseFullException();
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        private static DbUpdateException DatabaseFullException() =>
            new("SQLite Error 13: 'database or disk is full'.");
    }
}
