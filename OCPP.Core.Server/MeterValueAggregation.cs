using System;
using System.Collections.Generic;
using System.Linq;

namespace OCPP.Core.Server
{
    internal sealed class PhaseAwareMeasurementAggregate
    {
        private readonly Dictionary<string, double> _phaseValues = new(StringComparer.OrdinalIgnoreCase);
        private double? _overallValue;

        public void Add(double normalizedValue, string phase)
        {
            if (string.IsNullOrWhiteSpace(phase))
            {
                _overallValue = normalizedValue;
                return;
            }

            _phaseValues[phase] = normalizedValue;
        }

        public double? GetValue()
        {
            return _phaseValues.Count > 0 ? _phaseValues.Values.Sum() : _overallValue;
        }

        public double? GetValuePreferOverall()
        {
            return _overallValue ?? (_phaseValues.Count > 0 ? _phaseValues.Values.Sum() : null);
        }
    }

    internal static class MeterValueAggregation
    {
        public static double? NormalizePowerToKw(double rawValue, string unit)
        {
            if (string.IsNullOrWhiteSpace(unit) ||
                unit.Equals("W", StringComparison.OrdinalIgnoreCase) ||
                unit.Equals("VA", StringComparison.OrdinalIgnoreCase) ||
                unit.Equals("var", StringComparison.OrdinalIgnoreCase))
            {
                return rawValue / 1000d;
            }

            if (unit.Equals("kW", StringComparison.OrdinalIgnoreCase) ||
                unit.Equals("KW", StringComparison.OrdinalIgnoreCase) ||
                unit.Equals("kVA", StringComparison.OrdinalIgnoreCase) ||
                unit.Equals("kvar", StringComparison.OrdinalIgnoreCase))
            {
                return rawValue;
            }

            return null;
        }

        public static double? NormalizeCurrentToAmpere(double rawValue, string unit)
        {
            if (string.IsNullOrWhiteSpace(unit) || unit.Equals("A", StringComparison.OrdinalIgnoreCase))
            {
                return rawValue;
            }

            if (unit.Equals("mA", StringComparison.OrdinalIgnoreCase))
            {
                return rawValue / 1000d;
            }

            return null;
        }
    }
}
