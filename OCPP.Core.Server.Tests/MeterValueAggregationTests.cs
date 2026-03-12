using Xunit;

namespace OCPP.Core.Server.Tests
{
    public class MeterValueAggregationTests
    {
        [Fact]
        public void PhaseAwareMeasurementAggregate_PrefersSummedPhaseValues()
        {
            var aggregate = new PhaseAwareMeasurementAggregate();

            aggregate.Add(22.0, null);
            aggregate.Add(7.2, "L1");
            aggregate.Add(7.1, "L2");
            aggregate.Add(7.3, "L3");

            var total = aggregate.GetValue();
            Assert.NotNull(total);
            Assert.Equal(21.6, total.Value, 3);
        }

        [Fact]
        public void PhaseAwareMeasurementAggregate_GetValuePreferOverall_UsesAggregateRegisterWhenPresent()
        {
            var aggregate = new PhaseAwareMeasurementAggregate();

            aggregate.Add(24.6, null);
            aggregate.Add(8.1, "L1");
            aggregate.Add(8.2, "L2");
            aggregate.Add(8.3, "L3");

            var total = aggregate.GetValuePreferOverall();
            Assert.NotNull(total);
            Assert.Equal(24.6, total.Value, 3);
        }

        [Fact]
        public void NormalizeCurrentToAmpere_ConvertsMilliAmpere()
        {
            Assert.Equal(12.5, MeterValueAggregation.NormalizeCurrentToAmpere(12.5, "A"));
            Assert.Equal(1.2, MeterValueAggregation.NormalizeCurrentToAmpere(1200, "mA"));
        }

        [Fact]
        public void NormalizePowerToKw_ConvertsWattsAndKeepsKw()
        {
            Assert.Equal(7.2, MeterValueAggregation.NormalizePowerToKw(7200, "W"));
            Assert.Equal(11.0, MeterValueAggregation.NormalizePowerToKw(11, "kW"));
        }
    }
}
