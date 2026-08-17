using System;
using Microsoft.Extensions.Logging;
using OCPP.Core.Database;

namespace OCPP.Core.Server.Payments
{
    internal static class FinancialSettlementCalculator
    {
        public static FinancialSettlementCalculation Calculate(
            ChargePaymentReservation reservation,
            Transaction transaction,
            PaymentFlowOptions flowOptions,
            DateTime asOfUtc,
            ILogger logger = null)
        {
            if (reservation == null) throw new ArgumentNullException(nameof(reservation));
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));

            flowOptions ??= new PaymentFlowOptions();
            var hasValidDeliveredEnergy = transaction.MeterStop.HasValue &&
                                          transaction.MeterStart >= 0 &&
                                          transaction.MeterStop.Value >= 0 &&
                                          transaction.MeterStop.Value >= transaction.MeterStart;
            var actualEnergyKwh = hasValidDeliveredEnergy
                ? Math.Max(0, transaction.MeterStop.Value - transaction.MeterStart)
                : 0;
            var shouldNoCharge = ShouldTreatAsNoChargeForDeliveredEnergy(
                actualEnergyKwh,
                hasValidDeliveredEnergy,
                flowOptions,
                out var noChargeReason);
            var energyCostCents = shouldNoCharge
                ? 0L
                : CalculateAmountInCents(actualEnergyKwh, reservation.PricePerKwh);
            var configuredSessionFeeCents = CalculateFlatAmountInCents(reservation.UserSessionFee);
            string sessionFeeSuppressionReason = null;
            var shouldChargeSessionFee = !shouldNoCharge && ShouldChargeSessionFee(
                reservation,
                actualEnergyKwh,
                hasValidDeliveredEnergy,
                flowOptions,
                out sessionFeeSuppressionReason);
            var sessionFeeCents = shouldChargeSessionFee
                ? configuredSessionFeeCents
                : 0L;
            if (shouldNoCharge)
            {
                sessionFeeSuppressionReason = noChargeReason;
            }

            var idleSnapshot = shouldNoCharge
                ? new IdleFeeSnapshot()
                : IdleFeeCalculator.CalculateSnapshot(
                    transaction,
                    reservation,
                    flowOptions,
                    asOfUtc,
                    logger);
            var usageFeeMinutes = shouldNoCharge
                ? 0
                : reservation.UsageFeeAnchorMinutes == 1
                    ? idleSnapshot.TotalMinutes
                    : CalculateUsageFeeMinutes(transaction, reservation, flowOptions, asOfUtc, logger);
            var usageFeeCents = shouldNoCharge || usageFeeMinutes <= 0
                ? 0L
                : CalculateUsageFeeInCents(usageFeeMinutes, reservation.UsageFeePerMinute);

            return new FinancialSettlementCalculation
            {
                HasValidDeliveredEnergy = hasValidDeliveredEnergy,
                ShouldNoChargeForDeliveredEnergy = shouldNoCharge,
                DeliveredEnergyNoChargeReason = noChargeReason,
                ConfiguredSessionFeeCents = configuredSessionFeeCents,
                SessionFeeSuppressionReason = sessionFeeSuppressionReason,
                ActualEnergyKwh = actualEnergyKwh,
                EnergyCostCents = energyCostCents,
                SessionFeeCents = sessionFeeCents,
                UsageFeeMinutes = usageFeeMinutes,
                UsageFeeCents = usageFeeCents,
                AmountToCaptureCents = checked(energyCostCents + sessionFeeCents + usageFeeCents),
                MinimumChargeAmountCents = Math.Max(0, flowOptions.MinimumChargeAmountCents),
                IdleSnapshot = idleSnapshot
            };
        }

        private static int CalculateUsageFeeMinutes(
            Transaction transaction,
            ChargePaymentReservation reservation,
            PaymentFlowOptions flowOptions,
            DateTime asOfUtc,
            ILogger logger)
        {
            if (reservation.UsageFeePerMinute <= 0 || transaction.StartTime == default)
            {
                return 0;
            }

            var stopTimeUtc = transaction.StopTime ?? asOfUtc;
            if (stopTimeUtc <= transaction.StartTime)
            {
                return 0;
            }

            var billableMinutes = IdleFeeCalculator.CalculateBillableUsageMinutes(
                transaction.StartTime,
                stopTimeUtc,
                reservation.StartUsageFeeAfterMinutes,
                flowOptions,
                logger);
            return Math.Min(
                Math.Max(0, billableMinutes),
                Math.Max(0, reservation.MaxUsageFeeMinutes));
        }

        private static bool ShouldChargeSessionFee(
            ChargePaymentReservation reservation,
            double actualEnergyKwh,
            bool hasValidDeliveredEnergy,
            PaymentFlowOptions flowOptions,
            out string suppressionReason)
        {
            suppressionReason = null;
            if (reservation.UserSessionFee <= 0)
            {
                return false;
            }

            if (!hasValidDeliveredEnergy)
            {
                suppressionReason = "missing or inconsistent delivered energy";
                return false;
            }

            var minimumSessionFeeKwh = Math.Max(0, flowOptions.MinimumSessionFeeKwh);
            if (minimumSessionFeeKwh > 0 && Convert.ToDecimal(actualEnergyKwh) < minimumSessionFeeKwh)
            {
                suppressionReason = $"delivered energy below {minimumSessionFeeKwh:0.###} kWh";
                return false;
            }

            return true;
        }

        private static bool ShouldTreatAsNoChargeForDeliveredEnergy(
            double actualEnergyKwh,
            bool hasValidDeliveredEnergy,
            PaymentFlowOptions flowOptions,
            out string reason)
        {
            reason = null;
            if (!hasValidDeliveredEnergy)
            {
                reason = "missing or inconsistent delivered energy";
                return true;
            }

            var minimumSessionFeeKwh = Math.Max(0, flowOptions.MinimumSessionFeeKwh);
            if (minimumSessionFeeKwh > 0 && Convert.ToDecimal(actualEnergyKwh) < minimumSessionFeeKwh)
            {
                reason = $"delivered energy below configured minimum session energy {minimumSessionFeeKwh:0.###} kWh";
                return true;
            }

            return false;
        }

        private static long CalculateAmountInCents(double energyKwh, decimal pricePerKwh)
        {
            var subtotal = Math.Round(pricePerKwh * Convert.ToDecimal(energyKwh), 2, MidpointRounding.AwayFromZero);
            return checked((long)Math.Round(subtotal * 100m, 0, MidpointRounding.AwayFromZero));
        }

        private static long CalculateUsageFeeInCents(int minutes, decimal pricePerMinute)
        {
            if (minutes <= 0 || pricePerMinute <= 0) return 0;
            var subtotal = Math.Round(pricePerMinute * minutes, 2, MidpointRounding.AwayFromZero);
            return checked((long)Math.Round(subtotal * 100m, 0, MidpointRounding.AwayFromZero));
        }

        private static long CalculateFlatAmountInCents(decimal amount)
        {
            if (amount <= 0) return 0;
            var subtotal = Math.Round(amount, 2, MidpointRounding.AwayFromZero);
            return checked((long)Math.Round(subtotal * 100m, 0, MidpointRounding.AwayFromZero));
        }
    }

    internal sealed class FinancialSettlementCalculation
    {
        public bool HasValidDeliveredEnergy { get; init; }
        public bool ShouldNoChargeForDeliveredEnergy { get; init; }
        public string DeliveredEnergyNoChargeReason { get; init; }
        public long ConfiguredSessionFeeCents { get; init; }
        public string SessionFeeSuppressionReason { get; init; }
        public double ActualEnergyKwh { get; init; }
        public long EnergyCostCents { get; init; }
        public long SessionFeeCents { get; init; }
        public int UsageFeeMinutes { get; init; }
        public long UsageFeeCents { get; init; }
        public long AmountToCaptureCents { get; init; }
        public long MinimumChargeAmountCents { get; init; }
        public IdleFeeSnapshot IdleSnapshot { get; init; }
    }
}
