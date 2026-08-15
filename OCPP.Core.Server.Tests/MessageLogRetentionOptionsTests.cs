using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using OCPP.Core.Server.Maintenance;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    public class MessageLogRetentionOptionsTests
    {
        [Fact]
        public void TryRead_UsesDisabledDryRunThirtyDayDefaults()
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection()
                .Build();

            bool valid = MessageLogRetentionOptions.TryRead(
                configuration,
                out var options,
                out string error);

            Assert.True(valid, error);
            Assert.False(options.Enabled);
            Assert.True(options.DryRun);
            Assert.Equal(30, options.RetentionDays);
            Assert.Equal(1000, options.BatchSize);
            Assert.Equal(TimeSpan.FromMinutes(60), options.CleanupInterval);
        }

        [Fact]
        public void TryRead_UsesExplicitValidValues()
        {
            IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
            {
                ["Maintenance:MessageLogRetention:Enabled"] = "true",
                ["Maintenance:MessageLogRetention:DryRun"] = "false",
                ["Maintenance:MessageLogRetention:RetentionDays"] = "45",
                ["Maintenance:MessageLogRetention:BatchSize"] = "2500",
                ["Maintenance:MessageLogRetention:CleanupIntervalMinutes"] = "15"
            });

            bool valid = MessageLogRetentionOptions.TryRead(
                configuration,
                out var options,
                out string error);

            Assert.True(valid, error);
            Assert.True(options.Enabled);
            Assert.False(options.DryRun);
            Assert.Equal(45, options.RetentionDays);
            Assert.Equal(2500, options.BatchSize);
            Assert.Equal(TimeSpan.FromMinutes(15), options.CleanupInterval);
        }

        [Theory]
        [InlineData("RetentionDays", "0")]
        [InlineData("RetentionDays", "abc")]
        [InlineData("BatchSize", "0")]
        [InlineData("BatchSize", "10001")]
        [InlineData("CleanupIntervalMinutes", "0")]
        [InlineData("Enabled", "not-bool")]
        [InlineData("DryRun", "not-bool")]
        public void TryRead_RejectsMalformedOrUnsafeValues(string key, string value)
        {
            IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
            {
                [$"Maintenance:MessageLogRetention:{key}"] = value
            });

            bool valid = MessageLogRetentionOptions.TryRead(
                configuration,
                out _,
                out string error);

            Assert.False(valid);
            Assert.Contains(key, error, StringComparison.Ordinal);
            if (value.IndexOfAny("abcdefghijklmnopqrstuvwxyz".ToCharArray()) >= 0)
            {
                Assert.DoesNotContain(value, error, StringComparison.Ordinal);
            }
        }

        private static IConfiguration BuildConfiguration(
            IDictionary<string, string?> values)
        {
            return new ConfigurationBuilder()
                .AddInMemoryCollection(values)
                .Build();
        }
    }
}
