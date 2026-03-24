using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OCPP.Core.Database;
using OCPP.Core.Server.Payments;
using OCPP.Core.Server.Payments.Invoices;
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
            FakeEventFactory? eventFactory = null,
            IEmailNotificationService? emailService = null,
            IInvoiceIntegrationService? invoiceIntegrationService = null)
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
                now ?? (() => DateTime.UtcNow),
                emailService,
                invoiceIntegrationService: invoiceIntegrationService);
        }

        private static TimeZoneInfo ResolveZagrebTimeZone()
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById("Europe/Zagreb");
            }
            catch
            {
                return TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time");
            }
        }

        private static DateTime LocalToUtc(TimeZoneInfo timeZone, DateTime localTime)
        {
            return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified), timeZone);
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
        public void IdleFeeCalculator_CalculatesAccumulatedTotalsAcrossSuspendedEvResumeCycles()
        {
            var now = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            var reservation = new ChargePaymentReservation
            {
                UsageFeeAnchorMinutes = 1,
                UsageFeePerMinute = 0.25m,
                StartUsageFeeAfterMinutes = 0,
                MaxUsageFeeMinutes = 120
            };
            var transaction = new Transaction
            {
                IdleUsageFeeMinutes = 3,
                ChargingEndedAtUtc = now.AddMinutes(-4)
            };

            var snapshot = IdleFeeCalculator.CalculateSnapshot(
                transaction,
                reservation,
                new PaymentFlowOptions(),
                now,
                NullLogger.Instance);

            Assert.Equal(3, snapshot.AccumulatedMinutes);
            Assert.Equal(4, snapshot.CurrentIntervalMinutes);
            Assert.Equal(7, snapshot.TotalMinutes);
            Assert.Equal(1.75m, snapshot.TotalAmount);
            Assert.Equal(now.AddMinutes(-4), snapshot.SuspendedSinceUtc);
        }

        [Fact]
        public void IdleFeeCalculator_ShiftsGraceStartPastExcludedWindowAcrossMidnight()
        {
            TimeZoneInfo zagreb = ResolveZagrebTimeZone();
            DateTime suspendedSinceUtc = LocalToUtc(zagreb, new DateTime(2025, 1, 1, 21, 50, 0));
            DateTime expectedIdleFeeStartUtc = LocalToUtc(zagreb, new DateTime(2025, 1, 2, 6, 0, 0));

            var reservation = new ChargePaymentReservation
            {
                UsageFeeAnchorMinutes = 1,
                UsageFeePerMinute = 0.20m,
                StartUsageFeeAfterMinutes = 20,
                MaxUsageFeeMinutes = 120
            };
            var flowOptions = new PaymentFlowOptions
            {
                IdleFeeExcludedWindow = "22:00-06:00",
                IdleFeeExcludedTimeZoneId = "Europe/Zagreb"
            };

            DateTime? actualIdleFeeStartUtc = IdleFeeCalculator.CalculateIdleFeeStartAtUtc(
                suspendedSinceUtc,
                reservation,
                flowOptions,
                NullLogger.Instance);

            Assert.Equal(expectedIdleFeeStartUtc, actualIdleFeeStartUtc);
        }

        [Fact]
        public void IdleFeeCalculator_ExcludesCrossMidnightWindowFromBillableMinutes()
        {
            TimeZoneInfo zagreb = ResolveZagrebTimeZone();
            DateTime intervalStartUtc = LocalToUtc(zagreb, new DateTime(2025, 1, 1, 21, 50, 0));
            DateTime intervalEndUtc = LocalToUtc(zagreb, new DateTime(2025, 1, 2, 6, 10, 0));

            var reservation = new ChargePaymentReservation
            {
                UsageFeeAnchorMinutes = 1,
                UsageFeePerMinute = 0.20m,
                StartUsageFeeAfterMinutes = 0,
                MaxUsageFeeMinutes = 120
            };
            var flowOptions = new PaymentFlowOptions
            {
                IdleFeeExcludedWindow = "22:00-06:00",
                IdleFeeExcludedTimeZoneId = "Europe/Zagreb"
            };

            int minutes = IdleFeeCalculator.CalculateIntervalBillableMinutes(
                intervalStartUtc,
                intervalEndUtc,
                reservation,
                flowOptions,
                NullLogger.Instance);

            Assert.Equal(20, minutes);
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
            Assert.Equal("auto", sessionService.LastCreateOptions.BillingAddressCollection);
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
        public void ConfirmReservation_SendsPaymentAuthorizedEmail_WhenEmailServiceProvided()
        {
            using var context = CreateContext();
            var reservationId = Guid.NewGuid();
            context.ChargePaymentReservations.Add(new ChargePaymentReservation
            {
                ReservationId = reservationId,
                ChargePointId = "CP1",
                StripeCheckoutSessionId = "sess_email",
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
                    Id = "sess_email",
                    PaymentIntentId = "pi_email",
                    Status = "complete",
                    PaymentStatus = "paid",
                    CustomerDetails = new SessionCustomerDetails
                    {
                        Email = "driver@example.com"
                    }
                }
            };

            var intentService = new FakePaymentIntentService
            {
                GetResponse = new PaymentIntent
                {
                    Id = "pi_email",
                    Status = "requires_capture",
                    Amount = 123
                }
            };

            var emailService = new FakeEmailNotificationService();
            var coordinator = CreateCoordinator(context, sessionService, intentService, emailService: emailService);

            var result = coordinator.ConfirmReservation(context, reservationId, "sess_email");

            Assert.True(result.Success);
            Assert.Equal(1, emailService.PaymentAuthorizedCount);
            Assert.Equal("driver@example.com", emailService.LastToEmail);
            Assert.Contains($"/Payments/Status?reservationId={reservationId}", emailService.LastStatusUrl);
        }

        [Fact]
        public void ConfirmReservation_FailsWhenPaymentIntentStatusUnexpected()
        {
            using var context = CreateContext();
            var reservationId = Guid.NewGuid();
            context.ChargePaymentReservations.Add(new ChargePaymentReservation
            {
                ReservationId = reservationId,
                ChargePointId = "CP1",
                StripeCheckoutSessionId = "sess_unexpected",
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
                    Id = "sess_unexpected",
                    PaymentIntentId = "pi_unexpected",
                    Status = "complete",
                    PaymentStatus = "paid"
                }
            };

            var intentService = new FakePaymentIntentService
            {
                GetResponse = new PaymentIntent
                {
                    Id = "pi_unexpected",
                    Status = "processing",
                    Amount = 100
                }
            };

            var coordinator = CreateCoordinator(context, sessionService, intentService);
            var result = coordinator.ConfirmReservation(context, reservationId, "sess_unexpected");

            Assert.False(result.Success);
            Assert.Equal("processing", result.Status);
            Assert.Equal(PaymentReservationStatus.Failed, result.Reservation.Status);
            Assert.Contains("Unexpected PaymentIntent status", result.Reservation.LastError);
        }

        [Fact]
        public void CancelReservation_CancelsAuthorizedReservation()
        {
            using var context = CreateContext();
            var reservationId = Guid.NewGuid();
            context.ChargePaymentReservations.Add(new ChargePaymentReservation
            {
                ReservationId = reservationId,
                ChargePointId = "CP-CANCEL",
                ConnectorId = 1,
                ChargeTagId = "TAG-CANCEL",
                StripePaymentIntentId = "pi_cancel_me",
                Status = PaymentReservationStatus.Authorized,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
                Currency = "eur"
            });
            context.SaveChanges();

            var intentService = new FakePaymentIntentService
            {
                GetResponse = new PaymentIntent
                {
                    Id = "pi_cancel_me",
                    Status = "requires_capture",
                    Amount = 1000
                }
            };
            var coordinator = CreateCoordinator(context, new FakeSessionService(), intentService);

            coordinator.CancelReservation(context, reservationId, "user_cancelled");

            var reservation = context.ChargePaymentReservations.Single(r => r.ReservationId == reservationId);
            Assert.Equal(PaymentReservationStatus.Cancelled, reservation.Status);
            Assert.Equal("user_cancelled", reservation.LastError);
            Assert.True(intentService.CancelCalled);
        }

        [Fact]
        public void CancelReservation_IgnoresCompletedReservation()
        {
            using var context = CreateContext();
            var reservationId = Guid.NewGuid();
            context.ChargePaymentReservations.Add(new ChargePaymentReservation
            {
                ReservationId = reservationId,
                ChargePointId = "CP-COMPLETE",
                ConnectorId = 1,
                ChargeTagId = "TAG-COMPLETE",
                StripePaymentIntentId = "pi_complete",
                Status = PaymentReservationStatus.Completed,
                CapturedAmountCents = 50,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
                Currency = "eur"
            });
            context.SaveChanges();

            var intentService = new FakePaymentIntentService
            {
                GetResponse = new PaymentIntent
                {
                    Id = "pi_complete",
                    Status = "succeeded",
                    AmountReceived = 50
                }
            };
            var coordinator = CreateCoordinator(context, new FakeSessionService(), intentService);

            coordinator.CancelReservation(context, reservationId, "late_cancel");

            var reservation = context.ChargePaymentReservations.Single(r => r.ReservationId == reservationId);
            Assert.Equal(PaymentReservationStatus.Completed, reservation.Status);
            Assert.Equal(50, reservation.CapturedAmountCents);
            Assert.False(intentService.CancelCalled);
        }

        [Fact]
        public void ResumeReservation_ReturnsRedirect_ForOpenPendingCheckout()
        {
            using var context = CreateContext();
            var reservationId = Guid.NewGuid();
            context.ChargePaymentReservations.Add(new ChargePaymentReservation
            {
                ReservationId = reservationId,
                ChargePointId = "CP-RESUME",
                ConnectorId = 1,
                ChargeTagId = "TAG-RESUME",
                StripeCheckoutSessionId = "sess_resume_open",
                Status = PaymentReservationStatus.Pending,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
                Currency = "eur"
            });
            context.SaveChanges();

            var sessionService = new FakeSessionService
            {
                GetResponse = new Session
                {
                    Id = "sess_resume_open",
                    Status = "open",
                    Url = "https://checkout.stripe.test/resume"
                }
            };

            var coordinator = CreateCoordinator(context, sessionService, new FakePaymentIntentService());
            var result = coordinator.ResumeReservation(context, reservationId);

            Assert.True(result.Success);
            Assert.Equal("Redirect", result.Status);
            Assert.Equal("https://checkout.stripe.test/resume", result.CheckoutUrl);
            Assert.Equal(PaymentReservationStatus.Pending, result.Reservation.Status);
        }

        [Fact]
        public void ResumeReservation_ReturnsStatus_ForAuthorizedReservation()
        {
            using var context = CreateContext();
            var reservationId = Guid.NewGuid();
            context.ChargePaymentReservations.Add(new ChargePaymentReservation
            {
                ReservationId = reservationId,
                ChargePointId = "CP-RESUME",
                ConnectorId = 1,
                ChargeTagId = "TAG-AUTH",
                StripeCheckoutSessionId = "sess_resume_auth",
                Status = PaymentReservationStatus.Authorized,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
                Currency = "eur"
            });
            context.SaveChanges();

            var coordinator = CreateCoordinator(context, new FakeSessionService(), new FakePaymentIntentService());
            var result = coordinator.ResumeReservation(context, reservationId);

            Assert.True(result.Success);
            Assert.Equal("Status", result.Status);
            Assert.Null(result.CheckoutUrl);
            Assert.Equal(PaymentReservationStatus.Authorized, result.Reservation.Status);
        }

        [Fact]
        public void ResumeReservation_ReturnsMissingCheckoutSession_WhenPendingReservationHasNoSessionId()
        {
            using var context = CreateContext();
            var reservationId = Guid.NewGuid();
            context.ChargePaymentReservations.Add(new ChargePaymentReservation
            {
                ReservationId = reservationId,
                ChargePointId = "CP-RESUME",
                ConnectorId = 1,
                ChargeTagId = "TAG-MISSING",
                Status = PaymentReservationStatus.Pending,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
                Currency = "eur"
            });
            context.SaveChanges();

            var coordinator = CreateCoordinator(context, new FakeSessionService(), new FakePaymentIntentService());
            var result = coordinator.ResumeReservation(context, reservationId);

            Assert.False(result.Success);
            Assert.Equal("MissingCheckoutSession", result.Status);
            Assert.Null(result.CheckoutUrl);
            Assert.Equal(PaymentReservationStatus.Pending, result.Reservation.Status);
        }

        [Fact]
        public void ConfirmReservation_GeneratesOcppTag_AndCreatesChargeTag()
        {
            using var context = CreateContext();
            var reservationId = Guid.NewGuid();
            context.ChargePaymentReservations.Add(new ChargePaymentReservation
            {
                ReservationId = reservationId,
                ChargePointId = "CP1",
                StripeCheckoutSessionId = "sess_tag",
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
                    Id = "sess_tag",
                    PaymentIntentId = "pi_tag",
                    Status = "complete",
                    PaymentStatus = "paid"
                }
            };

            var intentService = new FakePaymentIntentService
            {
                GetResponse = new PaymentIntent
                {
                    Id = "pi_tag",
                    Status = "requires_capture",
                    Amount = 250
                }
            };

            var coordinator = CreateCoordinator(context, sessionService, intentService);
            var result = coordinator.ConfirmReservation(context, reservationId, "sess_tag");

            Assert.True(result.Success);
            Assert.False(string.IsNullOrWhiteSpace(result.Reservation.OcppIdTag));
            Assert.StartsWith("R", result.Reservation.OcppIdTag);
            Assert.True(result.Reservation.OcppIdTag.Length <= 20);
            Assert.True(context.ChargeTags.Any(t => t.TagId == result.Reservation.OcppIdTag));
        }

        [Fact]
        public void ConfirmReservation_DoesNotReauthorizeTerminalReservation()
        {
            using var context = CreateContext();
            var reservationId = Guid.NewGuid();
            context.ChargePaymentReservations.Add(new ChargePaymentReservation
            {
                ReservationId = reservationId,
                ChargePointId = "CP1",
                StripeCheckoutSessionId = "sess_cancelled",
                Status = PaymentReservationStatus.Cancelled,
                MaxAmountCents = 1000,
                ChargeTagId = "TAG1",
                LastError = "cancelled manually",
                Currency = "eur"
            });
            context.SaveChanges();

            var sessionService = new FakeSessionService
            {
                GetResponse = new Session
                {
                    Id = "sess_cancelled",
                    PaymentIntentId = "pi_cancelled",
                    Status = "complete",
                    PaymentStatus = "paid"
                }
            };

            var intentService = new FakePaymentIntentService
            {
                GetResponse = new PaymentIntent
                {
                    Id = "pi_cancelled",
                    Status = "requires_capture",
                    Amount = 250
                }
            };

            var emailService = new FakeEmailNotificationService();
            var coordinator = CreateCoordinator(context, sessionService, intentService, emailService: emailService);
            var result = coordinator.ConfirmReservation(context, reservationId, "sess_cancelled");

            Assert.False(result.Success);
            Assert.Equal(PaymentReservationStatus.Cancelled, result.Status);
            Assert.Equal(PaymentReservationStatus.Cancelled, result.Reservation.Status);
            Assert.Equal("cancelled manually", result.Reservation.LastError);
            Assert.Equal(0, emailService.PaymentAuthorizedCount);
        }

        [Fact]
        public void ConfirmReservation_DoesNotDowngradeStartRequestedReservation()
        {
            using var context = CreateContext();
            var reservationId = Guid.NewGuid();
            context.ChargePaymentReservations.Add(new ChargePaymentReservation
            {
                ReservationId = reservationId,
                ChargePointId = "CP1",
                StripeCheckoutSessionId = "sess_started",
                Status = PaymentReservationStatus.StartRequested,
                MaxAmountCents = 1000,
                ChargeTagId = "TAG1",
                Currency = "eur",
                AuthorizedAtUtc = DateTime.UtcNow.AddMinutes(-3),
                StartDeadlineAtUtc = DateTime.UtcNow.AddMinutes(4)
            });
            context.SaveChanges();

            var sessionService = new FakeSessionService
            {
                GetResponse = new Session
                {
                    Id = "sess_started",
                    PaymentIntentId = "pi_started",
                    Status = "complete",
                    PaymentStatus = "paid"
                }
            };

            var intentService = new FakePaymentIntentService
            {
                GetResponse = new PaymentIntent
                {
                    Id = "pi_started",
                    Status = "requires_capture",
                    Amount = 250
                }
            };

            var emailService = new FakeEmailNotificationService();
            var coordinator = CreateCoordinator(context, sessionService, intentService, emailService: emailService);
            var result = coordinator.ConfirmReservation(context, reservationId, "sess_started");

            Assert.True(result.Success);
            Assert.Equal(PaymentReservationStatus.StartRequested, result.Status);
            Assert.Equal(PaymentReservationStatus.StartRequested, result.Reservation.Status);
            Assert.Equal("pi_started", result.Reservation.StripePaymentIntentId);
            Assert.Equal(0, emailService.PaymentAuthorizedCount);
        }

        [Fact]
        public void MarkTransactionStarted_SendsR1RequestedEmail_WhenR1CheckoutMetadataPresent()
        {
            using var context = CreateContext();
            context.ChargePaymentReservations.Add(new ChargePaymentReservation
            {
                ReservationId = Guid.NewGuid(),
                ChargePointId = "CP1",
                ConnectorId = 1,
                ChargeTagId = "TAG1",
                StripeCheckoutSessionId = "sess_r1_start",
                Status = PaymentReservationStatus.Authorized,
                Currency = "eur",
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });
            context.SaveChanges();

            var sessionService = new FakeSessionService
            {
                GetResponse = new Session
                {
                    Id = "sess_r1_start",
                    CustomerDetails = new SessionCustomerDetails
                    {
                        Email = "r1@example.com"
                    },
                    Metadata = new Dictionary<string, string>
                    {
                        ["invoice_type"] = "R1",
                        ["buyer_company"] = "Acme d.o.o.",
                        ["buyer_oib"] = "12345678901"
                    }
                }
            };

            var emailService = new FakeEmailNotificationService();
            var coordinator = CreateCoordinator(context, sessionService, new FakePaymentIntentService(), emailService: emailService);

            coordinator.MarkTransactionStarted(context, "CP1", 1, "TAG1", 555);

            var reservation = context.ChargePaymentReservations.Single();
            Assert.Equal(PaymentReservationStatus.Charging, reservation.Status);
            Assert.Equal(1, emailService.R1InvoiceRequestedCount);
            Assert.Equal("r1@example.com", emailService.LastToEmail);
            Assert.Equal("Acme d.o.o.", emailService.LastBuyerCompanyName);
            Assert.Equal("12345678901", emailService.LastBuyerOib);
        }

        [Fact]
        public void RequestR1Invoice_UpdatesStripeMetadataAndSendsNotification()
        {
            using var context = CreateContext();
            var reservationId = Guid.NewGuid();
            context.ChargePoints.Add(new ChargePoint
            {
                ChargePointId = "CP1",
                Name = "Station A"
            });
            context.ChargePaymentReservations.Add(new ChargePaymentReservation
            {
                ReservationId = reservationId,
                ChargePointId = "CP1",
                ConnectorId = 1,
                ChargeTagId = "TAG1",
                StripeCheckoutSessionId = "sess_r1_update",
                StripePaymentIntentId = "pi_r1_update",
                Status = PaymentReservationStatus.Authorized,
                Currency = "eur",
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });
            context.SaveChanges();

            var sessionService = new FakeSessionService
            {
                GetResponse = new Session
                {
                    Id = "sess_r1_update",
                    PaymentIntentId = "pi_r1_update",
                    CustomerDetails = new SessionCustomerDetails
                    {
                        Email = "billing@example.com"
                    },
                    Metadata = new Dictionary<string, string>()
                }
            };

            var intentService = new FakePaymentIntentService
            {
                GetResponse = new PaymentIntent
                {
                    Id = "pi_r1_update",
                    Metadata = new Dictionary<string, string>()
                }
            };

            var emailService = new FakeEmailNotificationService();
            var coordinator = CreateCoordinator(context, sessionService, intentService, emailService: emailService);

            var result = coordinator.RequestR1Invoice(context, new PaymentR1InvoiceRequest
            {
                ReservationId = reservationId,
                BuyerCompanyName = "Acme d.o.o.",
                BuyerOib = "12345678903"
            });

            Assert.True(result.Success);
            Assert.Equal("Updated", result.Status);
            Assert.Equal("12345678903", result.BuyerOib);
            Assert.NotNull(sessionService.LastUpdateOptions);
            Assert.Equal("R1", sessionService.LastUpdateOptions?.Metadata?["invoice_type"]);
            Assert.Equal("Acme d.o.o.", sessionService.LastUpdateOptions?.Metadata?["buyer_company"]);
            Assert.Equal("12345678903", sessionService.LastUpdateOptions?.Metadata?["buyer_oib"]);
            Assert.NotNull(intentService.LastUpdateOptions);
            Assert.Equal("R1", intentService.LastUpdateOptions?.Metadata?["invoice_type"]);
            Assert.Equal("12345678903", intentService.LastUpdateOptions?.Metadata?["buyer_oib"]);
            Assert.Equal(1, emailService.R1InvoiceRequestedCount);
            Assert.Equal("billing@example.com", emailService.LastToEmail);
        }

        [Fact]
        public void RequestR1Invoice_RejectsInvalidOib()
        {
            using var context = CreateContext();
            var reservationId = Guid.NewGuid();
            context.ChargePaymentReservations.Add(new ChargePaymentReservation
            {
                ReservationId = reservationId,
                ChargePointId = "CP1",
                ConnectorId = 1,
                ChargeTagId = "TAG1",
                StripeCheckoutSessionId = "sess_invalid_oib",
                StripePaymentIntentId = "pi_invalid_oib",
                Status = PaymentReservationStatus.Authorized,
                Currency = "eur",
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });
            context.SaveChanges();

            var sessionService = new FakeSessionService();
            var intentService = new FakePaymentIntentService();
            var coordinator = CreateCoordinator(context, sessionService, intentService);

            var result = coordinator.RequestR1Invoice(context, new PaymentR1InvoiceRequest
            {
                ReservationId = reservationId,
                BuyerCompanyName = "Acme",
                BuyerOib = "12345678901"
            });

            Assert.False(result.Success);
            Assert.Equal("InvalidOib", result.Status);
            Assert.Null(sessionService.LastUpdateOptions);
            Assert.Null(intentService.LastUpdateOptions);
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
        public void CompleteReservation_DefersCaptureUntilConnectorBecomesAvailable()
        {
            using var context = CreateContext();
            var now = new DateTime(2026, 3, 12, 12, 20, 0, DateTimeKind.Utc);
            var reservation = new ChargePaymentReservation
            {
                ReservationId = Guid.NewGuid(),
                ChargePointId = "CP-DEFER",
                ConnectorId = 1,
                ChargeTagId = "TAG-DEFER",
                StripePaymentIntentId = "pi_defer",
                Status = PaymentReservationStatus.Charging,
                PricePerKwh = 0.50m,
                UsageFeePerMinute = 0.20m,
                StartUsageFeeAfterMinutes = 0,
                MaxUsageFeeMinutes = 30,
                UsageFeeAnchorMinutes = 1,
                Currency = "eur",
                StartTransactionAtUtc = now.AddMinutes(-15)
            };
            var transaction = new Transaction
            {
                TransactionId = 314,
                ChargePointId = "CP-DEFER",
                ConnectorId = 1,
                StartTagId = "TAG-DEFER",
                StartTime = now.AddMinutes(-15),
                StopTime = now.AddMinutes(-2),
                MeterStart = 1.0,
                MeterStop = 2.0
            };

            context.ChargePaymentReservations.Add(reservation);
            context.Transactions.Add(transaction);
            context.ConnectorStatuses.Add(new ConnectorStatus
            {
                ChargePointId = "CP-DEFER",
                ConnectorId = 1,
                LastStatus = "Occupied",
                LastStatusTime = now.AddMinutes(-1)
            });
            context.SaveChanges();

            var intentService = new FakePaymentIntentService
            {
                GetResponse = new PaymentIntent { Id = "pi_defer", Status = "requires_capture", Amount = 10_000 }
            };
            var coordinator = CreateCoordinator(
                context,
                new FakeSessionService(),
                intentService,
                now: () => now);

            coordinator.CompleteReservation(context, transaction);

            Assert.Equal(PaymentReservationStatus.WaitingForDisconnect, reservation.Status);
            Assert.Equal(transaction.StopTime, reservation.StopTransactionAtUtc);
            Assert.Null(reservation.DisconnectedAtUtc);
            Assert.Equal(transaction.StopTime, transaction.ChargingEndedAtUtc);
            Assert.False(intentService.CaptureCalled);
            Assert.False(intentService.CancelCalled);
        }

        [Fact]
        public void CompleteReservation_CompletesImmediately_WhenStopReasonSignalsDisconnect()
        {
            using var context = CreateContext();
            var stopAtUtc = new DateTime(2026, 3, 12, 12, 20, 0, DateTimeKind.Utc);
            var reservation = new ChargePaymentReservation
            {
                ReservationId = Guid.NewGuid(),
                ChargePointId = "CP-EV-DISCONNECT",
                ConnectorId = 1,
                ChargeTagId = "TAG-EV-DISCONNECT",
                StripePaymentIntentId = "pi_ev_disconnect",
                Status = PaymentReservationStatus.Charging,
                PricePerKwh = 0.50m,
                UserSessionFee = 0.50m,
                UsageFeePerMinute = 0.20m,
                StartUsageFeeAfterMinutes = 0,
                MaxUsageFeeMinutes = 30,
                UsageFeeAnchorMinutes = 1,
                Currency = "eur",
                StartTransactionAtUtc = stopAtUtc.AddMinutes(-20)
            };
            var transaction = new Transaction
            {
                TransactionId = 317,
                ChargePointId = "CP-EV-DISCONNECT",
                ConnectorId = 1,
                StartTagId = "TAG-EV-DISCONNECT",
                StartTime = stopAtUtc.AddMinutes(-20),
                StopTime = stopAtUtc,
                StopReason = "EVDisconnected",
                MeterStart = 0,
                MeterStop = 2
            };

            context.ChargePaymentReservations.Add(reservation);
            context.Transactions.Add(transaction);
            context.SaveChanges();

            var intentService = new FakePaymentIntentService
            {
                GetResponse = new PaymentIntent { Id = "pi_ev_disconnect", Status = "requires_capture", Amount = 10_000 }
            };
            var coordinator = CreateCoordinator(
                context,
                new FakeSessionService(),
                intentService,
                now: () => stopAtUtc);

            coordinator.CompleteReservation(context, transaction);

            Assert.Equal(PaymentReservationStatus.Completed, reservation.Status);
            Assert.Equal(stopAtUtc, reservation.DisconnectedAtUtc);
            Assert.True(intentService.CaptureCalled);
        }

        [Fact]
        public void HandleConnectorAvailable_CompletesWaitingReservationAndCaptures()
        {
            using var context = CreateContext();
            var disconnectedAtUtc = new DateTime(2026, 3, 12, 12, 25, 0, DateTimeKind.Utc);
            var reservation = new ChargePaymentReservation
            {
                ReservationId = Guid.NewGuid(),
                ChargePointId = "CP-DONE",
                ConnectorId = 1,
                ChargeTagId = "TAG-DONE",
                StripePaymentIntentId = "pi_done_wait",
                Status = PaymentReservationStatus.WaitingForDisconnect,
                PricePerKwh = 0.50m,
                UsageFeePerMinute = 0.20m,
                StartUsageFeeAfterMinutes = 0,
                MaxUsageFeeMinutes = 30,
                UsageFeeAnchorMinutes = 1,
                Currency = "eur",
                TransactionId = 315,
                StartTransactionAtUtc = disconnectedAtUtc.AddMinutes(-20),
                StopTransactionAtUtc = disconnectedAtUtc.AddMinutes(-3)
            };
            var transaction = new Transaction
            {
                TransactionId = 315,
                ChargePointId = "CP-DONE",
                ConnectorId = 1,
                StartTagId = "TAG-DONE",
                StartTime = disconnectedAtUtc.AddMinutes(-20),
                StopTime = disconnectedAtUtc.AddMinutes(-3),
                MeterStart = 0,
                MeterStop = 2
            };

            context.ChargePaymentReservations.Add(reservation);
            context.Transactions.Add(transaction);
            context.SaveChanges();

            var intentService = new FakePaymentIntentService
            {
                GetResponse = new PaymentIntent { Id = "pi_done_wait", Status = "requires_capture", Amount = 10_000 }
            };
            var coordinator = CreateCoordinator(
                context,
                new FakeSessionService(),
                intentService,
                now: () => disconnectedAtUtc);

            coordinator.HandleConnectorAvailable(context, "CP-DONE", 1, disconnectedAtUtc);

            Assert.Equal(PaymentReservationStatus.Completed, reservation.Status);
            Assert.Equal(disconnectedAtUtc, reservation.DisconnectedAtUtc);
            Assert.True(intentService.CaptureCalled);
            Assert.True(reservation.CapturedAmountCents > 0);
        }

        [Fact]
        public void CompleteReservation_KeepsPreStopSuspendedEvAnchorUntilDisconnect()
        {
            using var context = CreateContext();
            var suspendedAtUtc = new DateTime(2026, 3, 12, 12, 5, 0, DateTimeKind.Utc);
            var stopAtUtc = suspendedAtUtc.AddMinutes(2);
            var disconnectedAtUtc = suspendedAtUtc.AddMinutes(7);
            var reservation = new ChargePaymentReservation
            {
                ReservationId = Guid.NewGuid(),
                ChargePointId = "CP-IDLE-CONTINUITY",
                ConnectorId = 1,
                ChargeTagId = "TAG-IDLE-CONTINUITY",
                StripePaymentIntentId = "pi_idle_continuity",
                Status = PaymentReservationStatus.WaitingForDisconnect,
                UsageFeePerMinute = 0.20m,
                StartUsageFeeAfterMinutes = 0,
                MaxUsageFeeMinutes = 60,
                UsageFeeAnchorMinutes = 1,
                Currency = "eur",
                StartTransactionAtUtc = suspendedAtUtc.AddMinutes(-20),
                StopTransactionAtUtc = stopAtUtc,
                DisconnectedAtUtc = disconnectedAtUtc
            };
            var transaction = new Transaction
            {
                TransactionId = 318,
                ChargePointId = "CP-IDLE-CONTINUITY",
                ConnectorId = 1,
                StartTagId = "TAG-IDLE-CONTINUITY",
                StartTime = suspendedAtUtc.AddMinutes(-20),
                ChargingEndedAtUtc = suspendedAtUtc,
                StopTime = stopAtUtc,
                MeterStart = 20,
                MeterStop = 20
            };

            context.ChargePaymentReservations.Add(reservation);
            context.Transactions.Add(transaction);
            context.SaveChanges();

            var intentService = new FakePaymentIntentService
            {
                GetResponse = new PaymentIntent { Id = "pi_idle_continuity", Status = "requires_capture", Amount = 10_000 }
            };
            var coordinator = CreateCoordinator(
                context,
                new FakeSessionService(),
                intentService,
                now: () => disconnectedAtUtc);

            coordinator.CompleteReservation(context, transaction);

            Assert.Equal(PaymentReservationStatus.Completed, reservation.Status);
            Assert.Equal(7, transaction.IdleUsageFeeMinutes);
            Assert.Equal(1.4m, transaction.IdleUsageFeeAmount);
            Assert.Null(transaction.ChargingEndedAtUtc);
        }

        [Fact]
        public void CompleteReservation_UsesDisconnectedAtUtcForIdleBillingAfterStop()
        {
            using var context = CreateContext();
            var stopAtUtc = new DateTime(2026, 3, 12, 12, 10, 0, DateTimeKind.Utc);
            var disconnectedAtUtc = stopAtUtc.AddMinutes(5);
            var reservation = new ChargePaymentReservation
            {
                ReservationId = Guid.NewGuid(),
                ChargePointId = "CP-IDLE",
                ConnectorId = 1,
                ChargeTagId = "TAG-IDLE",
                StripePaymentIntentId = "pi_idle",
                Status = PaymentReservationStatus.WaitingForDisconnect,
                UsageFeePerMinute = 0.20m,
                StartUsageFeeAfterMinutes = 0,
                MaxUsageFeeMinutes = 60,
                UsageFeeAnchorMinutes = 1,
                Currency = "eur",
                DisconnectedAtUtc = disconnectedAtUtc
            };
            var transaction = new Transaction
            {
                TransactionId = 316,
                ChargePointId = "CP-IDLE",
                ConnectorId = 1,
                StartTagId = "TAG-IDLE",
                StartTime = stopAtUtc.AddMinutes(-30),
                StopTime = stopAtUtc,
                MeterStart = 10,
                MeterStop = 10
            };

            context.ChargePaymentReservations.Add(reservation);
            context.Transactions.Add(transaction);
            context.SaveChanges();

            var intentService = new FakePaymentIntentService
            {
                GetResponse = new PaymentIntent { Id = "pi_idle", Status = "requires_capture", Amount = 10_000 }
            };
            var coordinator = CreateCoordinator(
                context,
                new FakeSessionService(),
                intentService,
                now: () => disconnectedAtUtc);

            coordinator.CompleteReservation(context, transaction);

            Assert.Equal(PaymentReservationStatus.Completed, reservation.Status);
            Assert.Equal(5, transaction.IdleUsageFeeMinutes);
            Assert.Equal(1.0m, transaction.IdleUsageFeeAmount);
            Assert.Equal(100, reservation.CapturedAmountCents);
        }

        [Fact]
        public void CompleteReservation_SendsCompletionReceiptAndR1ReadyEmails_ForR1Flow()
        {
            using var context = CreateContext();
            var reservation = new ChargePaymentReservation
            {
                ReservationId = Guid.NewGuid(),
                ChargePointId = "CP1",
                ConnectorId = 1,
                ChargeTagId = "TAG1",
                StripeCheckoutSessionId = "sess_r1_complete",
                StripePaymentIntentId = "pi_r1_complete",
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
                TransactionId = 420,
                ChargePointId = "CP1",
                ConnectorId = 1,
                StartTagId = "TAG1",
                StartTime = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc),
                StopTime = new DateTime(2025, 1, 1, 12, 10, 0, DateTimeKind.Utc),
                MeterStart = 0,
                MeterStop = 2
            };
            context.Transactions.Add(transaction);
            context.SaveChanges();

            var intentService = new FakePaymentIntentService
            {
                GetResponse = new PaymentIntent { Id = "pi_r1_complete", Status = "requires_capture", Amount = 10_000 }
            };
            var sessionService = new FakeSessionService
            {
                GetResponse = new Session
                {
                    Id = "sess_r1_complete",
                    CustomerDetails = new SessionCustomerDetails
                    {
                        Email = "complete-r1@example.com"
                    },
                    Metadata = new Dictionary<string, string>
                    {
                        ["invoice_type"] = "R1"
                    }
                }
            };

            var emailService = new FakeEmailNotificationService();
            var coordinator = CreateCoordinator(
                context,
                sessionService,
                intentService,
                now: () => new DateTime(2025, 1, 1, 12, 20, 0, DateTimeKind.Utc),
                emailService: emailService);

            coordinator.CompleteReservation(context, transaction);

            Assert.Equal(PaymentReservationStatus.Completed, reservation.Status);
            Assert.Equal(1, emailService.ChargingCompletedCount);
            Assert.Equal(1, emailService.SessionReceiptCount);
            Assert.Equal(1, emailService.R1InvoiceReadyCount);
            Assert.Equal("complete-r1@example.com", emailService.LastToEmail);
        }

        [Fact]
        public void CompleteReservation_DoesNotSendR1ReadyEmail_ForNonR1Flow()
        {
            using var context = CreateContext();
            var reservation = new ChargePaymentReservation
            {
                ReservationId = Guid.NewGuid(),
                ChargePointId = "CP1",
                ConnectorId = 1,
                ChargeTagId = "TAG1",
                StripeCheckoutSessionId = "sess_r2_complete",
                StripePaymentIntentId = "pi_r2_complete",
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
                TransactionId = 421,
                ChargePointId = "CP1",
                ConnectorId = 1,
                StartTagId = "TAG1",
                StartTime = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc),
                StopTime = new DateTime(2025, 1, 1, 12, 10, 0, DateTimeKind.Utc),
                MeterStart = 0,
                MeterStop = 2
            };
            context.Transactions.Add(transaction);
            context.SaveChanges();

            var intentService = new FakePaymentIntentService
            {
                GetResponse = new PaymentIntent { Id = "pi_r2_complete", Status = "requires_capture", Amount = 10_000 }
            };
            var sessionService = new FakeSessionService
            {
                GetResponse = new Session
                {
                    Id = "sess_r2_complete",
                    CustomerDetails = new SessionCustomerDetails
                    {
                        Email = "complete-r2@example.com"
                    },
                    Metadata = new Dictionary<string, string>
                    {
                        ["invoice_type"] = "R2"
                    }
                }
            };

            var emailService = new FakeEmailNotificationService();
            var coordinator = CreateCoordinator(
                context,
                sessionService,
                intentService,
                now: () => new DateTime(2025, 1, 1, 12, 20, 0, DateTimeKind.Utc),
                emailService: emailService);

            coordinator.CompleteReservation(context, transaction);

            Assert.Equal(PaymentReservationStatus.Completed, reservation.Status);
            Assert.Equal(1, emailService.ChargingCompletedCount);
            Assert.Equal(1, emailService.SessionReceiptCount);
            Assert.Equal(0, emailService.R1InvoiceReadyCount);
        }

        [Fact]
        public void CompleteReservation_CallsInvoiceIntegration_AfterBreakdownIsPersisted()
        {
            using var context = CreateContext();
            context.ChargePaymentReservations.Add(new ChargePaymentReservation
            {
                ReservationId = Guid.NewGuid(),
                ChargePointId = "CP-INV",
                ConnectorId = 1,
                ChargeTagId = "TAG-INV",
                TransactionId = 200,
                StripeCheckoutSessionId = "sess_invoice",
                StripePaymentIntentId = "pi_invoice",
                PricePerKwh = 0.30m,
                UserSessionFee = 0.50m,
                UsageFeePerMinute = 0.20m,
                Status = PaymentReservationStatus.Charging,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
                Currency = "eur"
            });
            context.Transactions.Add(new Transaction
            {
                TransactionId = 200,
                ChargePointId = "CP-INV",
                ConnectorId = 1,
                StartTagId = "TAG-INV",
                StartTime = new DateTime(2026, 3, 5, 10, 0, 0, DateTimeKind.Utc),
                StopTime = new DateTime(2026, 3, 5, 10, 30, 0, DateTimeKind.Utc),
                MeterStart = 100,
                MeterStop = 108
            });
            context.SaveChanges();

            var sessionService = new FakeSessionService
            {
                GetResponse = new Session
                {
                    Id = "sess_invoice",
                    CustomerDetails = new SessionCustomerDetails { Email = "billing@example.com" }
                }
            };
            var intentService = new FakePaymentIntentService
            {
                GetResponse = new PaymentIntent { Id = "pi_invoice", Status = "requires_capture", Amount = 10_000 }
            };
            var invoiceIntegration = new FakeInvoiceIntegrationService();
            var coordinator = CreateCoordinator(
                context,
                sessionService,
                intentService,
                invoiceIntegrationService: invoiceIntegration);

            var transaction = context.Transactions.Single(t => t.TransactionId == 200);
            coordinator.CompleteReservation(context, transaction);

            Assert.Equal(1, invoiceIntegration.HandleCompletedReservationCount);
            Assert.NotNull(invoiceIntegration.LastReservation);
            Assert.NotNull(invoiceIntegration.LastTransaction);
            Assert.Equal(8d, invoiceIntegration.LastTransaction!.EnergyKwh);
            Assert.Equal(2.40m, invoiceIntegration.LastTransaction.EnergyCost);
            Assert.Equal(PaymentReservationStatus.Completed, invoiceIntegration.LastReservation!.Status);
        }

        [Fact]
        public void CompleteReservation_KeepsPaymentCompleted_WhenInvoiceIntegrationThrows()
        {
            using var context = CreateContext();
            var reservationId = Guid.NewGuid();
            context.ChargePaymentReservations.Add(new ChargePaymentReservation
            {
                ReservationId = reservationId,
                ChargePointId = "CP-INV-ERR",
                ConnectorId = 1,
                ChargeTagId = "TAG-INV-ERR",
                TransactionId = 201,
                StripeCheckoutSessionId = "sess_invoice_err",
                StripePaymentIntentId = "pi_invoice_err",
                PricePerKwh = 0.25m,
                Status = PaymentReservationStatus.Charging,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
                Currency = "eur"
            });
            context.Transactions.Add(new Transaction
            {
                TransactionId = 201,
                ChargePointId = "CP-INV-ERR",
                ConnectorId = 1,
                StartTagId = "TAG-INV-ERR",
                StartTime = new DateTime(2026, 3, 5, 11, 0, 0, DateTimeKind.Utc),
                StopTime = new DateTime(2026, 3, 5, 11, 20, 0, DateTimeKind.Utc),
                MeterStart = 50,
                MeterStop = 54
            });
            context.SaveChanges();

            var sessionService = new FakeSessionService
            {
                GetResponse = new Session { Id = "sess_invoice_err" }
            };
            var intentService = new FakePaymentIntentService
            {
                GetResponse = new PaymentIntent { Id = "pi_invoice_err", Status = "requires_capture", Amount = 5_000 }
            };
            var invoiceIntegration = new FakeInvoiceIntegrationService { ThrowOnHandle = true };
            var coordinator = CreateCoordinator(
                context,
                sessionService,
                intentService,
                invoiceIntegrationService: invoiceIntegration);

            var transaction = context.Transactions.Single(t => t.TransactionId == 201);
            coordinator.CompleteReservation(context, transaction);

            var reservation = context.ChargePaymentReservations.Single(r => r.ReservationId == reservationId);
            Assert.Equal(PaymentReservationStatus.Completed, reservation.Status);
            Assert.True(intentService.CaptureCalled);
            Assert.Equal(1, invoiceIntegration.HandleCompletedReservationCount);
        }

        [Fact]
        public void CompleteReservation_PassesPersistedInvoiceMetadata_ToCompletionEmails()
        {
            using var context = CreateContext();
            var reservationId = Guid.NewGuid();
            context.ChargePaymentReservations.Add(new ChargePaymentReservation
            {
                ReservationId = reservationId,
                ChargePointId = "CP-INV-MAIL",
                ConnectorId = 1,
                ChargeTagId = "TAG-INV-MAIL",
                TransactionId = 202,
                StripeCheckoutSessionId = "sess_invoice_mail",
                StripePaymentIntentId = "pi_invoice_mail",
                PricePerKwh = 0.40m,
                UserSessionFee = 0.50m,
                Status = PaymentReservationStatus.Charging,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
                Currency = "eur"
            });
            context.Transactions.Add(new Transaction
            {
                TransactionId = 202,
                ChargePointId = "CP-INV-MAIL",
                ConnectorId = 1,
                StartTagId = "TAG-INV-MAIL",
                StartTime = new DateTime(2026, 3, 5, 12, 0, 0, DateTimeKind.Utc),
                StopTime = new DateTime(2026, 3, 5, 12, 10, 0, DateTimeKind.Utc),
                MeterStart = 10,
                MeterStop = 12
            });
            context.SaveChanges();

            var sessionService = new FakeSessionService
            {
                GetResponse = new Session
                {
                    Id = "sess_invoice_mail",
                    CustomerDetails = new SessionCustomerDetails
                    {
                        Email = "invoice-mail@example.com"
                    },
                    Metadata = new Dictionary<string, string>
                    {
                        ["invoice_type"] = "R1"
                    }
                }
            };
            var intentService = new FakePaymentIntentService
            {
                GetResponse = new PaymentIntent { Id = "pi_invoice_mail", Status = "requires_capture", Amount = 5_000 }
            };
            var emailService = new FakeEmailNotificationService();
            var invoiceIntegration = new FakeInvoiceIntegrationService
            {
                OnHandle = (dbContext, reservation, transaction, session) =>
                {
                    dbContext.InvoiceSubmissionLogs.Add(new InvoiceSubmissionLog
                    {
                        ReservationId = reservation.ReservationId,
                        TransactionId = transaction.TransactionId,
                        Provider = "ERacuni",
                        Mode = "Submit",
                        Status = "Submitted",
                        ExternalInvoiceNumber = "R1-2026-0007",
                        ExternalPdfUrl = "https://example.test/invoices/r1-2026-0007.pdf",
                        CreatedAtUtc = DateTime.UtcNow,
                        CompletedAtUtc = DateTime.UtcNow
                    });
                    dbContext.SaveChanges();
                }
            };
            var coordinator = CreateCoordinator(
                context,
                sessionService,
                intentService,
                emailService: emailService,
                invoiceIntegrationService: invoiceIntegration);

            var transaction = context.Transactions.Single(t => t.TransactionId == 202);
            coordinator.CompleteReservation(context, transaction);

            Assert.Equal(1, emailService.SessionReceiptCount);
            Assert.Equal("R1-2026-0007", emailService.LastSessionReceiptInvoiceNumber);
            Assert.Equal("https://example.test/invoices/r1-2026-0007.pdf", emailService.LastSessionReceiptInvoiceUrl);
            Assert.Equal(1, emailService.R1InvoiceReadyCount);
            Assert.Equal("R1-2026-0007", emailService.LastR1InvoiceNumber);
            Assert.Equal("https://example.test/invoices/r1-2026-0007.pdf", emailService.LastR1InvoiceUrl);
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
        public void CompleteReservation_UsesAlreadySucceededPaymentIntent()
        {
            using var context = CreateContext();
            var reservation = new ChargePaymentReservation
            {
                ReservationId = Guid.NewGuid(),
                ChargePointId = "CP1",
                ConnectorId = 1,
                ChargeTagId = "TAG1",
                StripePaymentIntentId = "pi_done",
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
                TransactionId = 199,
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
                GetResponse = new PaymentIntent
                {
                    Id = "pi_done",
                    Status = "succeeded",
                    AmountReceived = 456
                }
            };

            var coordinator = CreateCoordinator(context, new FakeSessionService(), intentService);
            coordinator.CompleteReservation(context, transaction);

            Assert.Equal(PaymentReservationStatus.Completed, reservation.Status);
            Assert.Equal(456, reservation.CapturedAmountCents);
            Assert.True(reservation.CapturedAtUtc.HasValue);
            Assert.False(intentService.CaptureCalled);
            Assert.False(intentService.CancelCalled);
        }

        [Fact]
        public void HandleWebhookEvent_CheckoutCompletedMarksAuthorized()
        {
            using var context = CreateContext();
            var reservationId = Guid.NewGuid();
            context.ChargePaymentReservations.Add(new ChargePaymentReservation
            {
                ReservationId = reservationId,
                ChargePointId = "CP1",
                ConnectorId = 1,
                ChargeTagId = "TAG1",
                StripeCheckoutSessionId = "sess_webhook",
                Status = PaymentReservationStatus.Pending,
                LastError = "old failure text",
                FailureCode = "OldFailure",
                FailureMessage = "Old failure message",
                Currency = "eur"
            });
            context.SaveChanges();

            var evt = new Event
            {
                Id = "evt_webhook_completed",
                Type = EventTypes.CheckoutSessionCompleted,
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
            Assert.Null(reservation.LastError);
            Assert.Null(reservation.FailureCode);
            Assert.Null(reservation.FailureMessage);

            var processedWebhook = context.StripeWebhookEvents.Single(e => e.EventId == "evt_webhook_completed");
            Assert.Equal(reservationId, processedWebhook.ReservationId);
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
                Type = EventTypes.PaymentIntentPaymentFailed,
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
                Type = EventTypes.CheckoutSessionExpired,
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
            Assert.Equal("CheckoutExpired", reservation.FailureCode);
            Assert.Equal("Checkout session expired", reservation.FailureMessage);
        }

        [Fact]
        public void HandleWebhookEvent_CheckoutExpired_DoesNotChangeCompletedReservation()
        {
            using var context = CreateContext();
            context.ChargePaymentReservations.Add(new ChargePaymentReservation
            {
                ReservationId = Guid.NewGuid(),
                ChargePointId = "CP1",
                ConnectorId = 1,
                ChargeTagId = "TAG1",
                StripeCheckoutSessionId = "sess_done",
                Status = PaymentReservationStatus.Completed,
                Currency = "eur"
            });
            context.SaveChanges();

            var evt = new Event
            {
                Type = EventTypes.CheckoutSessionExpired,
                Data = new EventData
                {
                    Object = new Session
                    {
                        Id = "sess_done"
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

            var reservation = context.ChargePaymentReservations.Single(r => r.StripeCheckoutSessionId == "sess_done");
            Assert.Equal(PaymentReservationStatus.Completed, reservation.Status);
            Assert.Null(reservation.LastError);
        }

        [Fact]
        public void CancelPaymentIntentIfCancelable_CancelsWhenRequiresCapture()
        {
            using var context = CreateContext();
            var reservation = new ChargePaymentReservation
            {
                ReservationId = Guid.NewGuid(),
                ChargePointId = "CP1",
                ConnectorId = 1,
                ChargeTagId = "TAG1",
                StripePaymentIntentId = "pi_cancelable",
                Status = PaymentReservationStatus.Authorized,
                Currency = "eur",
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };
            context.ChargePaymentReservations.Add(reservation);
            context.SaveChanges();

            var intentService = new FakePaymentIntentService
            {
                GetResponse = new PaymentIntent { Id = "pi_cancelable", Status = "requires_capture" }
            };
            var coordinator = CreateCoordinator(context, new FakeSessionService(), intentService);

            coordinator.CancelPaymentIntentIfCancelable(context, reservation, "test");

            Assert.True(intentService.CancelCalled);
            Assert.NotNull(intentService.LastCancelRequestOptions);
            Assert.Contains(reservation.ReservationId.ToString(), intentService.LastCancelRequestOptions.IdempotencyKey);
        }

        [Fact]
        public void CancelPaymentIntentIfCancelable_SkipsWhenAlreadySucceeded()
        {
            using var context = CreateContext();
            var reservation = new ChargePaymentReservation
            {
                ReservationId = Guid.NewGuid(),
                ChargePointId = "CP1",
                ConnectorId = 1,
                ChargeTagId = "TAG1",
                StripePaymentIntentId = "pi_done",
                Status = PaymentReservationStatus.Authorized,
                Currency = "eur",
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };
            context.ChargePaymentReservations.Add(reservation);
            context.SaveChanges();

            var intentService = new FakePaymentIntentService
            {
                GetResponse = new PaymentIntent { Id = "pi_done", Status = "succeeded" }
            };
            var coordinator = CreateCoordinator(context, new FakeSessionService(), intentService);

            coordinator.CancelPaymentIntentIfCancelable(context, reservation, "test");

            Assert.False(intentService.CancelCalled);
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
                Type = EventTypes.CheckoutSessionCompleted,
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
                Type = EventTypes.CheckoutSessionCompleted,
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
        public async Task IdleWarningService_SendsWarningOnlyOnce_WhenWithinWarningWindow()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Maintenance:IdleWarningSweepSeconds"] = "30"
                } as IEnumerable<KeyValuePair<string, string?>>)
                .Build());

            var dbName = Guid.NewGuid().ToString();
            services.AddDbContext<OCPPCoreContext>(options => options.UseInMemoryDatabase(dbName));

            var fakeEmailService = new FakeEmailNotificationService();
            services.AddSingleton<IEmailNotificationService>(fakeEmailService);

            var provider = services.BuildServiceProvider();
            var ctx = provider.GetRequiredService<OCPPCoreContext>();
            var now = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);

            ctx.ChargePaymentReservations.Add(new ChargePaymentReservation
            {
                ReservationId = Guid.NewGuid(),
                ChargePointId = "CP1",
                ConnectorId = 1,
                ChargeTagId = "TAG1",
                StripeCheckoutSessionId = "sess_idle",
                Status = PaymentReservationStatus.Charging,
                UsageFeeAnchorMinutes = 1,
                UsageFeePerMinute = 0.20m,
                StartUsageFeeAfterMinutes = 20,
                MaxUsageFeeMinutes = 120,
                Currency = "eur",
                TransactionId = 991,
                CreatedAtUtc = now.AddMinutes(-30),
                UpdatedAtUtc = now.AddMinutes(-5)
            });

            ctx.Transactions.Add(new Transaction
            {
                TransactionId = 991,
                ChargePointId = "CP1",
                ConnectorId = 1,
                StartTagId = "TAG1",
                StartTime = now.AddHours(-1),
                ChargingEndedAtUtc = now.AddMinutes(-10),
                StopTime = null
            });
            ctx.SaveChanges();

            var sessionService = new FakeSessionService
            {
                GetResponse = new Session
                {
                    Id = "sess_idle",
                    CustomerDetails = new SessionCustomerDetails
                    {
                        Email = "idle@example.com"
                    }
                }
            };

            var svc = new TestIdleWarningEmailService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<ILogger<IdleFeeWarningEmailService>>(),
                provider.GetRequiredService<IConfiguration>(),
                Options.Create(new NotificationOptions
                {
                    EnableCustomerEmails = true,
                    IdleWarningLeadMinutes = 15
                }),
                Options.Create(new StripeOptions
                {
                    Enabled = true,
                    ApiKey = "test",
                    ReturnBaseUrl = "https://return"
                }),
                () => now);
            svc.SessionToReturn = sessionService.GetResponse;

            await svc.RunOnce();
            await svc.RunOnce();

            Assert.Equal(1, fakeEmailService.IdleFeeWarningCount);
            Assert.Equal("idle@example.com", fakeEmailService.LastToEmail);
        }

        [Fact]
        public async Task IdleWarningService_UsesSharedCalculatorForIdleFeeStartTiming()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Maintenance:IdleWarningSweepSeconds"] = "30",
                    ["Payments:IdleFeeExcludedWindow"] = "22:00-06:00",
                    ["Payments:IdleFeeExcludedTimeZoneId"] = "Europe/Zagreb"
                } as IEnumerable<KeyValuePair<string, string?>>)
                .Build());

            var dbName = Guid.NewGuid().ToString();
            services.AddDbContext<OCPPCoreContext>(options => options.UseInMemoryDatabase(dbName));

            var fakeEmailService = new FakeEmailNotificationService();
            services.AddSingleton<IEmailNotificationService>(fakeEmailService);

            var provider = services.BuildServiceProvider();
            var ctx = provider.GetRequiredService<OCPPCoreContext>();
            TimeZoneInfo zagreb = ResolveZagrebTimeZone();
            DateTime now = LocalToUtc(zagreb, new DateTime(2025, 1, 2, 5, 50, 0));
            DateTime chargingEndedAtUtc = LocalToUtc(zagreb, new DateTime(2025, 1, 1, 21, 50, 0));

            var reservation = new ChargePaymentReservation
            {
                ReservationId = Guid.NewGuid(),
                ChargePointId = "CP1",
                ConnectorId = 1,
                ChargeTagId = "TAG1",
                StripeCheckoutSessionId = "sess_idle_timing",
                Status = PaymentReservationStatus.Charging,
                UsageFeeAnchorMinutes = 1,
                UsageFeePerMinute = 0.20m,
                StartUsageFeeAfterMinutes = 20,
                MaxUsageFeeMinutes = 120,
                Currency = "eur",
                TransactionId = 992,
                CreatedAtUtc = now.AddHours(-10),
                UpdatedAtUtc = now.AddMinutes(-5)
            };
            ctx.ChargePaymentReservations.Add(reservation);

            ctx.Transactions.Add(new Transaction
            {
                TransactionId = 992,
                ChargePointId = "CP1",
                ConnectorId = 1,
                StartTagId = "TAG1",
                StartTime = now.AddHours(-12),
                ChargingEndedAtUtc = chargingEndedAtUtc,
                StopTime = null
            });
            ctx.SaveChanges();

            var expectedIdleFeeStartUtc = IdleFeeCalculator.CalculateIdleFeeStartAtUtc(
                chargingEndedAtUtc,
                reservation,
                new PaymentFlowOptions
                {
                    IdleFeeExcludedWindow = "22:00-06:00",
                    IdleFeeExcludedTimeZoneId = "Europe/Zagreb"
                },
                NullLogger.Instance);

            var svc = new TestIdleWarningEmailService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<ILogger<IdleFeeWarningEmailService>>(),
                provider.GetRequiredService<IConfiguration>(),
                Options.Create(new NotificationOptions
                {
                    EnableCustomerEmails = true,
                    IdleWarningLeadMinutes = 15
                }),
                Options.Create(new StripeOptions
                {
                    Enabled = true,
                    ApiKey = "test",
                    ReturnBaseUrl = "https://return"
                }),
                Options.Create(new PaymentFlowOptions
                {
                    IdleFeeExcludedWindow = "22:00-06:00",
                    IdleFeeExcludedTimeZoneId = "Europe/Zagreb"
                }),
                () => now);
            svc.SessionToReturn = new Session
            {
                Id = "sess_idle_timing",
                CustomerDetails = new SessionCustomerDetails
                {
                    Email = "idle-timing@example.com"
                }
            };

            await svc.RunOnce();

            Assert.Equal(1, fakeEmailService.IdleFeeWarningCount);
            Assert.Equal(expectedIdleFeeStartUtc, fakeEmailService.LastIdleFeeStartsAtUtc);
            Assert.Equal("idle-timing@example.com", fakeEmailService.LastToEmail);
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

    internal class FakeEmailNotificationService : IEmailNotificationService
    {
        public int PaymentAuthorizedCount { get; private set; }
        public int ChargingCompletedCount { get; private set; }
        public int IdleFeeWarningCount { get; private set; }
        public int SessionReceiptCount { get; private set; }
        public int R1InvoiceRequestedCount { get; private set; }
        public int R1InvoiceReadyCount { get; private set; }
        public string? LastToEmail { get; private set; }
        public string? LastStatusUrl { get; private set; }
        public string? LastBuyerCompanyName { get; private set; }
        public string? LastBuyerOib { get; private set; }
        public DateTime? LastIdleFeeStartsAtUtc { get; private set; }
        public TimeSpan? LastIdleFeeRemaining { get; private set; }
        public string? LastSessionReceiptInvoiceNumber { get; private set; }
        public string? LastSessionReceiptInvoiceUrl { get; private set; }
        public string? LastR1InvoiceNumber { get; private set; }
        public string? LastR1InvoiceUrl { get; private set; }

        public void SendPaymentAuthorized(string toEmail, ChargePaymentReservation reservation, Session session, string statusUrl)
        {
            PaymentAuthorizedCount++;
            LastToEmail = toEmail;
            LastStatusUrl = statusUrl;
        }

        public void SendChargingCompleted(string toEmail, ChargePaymentReservation reservation, Transaction transaction, ChargePoint chargePoint, string statusUrl)
        {
            ChargingCompletedCount++;
            LastToEmail = toEmail;
            LastStatusUrl = statusUrl;
        }

        public void SendIdleFeeWarning(string toEmail, ChargePaymentReservation reservation, Transaction transaction, ChargePoint chargePoint, DateTime idleFeeStartsAtUtc, TimeSpan remainingUntilIdleFee, string statusUrl)
        {
            IdleFeeWarningCount++;
            LastToEmail = toEmail;
            LastStatusUrl = statusUrl;
            LastIdleFeeStartsAtUtc = idleFeeStartsAtUtc;
            LastIdleFeeRemaining = remainingUntilIdleFee;
        }

        public void SendSessionReceipt(string toEmail, ChargePaymentReservation reservation, Transaction transaction, ChargePoint chargePoint, string statusUrl, string invoiceNumber, string invoiceUrl)
        {
            SessionReceiptCount++;
            LastToEmail = toEmail;
            LastStatusUrl = statusUrl;
            LastSessionReceiptInvoiceNumber = invoiceNumber;
            LastSessionReceiptInvoiceUrl = invoiceUrl;
        }

        public void SendR1InvoiceRequested(string toEmail, ChargePaymentReservation reservation, ChargePoint chargePoint, string statusUrl, string buyerCompanyName, string buyerOib)
        {
            R1InvoiceRequestedCount++;
            LastToEmail = toEmail;
            LastStatusUrl = statusUrl;
            LastBuyerCompanyName = buyerCompanyName;
            LastBuyerOib = buyerOib;
        }

        public void SendR1InvoiceReady(string toEmail, ChargePaymentReservation reservation, Transaction transaction, ChargePoint chargePoint, string statusUrl, string invoiceNumber, string invoiceUrl)
        {
            R1InvoiceReadyCount++;
            LastToEmail = toEmail;
            LastStatusUrl = statusUrl;
            LastR1InvoiceNumber = invoiceNumber;
            LastR1InvoiceUrl = invoiceUrl;
        }
    }

    internal class FakeSessionService : IStripeSessionService
    {
        public SessionCreateOptions? LastCreateOptions { get; private set; }
        public RequestOptions? LastCreateRequestOptions { get; private set; }
        public SessionUpdateOptions? LastUpdateOptions { get; private set; }
        public RequestOptions? LastUpdateRequestOptions { get; private set; }
        public string? LastUpdatedSessionId { get; private set; }
        public Session CreateResponse { get; set; } = new Session();
        public Session GetResponse { get; set; } = new Session();

        public Session Create(SessionCreateOptions options, RequestOptions requestOptions = null!)
        {
            LastCreateOptions = options;
            LastCreateRequestOptions = requestOptions;
            return CreateResponse;
        }

        public Session Get(string id) => GetResponse;

        public Session Update(string id, SessionUpdateOptions options, RequestOptions requestOptions = null!)
        {
            LastUpdatedSessionId = id;
            LastUpdateOptions = options;
            LastUpdateRequestOptions = requestOptions;
            GetResponse.Metadata = options?.Metadata ?? GetResponse.Metadata;
            return GetResponse;
        }
    }

    internal class FakePaymentIntentService : IStripePaymentIntentService
    {
        public PaymentIntent GetResponse { get; set; } = new PaymentIntent();
        public PaymentIntentUpdateOptions? LastUpdateOptions { get; private set; }
        public RequestOptions? LastUpdateRequestOptions { get; private set; }
        public string? LastUpdatedPaymentIntentId { get; private set; }
        public PaymentIntentCaptureOptions? LastCaptureOptions { get; private set; }
        public RequestOptions? LastCaptureRequestOptions { get; private set; }
        public bool CaptureCalled { get; private set; }
        public bool CancelCalled { get; private set; }
        public RequestOptions? LastCancelRequestOptions { get; private set; }

        public PaymentIntent Get(string id) => GetResponse;

        public PaymentIntent Update(string id, PaymentIntentUpdateOptions options, RequestOptions requestOptions = null!)
        {
            LastUpdatedPaymentIntentId = id;
            LastUpdateOptions = options;
            LastUpdateRequestOptions = requestOptions;
            GetResponse.Metadata = options?.Metadata ?? GetResponse.Metadata;
            return GetResponse;
        }

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

        public Event ConstructEvent(string payload, string signatureHeader, string webhookSecret, bool throwOnApiVersionMismatch = true) => EventToReturn;
    }

    internal class FakeInvoiceIntegrationService : IInvoiceIntegrationService
    {
        public int HandleCompletedReservationCount { get; private set; }
        public bool ThrowOnHandle { get; set; }
        public ChargePaymentReservation? LastReservation { get; private set; }
        public Transaction? LastTransaction { get; private set; }
        public Session? LastCheckoutSession { get; private set; }
        public Action<OCPPCoreContext, ChargePaymentReservation, Transaction, Session>? OnHandle { get; set; }

        public void HandleCompletedReservation(OCPPCoreContext dbContext, ChargePaymentReservation reservation, Transaction transaction, Session checkoutSession)
        {
            HandleCompletedReservationCount++;
            LastReservation = reservation;
            LastTransaction = transaction;
            LastCheckoutSession = checkoutSession;
            OnHandle?.Invoke(dbContext, reservation, transaction, checkoutSession);

            if (ThrowOnHandle)
            {
                throw new InvalidOperationException("Invoice integration failure");
            }
        }
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

    internal class TestIdleWarningEmailService : IdleFeeWarningEmailService
    {
        public Session? SessionToReturn { get; set; }

        public TestIdleWarningEmailService(
            IServiceScopeFactory scopeFactory,
            ILogger<IdleFeeWarningEmailService> logger,
            IConfiguration configuration,
            IOptions<NotificationOptions> notificationOptions,
            IOptions<StripeOptions> stripeOptions,
            Func<DateTime> utcNow)
            : base(scopeFactory, logger, configuration, notificationOptions, stripeOptions, utcNow)
        {
        }

        public TestIdleWarningEmailService(
            IServiceScopeFactory scopeFactory,
            ILogger<IdleFeeWarningEmailService> logger,
            IConfiguration configuration,
            IOptions<NotificationOptions> notificationOptions,
            IOptions<StripeOptions> stripeOptions,
            IOptions<PaymentFlowOptions> flowOptions,
            Func<DateTime> utcNow)
            : base(scopeFactory, logger, configuration, notificationOptions, stripeOptions, flowOptions, utcNow)
        {
        }

        public Task RunOnce(CancellationToken token = default) => SweepAsync(token);

        protected override Session GetCheckoutSession(string checkoutSessionId, Guid reservationId) => SessionToReturn ?? new Session();
    }
}
