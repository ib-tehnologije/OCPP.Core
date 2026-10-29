using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OCPP.Core.Database;
using OCPP.Core.Server.Payments;
using OCPP.Core.Server.Payments.Providers;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    /// <summary>
    /// Per-charge-point provider selection: ChargePoint.PaymentProvider ?? Payments:DefaultProvider picks the provider of
    /// a new checkout, and every later step uses the provider stored on the reservation, even after the charge point
    /// is switched to another provider.
    /// </summary>
    public partial class PaymentCoordinatorProviderTests
    {
        private const string OverrideChargePointId = "CP-WORLDLINE";
        private const string DefaultChargePointId = "CP-STRIPE";

        [Theory]
        [InlineData(null, "Stripe", "Stripe")]
        [InlineData("", "Stripe", "Stripe")]
        [InlineData("   ", "Saferpay", "Saferpay")]
        [InlineData(null, null, "Stripe")]
        [InlineData("Saferpay", "Stripe", "Saferpay")]
        [InlineData(" Stripe ", "Saferpay", "Stripe")]
        public void Resolver_UsesOverrideElseDefault(string? chargePointProvider, string? defaultProvider, string expected)
        {
            Assert.Equal(expected, PaymentProviderResolver.ResolveProviderName(chargePointProvider, defaultProvider));
        }

        [Fact]
        public void Resolver_NullOverrideResolvesDefaultGateway()
        {
            var (registry, stripe, _) = TwoProviderRegistry();

            Assert.Same(stripe, PaymentProviderResolver.ResolveCheckoutGateway(registry, null));
            Assert.Same(stripe, PaymentProviderResolver.ResolveCheckoutGateway(registry, ""));
        }

        [Fact]
        public void Resolver_OverrideResolvesOverrideGatewayCaseInsensitively()
        {
            var (registry, _, saferpay) = TwoProviderRegistry();

            Assert.Same(saferpay, PaymentProviderResolver.ResolveCheckoutGateway(registry, "Saferpay"));
            Assert.Same(saferpay, PaymentProviderResolver.ResolveCheckoutGateway(registry, " saferpay "));
        }

        [Fact]
        public void Resolver_UnknownOverrideThrowsAndNeverFallsBackToDefault()
        {
            var (registry, _, _) = TwoProviderRegistry();

            var error = Assert.Throws<PaymentProviderUnavailableException>(
                () => PaymentProviderResolver.ResolveCheckoutGateway(registry, "PayPal"));

            Assert.Equal(PaymentProviderUnavailableReason.Unknown, error.Reason);
            Assert.Equal("PayPal", error.Provider);
            Assert.Contains("Stripe, Saferpay", error.Message);
        }

        [Fact]
        public void Resolver_UnconfiguredOverrideThrows()
        {
            var (registry, _, saferpay) = TwoProviderRegistry();
            saferpay.IsConfigured = false;

            var error = Assert.Throws<PaymentProviderUnavailableException>(
                () => PaymentProviderResolver.ResolveCheckoutGateway(registry, "Saferpay"));

            Assert.Equal(PaymentProviderUnavailableReason.NotConfigured, error.Reason);
            Assert.Equal(PaymentProviderNames.Saferpay, error.Provider);
        }

        [Fact]
        public void CheckoutSettings_PrefersDefaultProviderAndAcceptsLegacyProvider()
        {
            Assert.Equal(
                PaymentProviderNames.Saferpay,
                PaymentCheckoutSettings.Resolve(new PaymentProviderOptions { DefaultProvider = "saferpay" }, null).DefaultProvider);
            Assert.Equal(
                PaymentProviderNames.Saferpay,
                PaymentCheckoutSettings.Resolve(new PaymentProviderOptions { Provider = "Saferpay" }, null).DefaultProvider);
            Assert.Equal(
                PaymentProviderNames.Stripe,
                PaymentCheckoutSettings.Resolve(new PaymentProviderOptions { DefaultProvider = "Stripe", Provider = "stripe" }, null).DefaultProvider);
            Assert.Equal(
                PaymentProviderNames.Stripe,
                PaymentCheckoutSettings.Resolve(new PaymentProviderOptions(), null).DefaultProvider);
        }

        [Fact]
        public void CheckoutSettings_ConflictingDefaultAndLegacyProviderThrows()
        {
            var error = Assert.Throws<InvalidOperationException>(() => PaymentCheckoutSettings.Resolve(
                new PaymentProviderOptions { DefaultProvider = "Stripe", Provider = "Saferpay" },
                null));

            Assert.Contains("Payments:DefaultProvider", error.Message);
            Assert.Contains("Payments:Provider", error.Message);
        }

        [Fact]
        public void CheckoutSettings_UnknownDefaultProviderThrows()
        {
            var error = Assert.Throws<InvalidOperationException>(() => PaymentCheckoutSettings.Resolve(
                new PaymentProviderOptions { DefaultProvider = "Worldline" },
                null));

            Assert.Contains("Payments:DefaultProvider", error.Message);
        }

        [Fact]
        public void CreateCheckout_UsesChargePointOverrideAndStoresItOnTheReservation()
        {
            using var context = CreateContext();
            AddPricedChargePoint(context, OverrideChargePointId, PaymentProviderNames.Saferpay);
            AddPricedChargePoint(context, DefaultChargePointId, null);
            var (coordinator, stripe, saferpay) = TwoProviderCoordinator();

            var overridden = coordinator.CreateCheckoutSession(context, CheckoutRequest(OverrideChargePointId));
            var defaulted = coordinator.CreateCheckoutSession(context, CheckoutRequest(DefaultChargePointId, connectorId: 2));

            Assert.Equal(PaymentProviderNames.Saferpay, overridden.Reservation.PaymentProvider);
            Assert.Equal(PaymentProviderNames.Stripe, defaulted.Reservation.PaymentProvider);
            Assert.Equal(OverrideChargePointId, Assert.Single(saferpay.CheckoutRequests).ChargePointId);
            Assert.Equal(DefaultChargePointId, Assert.Single(stripe.CheckoutRequests).ChargePointId);
        }

        [Fact]
        public void CreateCheckout_UnconfiguredOverrideFailsClosedWithoutCallingAnyProvider()
        {
            using var context = CreateContext();
            AddPricedChargePoint(context, OverrideChargePointId, PaymentProviderNames.Saferpay);
            var (coordinator, stripe, saferpay) = TwoProviderCoordinator();
            saferpay.IsConfigured = false;

            var error = Assert.Throws<PaymentProviderUnavailableException>(
                () => coordinator.CreateCheckoutSession(context, CheckoutRequest(OverrideChargePointId)));

            Assert.Equal(PaymentProviderUnavailableReason.NotConfigured, error.Reason);
            Assert.Empty(stripe.Calls);
            Assert.Empty(saferpay.Calls);
            Assert.Empty(context.ChargePaymentReservations);
        }

        [Fact]
        public void CreateCheckout_OverrideWorksWhileTheDefaultProviderIsNotConfigured()
        {
            using var context = CreateContext();
            AddPricedChargePoint(context, OverrideChargePointId, PaymentProviderNames.Saferpay);
            AddPricedChargePoint(context, DefaultChargePointId, null);
            var (coordinator, stripe, saferpay) = TwoProviderCoordinator();
            stripe.IsConfigured = false;

            Assert.False(coordinator.IsEnabled);
            Assert.True(coordinator.IsOperational);
            var created = coordinator.CreateCheckoutSession(context, CheckoutRequest(OverrideChargePointId));
            Assert.Equal(PaymentProviderNames.Saferpay, created.Reservation.PaymentProvider);
            Assert.Throws<PaymentProviderUnavailableException>(
                () => coordinator.CreateCheckoutSession(context, CheckoutRequest(DefaultChargePointId, connectorId: 2)));
            Assert.Empty(stripe.Calls);
        }

        [Fact]
        public void WorldlineSession_IsCapturedViaWorldlineAfterChargePointIsSwitchedBackToDefault()
        {
            using var context = CreateContext();
            var chargePoint = AddPricedChargePoint(context, OverrideChargePointId, PaymentProviderNames.Saferpay);
            var (coordinator, stripe, saferpay) = TwoProviderCoordinator();
            var reservation = StartSession(context, coordinator, saferpay, transactionId: 91);

            SwitchBackToDefault(context, chargePoint);
            var transaction = context.Transactions.Single(t => t.TransactionId == 91);
            transaction.MeterStop = 2.5;
            transaction.StopTime = Now.AddMinutes(30);
            transaction.StopReason = "EVDisconnected";
            context.SaveChanges();
            saferpay.AuthorizationResponse = Authorization(reservation.ReservationId, "wl_tx_1", PaymentAuthorizationState.RequiresCapture, 450);

            coordinator.CompleteReservation(context, transaction);

            Assert.Equal(PaymentReservationStatus.Completed, reservation.Status);
            Assert.Equal(PaymentProviderNames.Saferpay, reservation.PaymentProvider);
            var capture = Assert.Single(saferpay.Captures);
            Assert.Equal("wl_tx_1", capture.Reference);
            Assert.Equal(150, capture.AmountMinor);
            Assert.Empty(stripe.Calls);
        }

        [Fact]
        public void WorldlineSession_IsReleasedViaWorldlineAfterChargePointIsSwitchedBackToDefault()
        {
            using var context = CreateContext();
            var chargePoint = AddPricedChargePoint(context, OverrideChargePointId, PaymentProviderNames.Saferpay);
            var (coordinator, stripe, saferpay) = TwoProviderCoordinator();
            var reservation = StartSession(context, coordinator, saferpay, transactionId: 92);

            SwitchBackToDefault(context, chargePoint);
            var transaction = context.Transactions.Single(t => t.TransactionId == 92);
            transaction.MeterStop = 0;
            transaction.StopTime = Now.AddMinutes(5);
            transaction.StopReason = "EVDisconnected";
            context.SaveChanges();
            saferpay.AuthorizationResponse = Authorization(reservation.ReservationId, "wl_tx_1", PaymentAuthorizationState.RequiresCapture, 450);

            coordinator.CompleteReservation(context, transaction);

            Assert.Equal(PaymentReservationStatus.Cancelled, reservation.Status);
            Assert.Equal("wl_tx_1", Assert.Single(saferpay.Cancels).Reference);
            Assert.Empty(saferpay.Captures);
            Assert.Empty(stripe.Calls);
        }

        [Fact]
        public void WorldlineReservation_IsCancelledViaWorldlineAfterChargePointIsSwitchedBackToDefault()
        {
            using var context = CreateContext();
            var chargePoint = AddPricedChargePoint(context, OverrideChargePointId, PaymentProviderNames.Saferpay);
            var (coordinator, stripe, saferpay) = TwoProviderCoordinator();
            var created = coordinator.CreateCheckoutSession(context, CheckoutRequest(OverrideChargePointId));
            var reservationId = created.Reservation.ReservationId;
            saferpay.CheckoutStatus = CompletedCheckout(reservationId, "fake_token_1", "wl_tx_1", inlineAuthorization: true, amountMinor: 450);
            Assert.True(coordinator.ConfirmReservation(context, reservationId, null).Success);

            SwitchBackToDefault(context, chargePoint);
            saferpay.AuthorizationResponse = Authorization(reservationId, "wl_tx_1", PaymentAuthorizationState.RequiresCapture, 450);
            coordinator.CancelReservation(context, reservationId, "user_cancelled");

            Assert.Equal(PaymentReservationStatus.Cancelled, created.Reservation.Status);
            Assert.Equal("wl_tx_1", Assert.Single(saferpay.Cancels).Reference);
            Assert.Empty(stripe.Calls);
        }

        [Fact]
        public void Webhooks_OfBothProvidersAreHandledSideBySide()
        {
            using var context = CreateContext();
            var chargePoint = AddPricedChargePoint(context, OverrideChargePointId, PaymentProviderNames.Saferpay);
            var (coordinator, stripe, saferpay) = TwoProviderCoordinator();
            var created = coordinator.CreateCheckoutSession(context, CheckoutRequest(OverrideChargePointId));
            var reservationId = created.Reservation.ReservationId;
            SwitchBackToDefault(context, chargePoint);
            saferpay.CheckoutStatus = CompletedCheckout(reservationId, "fake_token_1", "wl_tx_1", inlineAuthorization: true, amountMinor: 450);
            saferpay.ParseResult = Notification(reservationId, "wl_evt_1");
            stripe.ParseResult = PaymentWebhookParseResult.Rejected("unsigned", statusCode: 400);

            var saferpayResult = coordinator.HandleWebhookAsync(
                context,
                new PaymentWebhookRequest { Provider = "saferpay", Method = "GET" },
                default).GetAwaiter().GetResult();
            var stripeResult = coordinator.HandleWebhookAsync(
                context,
                new PaymentWebhookRequest { Provider = null, Method = "POST" },
                default).GetAwaiter().GetResult();

            Assert.Equal(PaymentProviderNames.Saferpay, saferpayResult.Provider);
            Assert.Equal(PaymentReservationStatus.Authorized, created.Reservation.Status);
            Assert.Contains(reservationId, saferpayResult.AuthorizedReservationIds);
            Assert.Equal(PaymentProviderNames.Stripe, stripeResult.Provider);
            Assert.Equal(400, stripeResult.StatusCode);
            Assert.Equal(new[] { "ParseWebhook" }, stripe.Calls);
        }

        [Fact]
        public void StartupValidator_AcceptsConfiguredOverridesAndDefaults()
        {
            using var context = CreateContext();
            AddPricedChargePoint(context, OverrideChargePointId, "saferpay");
            AddPricedChargePoint(context, DefaultChargePointId, null);
            AddPricedChargePoint(context, "CP-EMPTY", "");
            var (registry, _, _) = TwoProviderRegistry();

            Assert.Empty(PaymentProviderStartupValidator.FindInvalidOverrides(context, registry));
            PaymentProviderStartupValidator.Validate(context, registry);
        }

        [Fact]
        public void StartupValidator_FailsFastForUnconfiguredOrUnknownOverrides()
        {
            using var context = CreateContext();
            AddPricedChargePoint(context, OverrideChargePointId, PaymentProviderNames.Saferpay);
            AddPricedChargePoint(context, "CP-TYPO", "Worldline");
            AddPricedChargePoint(context, DefaultChargePointId, null);
            var (registry, _, saferpay) = TwoProviderRegistry();
            saferpay.IsConfigured = false;

            var errors = PaymentProviderStartupValidator.FindInvalidOverrides(context, registry);
            var error = Assert.Throws<InvalidOperationException>(() => PaymentProviderStartupValidator.Validate(context, registry));

            Assert.Equal(2, errors.Count);
            Assert.Contains(errors, message => message.Contains("'CP-TYPO'") && message.Contains("Unknown payment provider 'Worldline'"));
            Assert.Contains(errors, message => message.Contains($"'{OverrideChargePointId}'") && message.Contains("not configured"));
            Assert.DoesNotContain(errors, message => message.Contains(DefaultChargePointId));
            Assert.Contains("CP-TYPO", error.Message);
            Assert.Contains(OverrideChargePointId, error.Message);
        }

        private static (PaymentGatewayRegistry Registry, FakePaymentGateway Stripe, FakePaymentGateway Saferpay) TwoProviderRegistry()
        {
            var stripe = new FakePaymentGateway(PaymentProviderNames.Stripe)
            {
                CreateResponse = new PaymentCheckoutSession
                {
                    SessionReference = "cs_test_1",
                    RedirectUrl = "https://stripe.example.com/checkout/cs_test_1"
                }
            };
            var saferpay = new FakePaymentGateway(PaymentProviderNames.Saferpay)
            {
                CheckoutLifetime = TimeSpan.FromHours(1),
                CapturedCheckoutRequiresRefund = true
            };
            var registry = new PaymentGatewayRegistry(new IPaymentGateway[] { stripe, saferpay }, PaymentProviderNames.Stripe);
            return (registry, stripe, saferpay);
        }

        private static (PaymentCoordinator Coordinator, FakePaymentGateway Stripe, FakePaymentGateway Saferpay) TwoProviderCoordinator()
        {
            var (registry, stripe, saferpay) = TwoProviderRegistry();
            var coordinator = new PaymentCoordinator(
                registry,
                new PaymentCheckoutSettings
                {
                    DefaultProvider = PaymentProviderNames.Stripe,
                    ReturnBaseUrl = ReturnBaseUrl,
                    Currency = "eur",
                    ProductName = "Charging session"
                },
                Options.Create(new PaymentFlowOptions { StartWindowMinutes = 7 }),
                NullLogger<PaymentCoordinator>.Instance,
                () => Now);
            return (coordinator, stripe, saferpay);
        }

        private static ChargePoint AddPricedChargePoint(OCPPCoreContext context, string chargePointId, string? paymentProvider)
        {
            var chargePoint = new ChargePoint
            {
                ChargePointId = chargePointId,
                MaxSessionKwh = 10,
                PricePerKwh = 0.40m,
                UserSessionFee = 0.50m,
                PaymentProvider = paymentProvider
            };
            context.ChargePoints.Add(chargePoint);
            context.SaveChanges();
            return chargePoint;
        }

        private static PaymentSessionRequest CheckoutRequest(string chargePointId, int connectorId = 1) => new()
        {
            ChargePointId = chargePointId,
            ConnectorId = connectorId,
            ChargeTagId = $"TAG-{chargePointId}-{connectorId}"
        };

        /// <summary>Creates a Worldline checkout, confirms it and starts charging on transaction <paramref name="transactionId"/>.</summary>
        private static ChargePaymentReservation StartSession(
            OCPPCoreContext context,
            PaymentCoordinator coordinator,
            FakePaymentGateway saferpay,
            int transactionId)
        {
            var created = coordinator.CreateCheckoutSession(context, CheckoutRequest(OverrideChargePointId));
            var reservation = created.Reservation;
            Assert.Equal(PaymentProviderNames.Saferpay, reservation.PaymentProvider);
            saferpay.CheckoutStatus = CompletedCheckout(reservation.ReservationId, "fake_token_1", "wl_tx_1", inlineAuthorization: true, amountMinor: 450);
            Assert.True(coordinator.ConfirmReservation(context, reservation.ReservationId, null).Success);

            context.Transactions.Add(new Transaction
            {
                TransactionId = transactionId,
                ChargePointId = OverrideChargePointId,
                ConnectorId = 1,
                StartTagId = reservation.OcppIdTag,
                StartTime = Now.AddMinutes(1),
                MeterStart = 0
            });
            context.SaveChanges();
            coordinator.MarkTransactionStarted(context, OverrideChargePointId, 1, reservation.OcppIdTag, transactionId);
            Assert.Equal(PaymentReservationStatus.Charging, reservation.Status);
            return reservation;
        }

        /// <summary>Operator sets the charge point back to "Default" (Stripe) while the Worldline session is in flight.</summary>
        private static void SwitchBackToDefault(OCPPCoreContext context, ChargePoint chargePoint)
        {
            chargePoint.PaymentProvider = null;
            context.SaveChanges();
            Assert.Equal(
                PaymentProviderNames.Stripe,
                PaymentProviderResolver.ResolveProviderName(chargePoint.PaymentProvider, PaymentProviderNames.Stripe));
        }
    }
}
