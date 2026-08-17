using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OCPP.Core.Server.Payments.Recovery
{
    public sealed class FinancialRecoveryManifest
    {
        private static readonly HashSet<string> SupportedOperations = new(StringComparer.OrdinalIgnoreCase)
        {
            "recover-settlement",
            "release-authorization",
            "recover-invoice"
        };

        private FinancialRecoveryManifest(
            int schemaVersion,
            IReadOnlyList<FinancialRecoveryManifestEntry> entries,
            string sha256)
        {
            SchemaVersion = schemaVersion;
            Entries = entries;
            Sha256 = sha256;
        }

        public int SchemaVersion { get; }
        public IReadOnlyList<FinancialRecoveryManifestEntry> Entries { get; }
        public string Sha256 { get; }

        public static FinancialRecoveryManifest Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new InvalidOperationException("Recovery manifest is empty.");
            }

            FinancialRecoveryManifestDocument document;
            try
            {
                document = JsonSerializer.Deserialize<FinancialRecoveryManifestDocument>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("Recovery manifest is not valid JSON.", ex);
            }

            if (document == null || document.SchemaVersion != 1)
            {
                throw new InvalidOperationException("Recovery manifest schemaVersion must be 1.");
            }

            var entries = document.Entries ?? new List<FinancialRecoveryManifestEntry>();
            foreach (var entry in entries)
            {
                if (entry == null || !SupportedOperations.Contains(entry.Operation ?? string.Empty))
                {
                    throw new InvalidOperationException("Recovery manifest contains an unsupported operation.");
                }

                if (entry.ReservationId == Guid.Empty)
                {
                    throw new InvalidOperationException("Every recovery entry requires a non-empty reservationId.");
                }
            }

            var duplicate = entries
                .GroupBy(entry => $"{entry.Operation?.Trim().ToLowerInvariant()}:{entry.ReservationId:D}")
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicate != null)
            {
                throw new InvalidOperationException("Recovery manifest contains a duplicate reservation operation.");
            }

            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
            return new FinancialRecoveryManifest(document.SchemaVersion, entries.AsReadOnly(), digest);
        }

        public void RequireExecutionConfirmation(bool execute, string confirmationSha256)
        {
            if (!execute)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(confirmationSha256) ||
                !CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(Sha256),
                    Encoding.ASCII.GetBytes(confirmationSha256.Trim().ToLowerInvariant())))
            {
                throw new InvalidOperationException("Execution requires the exact manifest SHA-256 digest.");
            }
        }

        private sealed class FinancialRecoveryManifestDocument
        {
            public int SchemaVersion { get; set; }
            public List<FinancialRecoveryManifestEntry> Entries { get; set; }
        }
    }

    public sealed class FinancialRecoveryManifestEntry
    {
        public string Operation { get; set; }
        public Guid ReservationId { get; set; }
    }
}
