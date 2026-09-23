using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OCPP.Core.Database;
using OCPP.Core.Server.Messages_OCPP16;
using OCPP.Core.Server.Payments;

namespace OCPP.Core.Server
{
    public partial class ControllerOCPP16
    {
        private MeterEvidenceResult ProcessMeterEvidence(
            Transaction transaction,
            ICollection<MeterValue> meterValues,
            DateTimeOffset fallbackTimestamp,
            string source,
            bool terminal)
        {
            if (transaction == null) return null;
            var energyGroups = (meterValues ?? Array.Empty<MeterValue>())
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
                    ObservedAtUtc = fallbackTimestamp.UtcDateTime,
                    Protocol = "OCPP1.6",
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

        private static bool IsEnergy(SampledValue sample) =>
            sample.Measurand == SampledValueMeasurand.Energy_Active_Import_Register || sample.Measurand == null;

        private static MeterEvidenceObservation CreateObservation(
            MeterValue meterValue,
            DateTimeOffset fallbackTimestamp,
            string source)
        {
            var energySamples = meterValue.SampledValue.Where(IsEnergy).ToList();
            var rawEvidence = string.Join(";", energySamples.Select(sample =>
                $"{sample.Value}[{sample.Unit?.ToString() ?? "Wh"},{sample.Phase?.ToString() ?? "overall"}]"));
            var overall = energySamples.LastOrDefault(sample => !sample.Phase.HasValue);
            MeterEvidenceObservation observation;
            if (overall != null)
            {
                observation = new MeterEvidenceObservation
                {
                    RawValue = overall.Value,
                    Unit = overall.Unit?.ToString()
                };
            }
            else
            {
                var totalKwh = 0d;
                var totalToleranceKwh = 0d;
                SampledValue invalid = null;
                foreach (var sample in energySamples)
                {
                    if (!MeterEvidenceProcessor.TryNormalizeEnergy(
                            sample.Value,
                            sample.Unit?.ToString(),
                            0,
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
                        RawValue = invalid.Value,
                        Unit = invalid.Unit?.ToString()
                    };
            }

            observation.RawEvidenceValue = rawEvidence;
            observation.ObservedAtUtc = (meterValue.Timestamp == default ? fallbackTimestamp : meterValue.Timestamp).UtcDateTime;
            observation.Protocol = "OCPP1.6";
            observation.Source = source;
            AddOfferedPower(observation, meterValue.SampledValue);
            return observation;
        }

        internal static void AddOfferedPower(MeterEvidenceObservation observation, IEnumerable<SampledValue> samples)
        {
            var power = samples.Where(sample => sample.Measurand == SampledValueMeasurand.Power_Offered).ToList();
            var selected = power.Where(sample => !sample.Phase.HasValue).TakeLast(1).ToList();
            if (selected.Count == 0) selected = power.Where(sample => sample.Phase.HasValue).ToList();
            if (selected.Count == 0) return;
            observation.CandidateOfferedPowerRawValue = string.Join(";", selected.Select(sample => sample.Value));
            observation.CandidateOfferedPowerUnit = string.Join(";", selected.Select(sample => sample.Unit?.ToString() ?? string.Empty));
            observation.CandidateOfferedPowerMultiplier = 0;
            var totalKw = 0d;
            foreach (var sample in selected)
            {
                if (!MeterEvidenceProcessor.TryNormalizePower(sample.Value, sample.Unit?.ToString(), 0, out var valueKw, out _)) return;
                totalKw += valueKw;
            }
            observation.OfferedPowerRawValue = totalKw.ToString("R", CultureInfo.InvariantCulture);
            observation.OfferedPowerUnit = "kW";
        }

        private MeterEvidenceResult ProcessStopMeterEvidence(
            Transaction transaction,
            long rawMeterWh,
            DateTimeOffset timestamp,
            string source)
        {
            return MeterEvidenceProcessor.Process(DbContext, transaction, new MeterEvidenceObservation
            {
                RawValue = rawMeterWh.ToString(CultureInfo.InvariantCulture),
                Unit = "Wh",
                ObservedAtUtc = timestamp.UtcDateTime,
                Protocol = "OCPP1.6",
                Source = source,
                IsTerminal = true
            });
        }

        private MeterEvidenceResult ProcessStartMeterEvidence(
            Transaction transaction,
            long rawMeterWh,
            DateTimeOffset timestamp)
        {
            return MeterEvidenceProcessor.Process(DbContext, transaction, new MeterEvidenceObservation
            {
                RawValue = rawMeterWh.ToString(CultureInfo.InvariantCulture),
                Unit = "Wh",
                ObservedAtUtc = timestamp.UtcDateTime,
                Protocol = "OCPP1.6",
                Source = "StartTransaction",
                IsTerminal = false
            });
        }
    }
}
