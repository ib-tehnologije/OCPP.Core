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
                    StripeCheckoutSessionId = "sess_late_cleanup",
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
            IDictionary<string, string?> configurationData)
        {
            var dbName = Guid.NewGuid().ToString();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(
                new ConfigurationBuilder()
                    .AddInMemoryCollection(configurationData)
                    .Build());
            services.AddDbContext<OCPPCoreContext>(options =>
                options.UseInMemoryDatabase(dbName));
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

    internal sealed class RecordingPaymentCoordinator : IPaymentCoordinator
    {
        public bool IsEnabled => true;
        public List<(Guid ReservationId, string Reason)> CancelCalls { get; } = new();
        public List<(Guid ReservationId, string Trigger)> ReconcileCalls { get; } = new();
        public List<int> CompleteCalls { get; } = new();
        public string? ReconcileErrorToRecord { get; set; }

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
                Outcome = PaymentAuthorizationReleaseOutcome.SkippedNotEligible
            };
        }

        public void MarkTransactionStarted(OCPPCoreContext dbContext, string chargePointId, int connectorId, string chargeTagId, int transactionId) =>
            throw new NotImplementedException();

        public void CompleteReservation(OCPPCoreContext dbContext, Transaction transaction)
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
}
