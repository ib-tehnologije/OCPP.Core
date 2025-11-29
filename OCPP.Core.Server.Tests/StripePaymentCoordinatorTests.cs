using System;
using Microsoft.EntityFrameworkCore;
using OCPP.Core.Database;
using OCPP.Core.Server.Payments;
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
    }
}
