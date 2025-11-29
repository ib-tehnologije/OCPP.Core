using OCPP.Core.Database;

namespace OCPP.Core.Server.Payments
{
    public partial class StripePaymentCoordinator
    {
        internal static long TestCalculateAmountInCents(double energyKwh, decimal pricePerKwh) =>
            CalculateAmountInCents(energyKwh, pricePerKwh);

        internal static long TestCalculateUsageFeeInCents(int minutes, decimal pricePerMinute) =>
            CalculateUsageFeeInCents(minutes, pricePerMinute);

        internal static long TestCalculateFlatAmountInCents(decimal amount) =>
            CalculateFlatAmountInCents(amount);

        internal static string TestNormalizeChargeTag(string tag) => NormalizeChargeTag(tag);

        internal static void TestPersistTransactionBreakdown(
            OCPPCoreContext dbContext,
            Transaction transaction,
            ChargePaymentReservation reservation,
            double energyKwh,
            long energyCostCents,
            int usageFeeMinutes,
            long usageFeeCents,
            long sessionFeeCents,
            long totalCents) =>
            PersistTransactionBreakdown(
                dbContext,
                transaction,
                reservation,
                energyKwh,
                energyCostCents,
                usageFeeMinutes,
                usageFeeCents,
                sessionFeeCents,
                totalCents);
    }
}
