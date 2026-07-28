/*
 * OCPP.Core - https://github.com/dallmann-consulting/OCPP.Core
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace OCPP.Core.Server.Payments
{
    internal sealed class ForeignVatNumberValidationResult
    {
        public bool Success { get; set; }
        public string Status { get; set; }
        public string NormalizedVatIdentifier { get; set; }
        public string ViesCountryCode { get; set; }
    }

    internal static class ForeignVatNumberValidator
    {
        private sealed class VatFormat
        {
            public VatFormat(string viesCountryCode, string bodyPattern)
            {
                ViesCountryCode = viesCountryCode;
                BodyPattern = new Regex(
                    $"^(?:{bodyPattern})$",
                    RegexOptions.CultureInvariant | RegexOptions.Compiled);
            }

            public string ViesCountryCode { get; }
            public Regex BodyPattern { get; }
        }

        // Formats follow the public European Commission VIES FAQ table. Greece uses
        // EL and Northern Ireland uses XI even though their selected country codes
        // are GR and GB respectively.
        private static readonly IReadOnlyDictionary<string, VatFormat> Formats =
            new Dictionary<string, VatFormat>(StringComparer.OrdinalIgnoreCase)
            {
                ["AT"] = new VatFormat("AT", @"U\d{8}"),
                ["BE"] = new VatFormat("BE", @"[01]\d{9}"),
                ["BG"] = new VatFormat("BG", @"\d{9,10}"),
                ["CY"] = new VatFormat("CY", @"\d{8}[A-Z]"),
                ["CZ"] = new VatFormat("CZ", @"\d{8,10}"),
                ["DE"] = new VatFormat("DE", @"\d{9}"),
                ["DK"] = new VatFormat("DK", @"\d{8}"),
                ["EE"] = new VatFormat("EE", @"\d{9}"),
                ["GR"] = new VatFormat("EL", @"\d{9}"),
                ["ES"] = new VatFormat("ES", @"[A-Z0-9]\d{7}[A-Z0-9]"),
                ["FI"] = new VatFormat("FI", @"\d{8}"),
                ["FR"] = new VatFormat("FR", @"[A-Z0-9]{2}\d{9}"),
                ["HU"] = new VatFormat("HU", @"\d{8}"),
                ["IE"] = new VatFormat("IE", @"(?:\d[A-Z0-9+*]\d{5}[A-Z]|\d{7}[A-Z]{1,2})"),
                ["IT"] = new VatFormat("IT", @"\d{11}"),
                ["LT"] = new VatFormat("LT", @"(?:\d{9}|\d{12})"),
                ["LU"] = new VatFormat("LU", @"\d{8}"),
                ["LV"] = new VatFormat("LV", @"\d{11}"),
                ["MT"] = new VatFormat("MT", @"\d{8}"),
                ["NL"] = new VatFormat("NL", @"\d{9}B\d{2}"),
                ["PL"] = new VatFormat("PL", @"\d{10}"),
                ["PT"] = new VatFormat("PT", @"\d{9}"),
                ["RO"] = new VatFormat("RO", @"\d{2,10}"),
                ["SE"] = new VatFormat("SE", @"\d{12}"),
                ["SI"] = new VatFormat("SI", @"\d{8}"),
                ["SK"] = new VatFormat("SK", @"\d{10}"),
                ["GB"] = new VatFormat("XI", @"(?:\d{9}|\d{12}|GD\d{3}|HA\d{3})")
            };

        public static ForeignVatNumberValidationResult ValidateAndNormalize(
            string selectedCountryCode,
            string originalIdentifier)
        {
            if (!Formats.TryGetValue(selectedCountryCode ?? string.Empty, out var format))
            {
                return Invalid("UnsupportedVatCountry");
            }

            var normalized = new string((originalIdentifier ?? string.Empty)
                .Where(ch => !IsPresentationSeparator(ch))
                .Select(char.ToUpperInvariant)
                .ToArray());

            if (normalized.Any(ch => !IsVatCharacter(ch)))
            {
                return Invalid("InvalidVatCharacters");
            }

            if (!normalized.StartsWith(format.ViesCountryCode, StringComparison.Ordinal))
            {
                return Invalid("InvalidVatCountryPrefix");
            }

            var body = normalized.Substring(format.ViesCountryCode.Length);
            if (!format.BodyPattern.IsMatch(body))
            {
                return Invalid("InvalidVatFormat");
            }

            return new ForeignVatNumberValidationResult
            {
                Success = true,
                Status = "Valid",
                NormalizedVatIdentifier = normalized,
                ViesCountryCode = format.ViesCountryCode
            };
        }

        private static bool IsPresentationSeparator(char value) =>
            value == ' ' ||
            value == '\u00A0' ||
            value == '-' ||
            value == '.';

        private static bool IsVatCharacter(char value) =>
            (value >= 'A' && value <= 'Z') ||
            (value >= '0' && value <= '9') ||
            value == '+' ||
            value == '*';

        private static ForeignVatNumberValidationResult Invalid(string status) =>
            new ForeignVatNumberValidationResult
            {
                Success = false,
                Status = status
            };
    }
}
