using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OCPP.Core.Database;
using OCPP.Core.Server.Messages_OCPP21;
using OCPP.Core.Server.Payments;

namespace OCPP.Core.Server
{
    public partial class ControllerOCPP21
    {
        private MeterEvidenceResult ProcessMeterEvidence(
            Transaction transaction,
            ICollection<MeterValueType> meterValues,
            DateTimeOffset fallbackTimestamp,
            string source,
            bool terminal)
        {
            if (transaction == null) return null;
            var energyGroups = (meterValues ?? Array.Empty<MeterValueType>())
                .Select((value, index) => new { Value = value, Index = index })
                .Where(item => item.Value.SampledValue?.Any(IsEnergy) == true)
                .OrderBy(item => item.Value.Timestamp == default ? fallbackTimestamp : item.Value.Timestamp)
                .ThenBy(item => item.Index)
                .Select(item => item.Value)
                .ToList();
            if (energyGroups.Count == 0)
            {
                if (!terminal) return null;
                return MeterEvidenceProcessor.Process(DbContext, transaction, new MeterEvidenceObservation
                {
                    RawValue = string.Empty,
                    Unit = null,
                    ObservedAtUtc = fallbackTimestamp.UtcDateTime,
                    Protocol = "OCPP2.1",
                    Source = source,
                    IsTerminal = true
                });
            }

            MeterEvidenceResult result = null;
            for (var index = 0; index < energyGroups.Count; index++)
            {
                var observation = CreateObservation(energyGroups[index], fallbackTimestamp, source);
                observation.IsTerminal = terminal && index == energyGroups.Count - 1;
                result = MeterEvidenceProcessor.Process(DbContext, transaction, observation);
            }
            return result;
        }

        private static bool IsEnergy(SampledValueType sample) =>
            sample.Measurand == MeasurandEnumType.Energy_Active_Import_Register || sample.Measurand == null;

        private static MeterEvidenceObservation CreateObservation(
            MeterValueType meterValue,
            DateTimeOffset fallbackTimestamp,
            string source)
        {
            var energySamples = meterValue.SampledValue.Where(IsEnergy).ToList();
            var rawEvidence = string.Join(";", energySamples.Select(sample =>
                $"{sample.Value.ToString("R", CultureInfo.InvariantCulture)}[{sample.UnitOfMeasure?.Unit ?? "Wh"},10^{sample.UnitOfMeasure?.Multiplier ?? 0},{sample.Phase?.ToString() ?? "overall"}]"));
            var overall = energySamples.LastOrDefault(sample => !sample.Phase.HasValue);
            MeterEvidenceObservation observation;
            if (overall != null)
            {
                observation = new MeterEvidenceObservation
                {
                    RawValue = overall.Value.ToString("R", CultureInfo.InvariantCulture),
                    Unit = overall.UnitOfMeasure?.Unit,
                    UnitMultiplier = overall.UnitOfMeasure?.Multiplier ?? 0
                };
            }
            else
            {
                var totalKwh = 0d;
                var totalToleranceKwh = 0d;
                SampledValueType invalid = null;
                foreach (var sample in energySamples)
                {
                    if (!MeterEvidenceProcessor.TryNormalizeEnergy(
                            sample.Value.ToString("R", CultureInfo.InvariantCulture),
                            sample.UnitOfMeasure?.Unit,
                            sample.UnitOfMeasure?.Multiplier ?? 0,
                            out var valueKwh,
                            out var toleranceKwh,
                            out _))
                    {
                        invalid = sample;
                        break;
                    }
                    totalKwh += valueKwh;
                    totalToleranceKwh += toleranceKwh;
                }
                observation = invalid == null
                    ? new MeterEvidenceObservation
                    {
                        RawValue = totalKwh.ToString("R", CultureInfo.InvariantCulture),
                        Unit = "kWh",
                        EnergyToleranceKwh = totalToleranceKwh
                    }
                    : new MeterEvidenceObservation
                    {
                        RawValue = invalid.Value.ToString("R", CultureInfo.InvariantCulture),
                        Unit = invalid.UnitOfMeasure?.Unit,
                        UnitMultiplier = invalid.UnitOfMeasure?.Multiplier ?? 0
                    };
            }

            observation.RawEvidenceValue = rawEvidence;
            observation.ObservedAtUtc = (meterValue.Timestamp == default ? fallbackTimestamp : meterValue.Timestamp).UtcDateTime;
            observation.Protocol = "OCPP2.1";
            observation.Source = source;
            observation.SkipMeterStartSeed = string.Equals(source, "TransactionEvent.Started", StringComparison.Ordinal);
            AddOfferedPower(observation, meterValue.SampledValue);
            return observation;
        }

        internal static void AddOfferedPower(MeterEvidenceObservation observation, IEnumerable<SampledValueType> samples)
        {
            var power = samples.Where(sample =>
                sample.Measurand == MeasurandEnumType.Power_Offered ||
                sample.Measurand == MeasurandEnumType.Power_Import_Offered).ToList();
            var selected = power.Where(sample => !sample.Phase.HasValue).TakeLast(1).ToList();
            if (selected.Count == 0) selected = power.Where(sample => sample.Phase.HasValue).ToList();
            if (selected.Count == 0) return;
            observation.CandidateOfferedPowerRawValue = string.Join(";", selected.Select(sample => sample.Value.ToString("R", CultureInfo.InvariantCulture)));
            observation.CandidateOfferedPowerUnit = string.Join(";", selected.Select(sample => sample.UnitOfMeasure?.Unit ?? string.Empty));
            var multipliers = selected.Select(sample => sample.UnitOfMeasure?.Multiplier ?? 0).Distinct().ToList();
            observation.CandidateOfferedPowerMultiplier = multipliers.Count == 1 ? multipliers[0] : null;
            var totalKw = 0d;
            foreach (var sample in selected)
            {
                if (!MeterEvidenceProcessor.TryNormalizePower(
                        sample.Value.ToString("R", CultureInfo.InvariantCulture),
                        sample.UnitOfMeasure?.Unit,
                        sample.UnitOfMeasure?.Multiplier ?? 0,
                        out var valueKw,
                        out _)) return;
                totalKw += valueKw;
            }
            observation.OfferedPowerRawValue = totalKw.ToString("R", CultureInfo.InvariantCulture);
            observation.OfferedPowerUnit = "kW";
            observation.OfferedPowerMultiplier = 0;
        }
    }
}
