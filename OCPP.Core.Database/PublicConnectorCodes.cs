using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

#nullable disable

namespace OCPP.Core.Database
{
    /// <summary>
    /// Builds the public charge point code, the connector label and the EVSE ID shown to drivers.
    ///
    /// Charge point code: the configured PublicDisplayCode, or, when it is empty, a code derived from a
    /// charge point ID shaped like an EVSE ID without separators (HRABCE1234501 -> HR*ABC*E1234501).
    ///
    /// Connector label: the last two digits of the charge point code as a number (00 counts as 1)
    /// followed by the connector letter (1 = A, 2 = B, ...), e.g. HR*ABC*E1234502 connector 2 -> 2B.
    /// When the code does not end in a digit or the connector number has no letter, the label falls back
    /// to the plain connector number.
    ///
    /// EVSE ID: {charge point code}*{connector label}, e.g. HR*ABC*E1234502*2B.
    ///
    /// These values are display-only. OCPP messages, QR links and stored data keep using the charge point ID
    /// and the numeric connector ID.
    /// </summary>
    public static class PublicConnectorCodes
    {
        private static readonly Regex EvseLikeChargePointId = new Regex(
            "^(?<country>[A-Z]{2})(?<operator>[A-Z0-9]{3})(?<station>E[A-Z0-9]+)$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        public static string ResolveChargePointCode(string publicDisplayCode, string chargePointId)
        {
            if (!string.IsNullOrWhiteSpace(publicDisplayCode))
            {
                return publicDisplayCode.Trim();
            }

            return DeriveChargePointCode(chargePointId);
        }

        public static string DeriveChargePointCode(string chargePointId)
        {
            if (string.IsNullOrWhiteSpace(chargePointId))
            {
                return null;
            }

            Match match = EvseLikeChargePointId.Match(chargePointId.Trim());
            if (!match.Success)
            {
                return null;
            }

            return string.Join(
                "*",
                match.Groups["country"].Value.ToUpperInvariant(),
                match.Groups["operator"].Value.ToUpperInvariant(),
                match.Groups["station"].Value.ToUpperInvariant());
        }

        public static string BuildConnectorLabel(string chargePointCode, int connectorId)
        {
            if (string.IsNullOrWhiteSpace(chargePointCode) || connectorId <= 0)
            {
                return null;
            }

            string fallback = connectorId.ToString(CultureInfo.InvariantCulture);
            if (connectorId > 26)
            {
                return fallback;
            }

            string lastSegment = chargePointCode.Trim()
                .Split(new[] { '*', '-' }, StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault();
            if (string.IsNullOrEmpty(lastSegment))
            {
                return fallback;
            }

            string trailingDigits = new string(lastSegment.Reverse().TakeWhile(char.IsAsciiDigit).Reverse().ToArray());
            if (trailingDigits.Length == 0)
            {
                return fallback;
            }

            string lastTwoDigits = trailingDigits.Length > 2
                ? trailingDigits.Substring(trailingDigits.Length - 2)
                : trailingDigits;
            int number = int.Parse(lastTwoDigits, NumberStyles.None, CultureInfo.InvariantCulture);
            if (number == 0)
            {
                number = 1;
            }

            char letter = (char)('A' + connectorId - 1);
            return number.ToString(CultureInfo.InvariantCulture) + letter;
        }

        public static string BuildEvseId(string chargePointCode, int connectorId)
        {
            string label = BuildConnectorLabel(chargePointCode, connectorId);
            return label == null
                ? null
                : $"{chargePointCode.Trim()}*{label}";
        }
    }
}
