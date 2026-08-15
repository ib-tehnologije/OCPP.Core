using System;
using Microsoft.Extensions.Configuration;

namespace OCPP.Core.Server.Maintenance
{
    internal sealed class MessageLogRetentionOptions
    {
        internal const string SectionName = "Maintenance:MessageLogRetention";
        internal const int MaximumBatchSize = 1000;

        private MessageLogRetentionOptions(
            bool enabled,
            bool dryRun,
            int retentionDays,
            int batchSize,
            int cleanupIntervalMinutes)
        {
            Enabled = enabled;
            DryRun = dryRun;
            RetentionDays = retentionDays;
            BatchSize = batchSize;
            CleanupInterval = TimeSpan.FromMinutes(cleanupIntervalMinutes);
        }

        internal bool Enabled { get; }
        internal bool DryRun { get; }
        internal int RetentionDays { get; }
        internal int BatchSize { get; }
        internal TimeSpan CleanupInterval { get; }

        internal static bool TryRead(
            IConfiguration configuration,
            out MessageLogRetentionOptions options,
            out string error)
        {
            IConfigurationSection section = configuration.GetSection(SectionName);
            if (!TryBoolean(section, "Enabled", false, out bool enabled, out error) ||
                !TryBoolean(section, "DryRun", true, out bool dryRun, out error) ||
                !TryInteger(
                    section,
                    "RetentionDays",
                    30,
                    1,
                    int.MaxValue,
                    out int retentionDays,
                    out error) ||
                !TryInteger(
                    section,
                    "BatchSize",
                    1000,
                    1,
                    MaximumBatchSize,
                    out int batchSize,
                    out error) ||
                !TryInteger(
                    section,
                    "CleanupIntervalMinutes",
                    60,
                    1,
                    int.MaxValue,
                    out int cleanupIntervalMinutes,
                    out error))
            {
                options = null;
                return false;
            }

            options = new MessageLogRetentionOptions(
                enabled,
                dryRun,
                retentionDays,
                batchSize,
                cleanupIntervalMinutes);
            error = string.Empty;
            return true;
        }

        private static bool TryBoolean(
            IConfigurationSection section,
            string key,
            bool defaultValue,
            out bool value,
            out string error)
        {
            string raw = section[key];
            if (raw == null)
            {
                value = defaultValue;
                error = string.Empty;
                return true;
            }

            if (bool.TryParse(raw, out value))
            {
                error = string.Empty;
                return true;
            }

            error = $"{SectionName}:{key} must be true or false.";
            return false;
        }

        private static bool TryInteger(
            IConfigurationSection section,
            string key,
            int defaultValue,
            int minimum,
            int maximum,
            out int value,
            out string error)
        {
            string raw = section[key];
            if (raw == null)
            {
                value = defaultValue;
                error = string.Empty;
                return true;
            }

            if (int.TryParse(raw, out value) && value >= minimum && value <= maximum)
            {
                error = string.Empty;
                return true;
            }

            error = $"{SectionName}:{key} must be between {minimum} and {maximum}.";
            return false;
        }
    }
}
