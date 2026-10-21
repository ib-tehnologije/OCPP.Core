/*
 * OCPP.Core - https://github.com/dallmann-consulting/OCPP.Core
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace OCPP.Core.Server.Payments.Providers.Saferpay
{
    /// <summary>
    /// Removes Saferpay credentials and identifiers from text that is logged, persisted or shown to users.
    /// </summary>
    internal static class SaferpayTextSanitizer
    {
        internal const string Redacted = "[redacted]";

        /// <summary>Credentials shorter than this are not replaced, so a trivial value cannot mangle every message.</summary>
        private const int MinimumSecretLength = 4;

        // Saferpay tokens, transaction ids and capture ids ("..._c") are long alphanumeric runs. Lookarounds are
        // used instead of \b so ids embedded after an underscore (mock ids such as mock_tx_<hex>) are also caught.
        private static readonly Regex IdentifierPattern = new Regex(
            @"(?<![A-Za-z0-9])[A-Za-z0-9]{20,}(?:_c)?(?![A-Za-z0-9])",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static string Sanitize(string text, SaferpayOptions options)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            string sanitized = text;
            foreach (var secret in Secrets(options))
            {
                sanitized = sanitized.Replace(secret, Redacted, StringComparison.OrdinalIgnoreCase);
            }

            return IdentifierPattern.Replace(sanitized, Redacted);
        }

        private static IEnumerable<string> Secrets(SaferpayOptions options)
        {
            if (options == null)
            {
                return Array.Empty<string>();
            }

            var secrets = new List<string>();
            AddSecret(secrets, options.ApiPassword);
            AddSecret(secrets, options.ApiUsername);
            AddSecret(secrets, options.NotifySecret);
            if (!string.IsNullOrEmpty(options.ApiUsername) || !string.IsNullOrEmpty(options.ApiPassword))
            {
                AddSecret(secrets, Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.ApiUsername}:{options.ApiPassword}")));
            }

            // Longest first, so a secret that contains another one is removed as a whole.
            return secrets.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(secret => secret.Length);
        }

        private static void AddSecret(List<string> secrets, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            string trimmed = value.Trim();
            if (trimmed.Length >= MinimumSecretLength)
            {
                secrets.Add(trimmed);
            }

            if (!string.Equals(trimmed, value, StringComparison.Ordinal) && value.Length >= MinimumSecretLength)
            {
                secrets.Add(value);
            }
        }
    }
}
