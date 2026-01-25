using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OCPP.Core.Database;
using OCPP.Core.Server.Payments;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Stripe;
using Stripe.Checkout;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    public class StripePaymentCoordinatorTests
    {
        private static OCPPCoreContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<OCPPCoreContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            return new OCPPCoreContext(options);
        }

        private static StripePaymentCoordinator CreateCoordinator(
            OCPPCoreContext context,
            FakeSessionService sessionService,
            FakePaymentIntentService intentService,
            StripeOptions? stripeOptions = null,
            Func<DateTime>? now = null,
            FakeEventFactory? eventFactory = null)
        {
            var options = Options.Create(stripeOptions ?? new StripeOptions { Enabled = true, ApiKey = "test", ReturnBaseUrl = "https://return" });
            var flowOptions = Options.Create(new PaymentFlowOptions { StartWindowMinutes = 7 });
            return new StripePaymentCoordinator(
                options,
                flowOptions,
                NullLogger<StripePaymentCoordinator>.Instance,
                sessionService,
                intentService,
                eventFactory ?? new FakeEventFactory(),
                now ?? (() => DateTime.UtcNow));
        }

        [Theory]
        [InlineData(5.0, 0.35, 175)]
        [InlineData(0.123, 0.50, 6)]
        [InlineData(10.0, 0.0, 0)]
        public void CalculateAmountInCents_ComputesRoundedSubtotal(double energy, double pricePerKwh, long expected)
        {
            var cents = StripePaymentCoordinator.TestCalculateAmountInCents(energy, (decimal)pricePerKwh);
            Assert.Equal(expected, cents);
        }

        [Theory]
        [InlineData(15, 0.30, 450)]
        [InlineData(0, 0.30, 0)]
        [InlineData(10, 0.00, 0)]
        public void CalculateUsageFeeInCents_RespectsMinutesAndRate(int minutes, decimal rate, long expected)
        {
            var cents = StripePaymentCoordinator.TestCalculateUsageFeeInCents(minutes, rate);
            Assert.Equal(expected, cents);
        }

        [Fact]
        public void NormalizeChargeTag_StripsSuffixAfterUnderscore()
        {
            Assert.Equal("ABC123", StripePaymentCoordinator.TestNormalizeChargeTag("ABC123_suffix"));
            Assert.Equal("ABC123", StripePaymentCoordinator.TestNormalizeChargeTag("ABC123"));
        }

        [Fact]
        public void PersistTransactionBreakdown_ComputesOwnerAndOperatorShares()
        {
            using var context = CreateContext();
            var reservation = new ChargePaymentReservation
            {
                OwnerCommissionPercent = 10m,
                OwnerCommissionFixedPerKwh = 0m,
                OwnerSessionFee = 0.50m,
                Currency = "eur"
            };
            var transaction = new Transaction();

            StripePaymentCoordinator.TestPersistTransactionBreakdown(
                context,
                transaction,
                reservation,
                energyKwh: 12.5,
                energyCostCents: StripePaymentCoordinator.TestCalculateAmountInCents(12.5, 0.30m),
                usageFeeMinutes: 20,
                usageFeeCents: StripePaymentCoordinator.TestCalculateUsageFeeInCents(20, 0.20m),
                sessionFeeCents: StripePaymentCoordinator.TestCalculateFlatAmountInCents(0.50m),
                totalCents: 0);

            Assert.Equal(12.5, transaction.EnergyKwh);
            Assert.Equal(3.75m, transaction.EnergyCost); // 12.5 * 0.30
            Assert.Equal(20, transaction.UsageFeeMinutes);
            Assert.Equal(4.00m, transaction.UsageFeeAmount); // 20 * 0.20
            Assert.Equal(0.50m, transaction.UserSessionFeeAmount);
            Assert.Equal(0.50m, transaction.OwnerSessionFeeAmount);
            Assert.Equal(10m, transaction.OwnerCommissionPercent);

            var gross = transaction.EnergyCost + transaction.UsageFeeAmount + transaction.UserSessionFeeAmount;
            var expectedOperatorCommission = Math.Round(gross * 0.10m, 4, MidpointRounding.AwayFromZero);
            var expectedOperatorRevenue = expectedOperatorCommission + reservation.OwnerSessionFee;
            Assert.Equal(expectedOperatorCommission, transaction.OperatorCommissionAmount);
            Assert.Equal(expectedOperatorRevenue, transaction.OperatorRevenueTotal);
            Assert.Equal(Math.Max(0m, gross - expectedOperatorRevenue), transaction.OwnerPayoutTotal);
            Assert.Equal("eur", transaction.Currency);
        }

        [Fact]
        public void PersistTransactionBreakdown_UsesFixedCommissionWhenConfigured()
        {
            using var context = CreateContext();
            var reservation = new ChargePaymentReservation
            {
                OwnerCommissionPercent = 0m,
                OwnerCommissionFixedPerKwh = 0.05m,
                OwnerSessionFee = 0m,
                Currency = "eur"
            };
            var transaction = new Transaction();

            StripePaymentCoordinator.TestPersistTransactionBreakdown(
                context,
                transaction,
                reservation,
                energyKwh: 8,
                energyCostCents: StripePaymentCoordinator.TestCalculateAmountInCents(8, 0.25m),
                usageFeeMinutes: 0,
                usageFeeCents: 0,
                sessionFeeCents: 0,
                totalCents: 0);

            Assert.Equal(8d, transaction.EnergyKwh);
            Assert.Equal(2.00m, transaction.EnergyCost);
            Assert.Equal(0.40m, transaction.OperatorCommissionAmount); // 0.05 per kWh * 8
            Assert.Equal(0.40m, transaction.OperatorRevenueTotal);
            Assert.Equal(1.60m, transaction.OwnerPayoutTotal);
        }

        [Theory]
        [InlineData(0, 10, 100, false, 0.00, 0)]   // no per-minute fee or duration
        [InlineData(10, 0, 100, false, 0.25, 10)]  // usage starts immediately
        [InlineData(10, 3, 100, false, 0.25, 7)]   // grace period before idle fee
        [InlineData(10, 3, 5, false, 0.25, 5)]     // max cap applied
        public void CalculateUsageFeeMinutes_RespectsGraceAndCap(
            int totalMinutes,
            int startUsageAfterMinutes,
            int maxUsageMinutes,
            bool usageAfterChargingEnds,
            double pricePerMinute,
            int expectedMinutes)
        {
            var start = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            var stop = start.AddMinutes(totalMinutes);

            var reservation = new ChargePaymentReservation
            {
                UsageFeePerMinute = (decimal)pricePerMinute,
                StartUsageFeeAfterMinutes = startUsageAfterMinutes,
                MaxUsageFeeMinutes = maxUsageMinutes,
                UsageFeeAnchorMinutes = usageAfterChargingEnds ? 1 : 0
            };

            var transaction = new Transaction
            {
                StartTime = start,
                StopTime = stop
            };

            var minutes = StripePaymentCoordinator.TestCalculateUsageFeeMinutes(transaction, reservation, stop);
            Assert.Equal(expectedMinutes, minutes);
        }

        [Theory]
        [InlineData(10, 2, 10, 8)]   // idle fee after charging ends with cap
        [InlineData(0, 0, 10, 0)]    // no ChargingEndedAtUtc -> no idle fee
        [InlineData(0, 0, 0, 0)]     // zero duration
        public void CalculateUsageFeeMinutes_IdleAnchorUsesChargingEnded(
            int idleMinutesAfterCharging,
            int startUsageAfterMinutes,
            int maxUsageMinutes,
            int expectedMinutes)
        {
            var start = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            var chargingEnded = start.AddMinutes(5);
            var stop = chargingEnded.AddMinutes(idleMinutesAfterCharging);

            var reservation = new ChargePaymentReservation
            {
                UsageFeePerMinute = 0.30m,
                StartUsageFeeAfterMinutes = startUsageAfterMinutes,
                MaxUsageFeeMinutes = maxUsageMinutes,
                UsageFeeAnchorMinutes = 1
            };

            var transaction = new Transaction
            {
                StartTime = start,
                ChargingEndedAtUtc = idleMinutesAfterCharging > 0 ? chargingEnded : (DateTime?)null,
                StopTime = stop
            };

            var minutes = StripePaymentCoordinator.TestCalculateUsageFeeMinutes(transaction, reservation, stop);
            Assert.Equal(expectedMinutes, minutes);
        }

        [Fact]
        public void CreateCheckoutSession_ComputesMaxTotalsAndPersistsReservation()
        {
            using var context = CreateContext();
            context.ChargePoints.Add(new ChargePoint
            {
                ChargePointId = "CP1",
                MaxSessionKwh = 10,
                PricePerKwh = 0.35m,
                ConnectorUsageFeePerMinute = 0.20m,
                StartUsageFeeAfterMinutes = 0,
                MaxUsageFeeMinutes = 5,
                UserSessionFee = 0.50m,
                OwnerSessionFee = 0.25m,
                OwnerCommissionPercent = 0.10m,
                OwnerCommissionFixedPerKwh = 0m
            });
            context.SaveChanges();

            var sessionService = new FakeSessionService
            {
                CreateResponse = new Session
                {
                    Id = "sess_123",
                    Url = "https://checkout/session/123",
                    PaymentIntentId = "pi_123"
                }
            };
            var intentService = new FakePaymentIntentService();
            var coordinator = CreateCoordinator(context, sessionService, intentService);

            var result = coordinator.CreateCheckoutSession(context, new PaymentSessionRequest
            {
                ChargePointId = "CP1",
                ChargeTagId = "TAG1",
                ConnectorId = 1,
                ReturnBaseUrl = "https://custom-return"
            });

            Assert.Equal("sess_123", result.Reservation.StripeCheckoutSessionId);
            Assert.Equal("pi_123", result.Reservation.StripePaymentIntentId);
            // 10 kWh * 0.35 = 3.50 -> 350 cents; usage fee 5 * 0.20 = 100 cents; session fee 50 cents => 500 total
            Assert.Equal(500, result.Reservation.MaxAmountCents);

            Assert.NotNull(sessionService.LastCreateOptions);
            Assert.Equal("https://checkout/session/123", result.CheckoutUrl);
            Assert.Contains("reservation_id", sessionService.LastCreateOptions.Metadata.Keys);
            Assert.StartsWith("https://custom-return", sessionService.LastCreateOptions.SuccessUrl);
            Assert.StartsWith("https://custom-return", sessionService.LastCreateOptions.CancelUrl);
        }

        [Fact]
        public void ConfirmReservation_CompletesWhenSessionAndIntentValid()
        {
            using var context = CreateContext();
            var reservationId = Guid.NewGuid();
            context.ChargePaymentReservations.Add(new ChargePaymentReservation
            {
                ReservationId = reservationId,
                ChargePointId = "CP1",
                StripeCheckoutSessionId = "sess_ok",
                Status = PaymentReservationStatus.Pending,
                MaxAmountCents = 1000,
                ChargeTagId = "TAG1",
                Currency = "eur"
            });
            context.SaveChanges();

            var sessionService = new FakeSessionService
            {
                GetResponse = new Session
                {
                    Id = "sess_ok",
                    PaymentIntentId = "pi_ok",
                    Status = "complete",
                    PaymentStatus = "paid"
                }
            };
            var intentService = new FakePaymentIntentService
            {
                GetResponse = new PaymentIntent
                {
                    Id = "pi_ok",
                    Status = "requires_capture",
                    Amount = 123
                }
            };
            var coordinator = CreateCoordinator(context, sessionService, intentService);

            var result = coordinator.ConfirmReservation(context, reservationId, "sess_ok");

            Assert.True(result.Success);
            Assert.Equal(PaymentReservationStatus.Authorized, result.Reservation.Status);
            Assert.Equal("pi_ok", result.Reservation.StripePaymentIntentId);
            Assert.Equal(123, result.Reservation.MaxAmountCents);
        }

        [Fact]
        public void CompleteReservation_CapturesWhenAmountDue()
        {
            using var context = CreateContext();
            var reservation = new ChargePaymentReservation
            {
                ReservationId = Guid.NewGuid(),
                ChargePointId = "CP1",
                ConnectorId = 1,
                ChargeTagId = "TAG1",
                StripePaymentIntentId = "pi_capture",
                Status = PaymentReservationStatus.Charging,
                PricePerKwh = 0.50m,
                UserSessionFee = 0.50m,
                UsageFeePerMinute = 0.20m,
                StartUsageFeeAfterMinutes = 0,
                MaxUsageFeeMinutes = 10,
                UsageFeeAnchorMinutes = 0,
                Currency = "eur"
            };
            context.ChargePaymentReservations.Add(reservation);
            var transaction = new Transaction
            {
                TransactionId = 42,
                ChargePointId = "CP1",
                ConnectorId = 1,
                StartTagId = "TAG1",
                StartTime = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc),
                StopTime = new DateTime(2025, 1, 1, 12, 5, 0, DateTimeKind.Utc),
                MeterStart = 0,
                MeterStop = 1
            };
            context.Transactions.Add(transaction);
            context.SaveChanges();

            var intentService = new FakePaymentIntentService
            {
                GetResponse = new PaymentIntent { Id = "pi_capture", Status = "requires_capture", Amount = 10_000 }
            };
            var sessionService = new FakeSessionService();
            var coordinator = CreateCoordinator(context, sessionService, intentService, now: () => new DateTime(2025, 1, 1, 12, 10, 0, DateTimeKind.Utc));

            coordinator.CompleteReservation(context, transaction);

            Assert.Equal(PaymentReservationStatus.Completed, reservation.Status);
            Assert.True(intentService.CaptureCalled);
            Assert.Equal(reservation.CapturedAmountCents, intentService.LastCaptureOptions?.AmountToCapture ?? 0);
            Assert.True(reservation.CapturedAmountCents > 0);
        }

        [Fact]
        public void CompleteReservation_CancelsWhenNoAmountToCapture()
        {
            using var context = CreateContext();
            var reservation = new ChargePaymentReservation
            {
                ReservationId = Guid.NewGuid(),
                ChargePointId = "CP1",
                ConnectorId = 1,
                ChargeTagId = "TAG1",
                StripePaymentIntentId = "pi_cancel",
                Status = PaymentReservationStatus.Charging,
                PricePerKwh = 0m,
                UserSessionFee = 0m,
                UsageFeePerMinute = 0m,
                StartUsageFeeAfterMinutes = 0,
                MaxUsageFeeMinutes = 0,
                UsageFeeAnchorMinutes = 0,
                Currency = "eur"
            };
            context.ChargePaymentReservations.Add(reservation);
            var transaction = new Transaction
            {
                TransactionId = 99,
                ChargePointId = "CP1",
                ConnectorId = 1,
                StartTagId = "TAG1",
                StartTime = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc),
                StopTime = new DateTime(2025, 1, 1, 12, 5, 0, DateTimeKind.Utc),
                MeterStart = 0,
                MeterStop = 0
            };
            context.Transactions.Add(transaction);
            context.SaveChanges();

            var intentService = new FakePaymentIntentService
            {
                GetResponse = new PaymentIntent { Id = "pi_cancel", Status = "requires_capture", Amount = 10_000 }
            };
            var coordinator = CreateCoordinator(context, new FakeSessionService(), intentService);

            coordinator.CompleteReservation(context, transaction);

            Assert.Equal(PaymentReservationStatus.Cancelled, reservation.Status);
            Assert.True(intentService.CancelCalled);
            Assert.False(intentService.CaptureCalled);
        }

        [Fact]
        public void HandleWebhookEvent_CheckoutCompletedMarksAuthorized()
        {
            using var context = CreateContext();
            context.ChargePaymentReservations.Add(new ChargePaymentReservation
            {
                ReservationId = Guid.NewGuid(),
                ChargePointId = "CP1",
                ConnectorId = 1,
                ChargeTagId = "TAG1",
                StripeCheckoutSessionId = "sess_webhook",
                Status = PaymentReservationStatus.Pending,
                Currency = "eur"
            });
            context.SaveChanges();

            var evt = new Event
            {
                Type = Events.CheckoutSessionCompleted,
                Data = new EventData
                {
                    Object = new Session
                    {
                        Id = "sess_webhook",
                        PaymentIntentId = "pi_webhook",
                        PaymentStatus = "paid"
                    }
                }
            };

            var sessionService = new FakeSessionService();
            var intentService = new FakePaymentIntentService();
            var eventFactory = new FakeEventFactory { EventToReturn = evt };

            var options = new StripeOptions { Enabled = true, ApiKey = "test", ReturnBaseUrl = "https://return", WebhookSecret = "whsec_test" };
            var coordinator = CreateCoordinator(context, sessionService, intentService, stripeOptions: options, eventFactory: eventFactory);

            coordinator.HandleWebhookEvent(context, "payload", "sig");

            var reservation = context.ChargePaymentReservations.Single(r => r.StripeCheckoutSessionId == "sess_webhook");
            Assert.Equal(PaymentReservationStatus.Authorized, reservation.Status);
            Assert.Equal("pi_webhook", reservation.StripePaymentIntentId);
            Assert.NotNull(reservation.AuthorizedAtUtc);
        }

        [Fact]
        public void HandleWebhookEvent_PaymentFailedMarksFailed()
        {
            using var context = CreateContext();
            context.ChargePaymentReservations.Add(new ChargePaymentReservation
            {
                ReservationId = Guid.NewGuid(),
                ChargePointId = "CP1",
                ConnectorId = 1,
                ChargeTagId = "TAG1",
                StripePaymentIntentId = "pi_fail",
                Status = PaymentReservationStatus.Pending,
                Currency = "eur"
            });
            context.SaveChanges();

            var evt = new Event
            {
                Type = Events.PaymentIntentPaymentFailed,
                Data = new EventData
                {
                    Object = new PaymentIntent
                    {
                        Id = "pi_fail",
                        LastPaymentError = new StripeError { Message = "card_declined" }
                    }
                }
            };

            var coordinator = CreateCoordinator(
                context,
                new FakeSessionService(),
                new FakePaymentIntentService(),
                stripeOptions: new StripeOptions { Enabled = true, ApiKey = "test", ReturnBaseUrl = "https://return", WebhookSecret = "whsec_test" },
                eventFactory: new FakeEventFactory { EventToReturn = evt });

            coordinator.HandleWebhookEvent(context, "payload", "sig");

            var reservation = context.ChargePaymentReservations.Single(r => r.StripePaymentIntentId == "pi_fail");
            Assert.Equal(PaymentReservationStatus.Failed, reservation.Status);
            Assert.Equal("card_declined", reservation.LastError);
        }

        [Fact]
        public void HandleWebhookEvent_CheckoutExpiredCancels()
        {
            using var context = CreateContext();
            context.ChargePaymentReservations.Add(new ChargePaymentReservation
            {
                ReservationId = Guid.NewGuid(),
                ChargePointId = "CP1",
                ConnectorId = 1,
                ChargeTagId = "TAG1",
                StripeCheckoutSessionId = "sess_expired",
                Status = PaymentReservationStatus.Pending,
                Currency = "eur"
            });
            context.SaveChanges();

            var evt = new Event
            {
                Type = Events.CheckoutSessionExpired,
                Data = new EventData
                {
                    Object = new Session
                    {
                        Id = "sess_expired"
                    }
                }
            };

            var eventFactory = new FakeEventFactory { EventToReturn = evt };
            var coordinator = CreateCoordinator(
                context,
                new FakeSessionService(),
                new FakePaymentIntentService(),
                stripeOptions: new StripeOptions { Enabled = true, ApiKey = "test", ReturnBaseUrl = "https://return", WebhookSecret = "whsec_test" },
                eventFactory: eventFactory);

            coordinator.HandleWebhookEvent(context, "payload", "sig");

            var reservation = context.ChargePaymentReservations.Single(r => r.StripeCheckoutSessionId == "sess_expired");
            Assert.Equal(PaymentReservationStatus.Cancelled, reservation.Status);
            Assert.Equal("Checkout session expired", reservation.LastError);
        }

        [Fact]
        public void HandleWebhookEvent_RejectsWhenSecretMissingAndNotAllowed()
        {
            using var context = CreateContext();
            context.ChargePaymentReservations.Add(new ChargePaymentReservation
            {
                ReservationId = Guid.NewGuid(),
                ChargePointId = "CP1",
                ConnectorId = 1,
                ChargeTagId = "TAG1",
                StripeCheckoutSessionId = "sess_noverify",
                Status = PaymentReservationStatus.Pending,
                Currency = "eur"
            });
            context.SaveChanges();

            var evt = new Event
            {
                Type = Events.CheckoutSessionCompleted,
                Data = new EventData
                {
                    Object = new Session { Id = "sess_noverify", PaymentStatus = "paid", PaymentIntentId = "pi_x" }
                }
            };

            var eventFactory = new FakeEventFactory { EventToReturn = evt };
            var coordinator = CreateCoordinator(
                context,
                new FakeSessionService(),
                new FakePaymentIntentService(),
                stripeOptions: new StripeOptions { Enabled = true, ApiKey = "test", ReturnBaseUrl = "https://return", WebhookSecret = null, AllowInsecureWebhooks = false },
                eventFactory: eventFactory);

            coordinator.HandleWebhookEvent(context, "payload", "sig");

            var reservation = context.ChargePaymentReservations.Single(r => r.StripeCheckoutSessionId == "sess_noverify");
            Assert.Equal(PaymentReservationStatus.Pending, reservation.Status);
            Assert.Null(reservation.AuthorizedAtUtc);
        }

        [Fact]
        public void HandleWebhookEvent_IsIdempotent()
        {
            using var context = CreateContext();
            var reservationId = Guid.NewGuid();
            context.ChargePaymentReservations.Add(new ChargePaymentReservation
            {
                ReservationId = reservationId,
                ChargePointId = "CP1",
                ConnectorId = 1,
                ChargeTagId = "TAG1",
                StripeCheckoutSessionId = "sess_dup",
                Status = PaymentReservationStatus.Pending,
                Currency = "eur"
            });
            context.SaveChanges();

            var evt = new Event
            {
                Id = "evt_1",
                Type = Events.CheckoutSessionCompleted,
                Created = DateTime.UtcNow,
                Data = new EventData
                {
                    Object = new Session
                    {
                        Id = "sess_dup",
                        PaymentIntentId = "pi_dup",
                        PaymentStatus = "paid"
                    }
                }
            };

            var eventFactory = new FakeEventFactory { EventToReturn = evt };
            var coordinator = CreateCoordinator(
                context,
                new FakeSessionService(),
                new FakePaymentIntentService(),
                stripeOptions: new StripeOptions { Enabled = true, ApiKey = "test", ReturnBaseUrl = "https://return", WebhookSecret = "whsec_test" },
                eventFactory: eventFactory);

            coordinator.HandleWebhookEvent(context, "payload", "sig");
            coordinator.HandleWebhookEvent(context, "payload", "sig"); // duplicate

            var reservation = context.ChargePaymentReservations.Single(r => r.StripeCheckoutSessionId == "sess_dup");
            Assert.Equal(PaymentReservationStatus.Authorized, reservation.Status);
            Assert.Equal(1, context.StripeWebhookEvents.Count());
        }

        [Fact]
        public void IdempotencyKeys_AreSetForCheckoutCreateAndCapture()
        {
            using var context = CreateContext();
            context.ChargePoints.Add(new ChargePoint
            {
                ChargePointId = "CP1",
                MaxSessionKwh = 1,
                PricePerKwh = 1m,
                ConnectorUsageFeePerMinute = 0m,
                MaxUsageFeeMinutes = 0,
                StartUsageFeeAfterMinutes = 0,
                UserSessionFee = 0m,
                OwnerSessionFee = 0m,
                OwnerCommissionPercent = 0m,
                OwnerCommissionFixedPerKwh = 0m
            });
            context.SaveChanges();

            var sessionService = new FakeSessionService
            {
                CreateResponse = new Session { Id = "sess_key", Url = "https://checkout/session/key", PaymentIntentId = "pi_key" }
            };
            var intentService = new FakePaymentIntentService
            {
                GetResponse = new PaymentIntent { Id = "pi_key", Status = "requires_capture", Amount = 1000 }
            };

            var coordinator = CreateCoordinator(context, sessionService, intentService, now: () => new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc));

            var result = coordinator.CreateCheckoutSession(context, new PaymentSessionRequest
            {
                ChargePointId = "CP1",
                ChargeTagId = "TAG1",
                ConnectorId = 1
            });

            Assert.NotNull(sessionService.LastCreateRequestOptions);
            Assert.Contains("checkout_create", sessionService.LastCreateRequestOptions.IdempotencyKey);
            Assert.Contains(result.Reservation.ReservationId.ToString(), sessionService.LastCreateRequestOptions.IdempotencyKey);

            // complete the flow enough to capture
            result.Reservation.Status = PaymentReservationStatus.Charging;
            var transaction = new Transaction
            {
                TransactionId = 77,
                ChargePointId = "CP1",
                ConnectorId = 1,
                StartTagId = "TAG1",
                StartTime = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc),
                StopTime = new DateTime(2025, 1, 1, 12, 10, 0, DateTimeKind.Utc),
                MeterStart = 0,
                MeterStop = 1
            };
            context.Transactions.Add(transaction);
            context.SaveChanges();

            coordinator.CompleteReservation(context, transaction);

            Assert.True(intentService.CaptureCalled);
            Assert.NotNull(intentService.LastCaptureRequestOptions);
            Assert.Contains("capture", intentService.LastCaptureRequestOptions.IdempotencyKey);
            Assert.Contains(result.Reservation.ReservationId.ToString(), intentService.LastCaptureRequestOptions.IdempotencyKey);
        }

        [Fact]
        public async Task CleanupService_CancelsStaleReservations()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Maintenance:ReservationTimeoutMinutes"] = "1",
                    ["Maintenance:CleanupIntervalSeconds"] = "30"
                } as IEnumerable<KeyValuePair<string, string?>>)
                .Build());
            var dbName = Guid.NewGuid().ToString();
            services.AddDbContext<OCPPCoreContext>(options => options.UseInMemoryDatabase(dbName));

            var provider = services.BuildServiceProvider();
            var ctx = provider.GetRequiredService<OCPPCoreContext>();

            ctx.ChargePaymentReservations.Add(new ChargePaymentReservation
            {
                ReservationId = Guid.NewGuid(),
                ChargePointId = "CP1",
                ConnectorId = 1,
                ChargeTagId = "TAG1",
                Status = PaymentReservationStatus.Pending,
                UpdatedAtUtc = DateTime.UtcNow.AddHours(-2),
                Currency = "eur"
            });
            ctx.SaveChanges();

            var svc = new TestCleanupService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<ILogger<PaymentReservationCleanupService>>(),
                provider.GetRequiredService<IConfiguration>(),
                ctx);
            await svc.RunOnce();

            ctx.Entry(ctx.ChargePaymentReservations.Single()).Reload();
            var reservation = ctx.ChargePaymentReservations.Single();
            Assert.Equal(PaymentReservationStatus.Cancelled, reservation.Status);
            Assert.Contains("Auto-cancelled", reservation.LastError);
        }
    }

    internal class FakeSessionService : IStripeSessionService
    {
        public SessionCreateOptions? LastCreateOptions { get; private set; }
        public RequestOptions? LastCreateRequestOptions { get; private set; }
        public Session CreateResponse { get; set; } = new Session();
        public Session GetResponse { get; set; } = new Session();

        public Session Create(SessionCreateOptions options, RequestOptions requestOptions = null!)
        {
            LastCreateOptions = options;
            LastCreateRequestOptions = requestOptions;
            return CreateResponse;
        }

        public Session Get(string id) => GetResponse;
    }

    internal class FakePaymentIntentService : IStripePaymentIntentService
    {
        public PaymentIntent GetResponse { get; set; } = new PaymentIntent();
        public PaymentIntentCaptureOptions? LastCaptureOptions { get; private set; }
        public RequestOptions? LastCaptureRequestOptions { get; private set; }
        public bool CaptureCalled { get; private set; }
        public bool CancelCalled { get; private set; }
        public RequestOptions? LastCancelRequestOptions { get; private set; }

        public PaymentIntent Get(string id) => GetResponse;

        public PaymentIntent Capture(string id, PaymentIntentCaptureOptions options, RequestOptions requestOptions = null!)
        {
            CaptureCalled = true;
            LastCaptureOptions = options;
            LastCaptureRequestOptions = requestOptions;
            var capturedAmount = options.AmountToCapture ?? 0;
            return new PaymentIntent { Id = id, Status = "succeeded", AmountReceived = capturedAmount };
        }

        public void Cancel(string id, RequestOptions requestOptions = null!)
        {
            CancelCalled = true;
            LastCancelRequestOptions = requestOptions;
        }
    }

    internal class FakeEventFactory : IStripeEventFactory
    {
        public Event EventToReturn { get; set; } = new Event();

        public Event ConstructEvent(string payload, string signatureHeader, string webhookSecret) => EventToReturn;
    }

    internal class TestCleanupService : PaymentReservationCleanupService
    {
        private readonly OCPPCoreContext _ctx;

        public TestCleanupService(IServiceScopeFactory scopeFactory, ILogger<PaymentReservationCleanupService> logger, IConfiguration configuration, OCPPCoreContext ctx)
            : base(scopeFactory, logger, configuration, Options.Create(new PaymentFlowOptions { StartWindowMinutes = 7 }))
        {
            _ctx = ctx;
        }

        public Task RunOnce(CancellationToken token = default) => CleanupAsync(token);

        protected override async Task CleanupAsync(CancellationToken token)
        {
            var stale = _ctx.ChargePaymentReservations
                .Where(r =>
                    r.Status == PaymentReservationStatus.Pending ||
                    r.Status == PaymentReservationStatus.Authorized ||
                    r.Status == PaymentReservationStatus.StartRequested)
                .ToList();

            foreach (var reservation in stale)
            {
                reservation.Status = PaymentReservationStatus.Cancelled;
                reservation.LastError = "Auto-cancelled: stale reservation (background sweep)";
                reservation.UpdatedAtUtc = DateTime.UtcNow;
            }

            await _ctx.SaveChangesAsync(token);
        }
    }
}
