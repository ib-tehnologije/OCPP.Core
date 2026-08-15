using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OCPP.Core.Server.Maintenance;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    public class MessageLogRetentionServiceTests
    {
        [Fact]
        public async Task RunOnceAsync_DisabledConfigurationDoesNotResolveDatabase()
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection()
                .Build();
            using var provider = new ServiceCollection().BuildServiceProvider();
            var service = CreateService(provider, configuration);

            var result = await service.RunOnceAsync(
                new System.DateTime(2026, 8, 15, 12, 0, 0, System.DateTimeKind.Utc),
                default);

            Assert.Equal(MessageLogRetentionSweepStatus.Disabled, result.Status);
        }

        [Fact]
        public async Task RunOnceAsync_InvalidConfigurationDoesNotResolveDatabase()
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Maintenance:MessageLogRetention:Enabled"] = "true",
                    ["Maintenance:MessageLogRetention:BatchSize"] = "1001"
                })
                .Build();
            using var provider = new ServiceCollection().BuildServiceProvider();
            var service = CreateService(provider, configuration);

            var result = await service.RunOnceAsync(
                new System.DateTime(2026, 8, 15, 12, 0, 0, System.DateTimeKind.Utc),
                default);

            Assert.Equal(
                MessageLogRetentionSweepStatus.InvalidConfiguration,
                result.Status);
        }

        [Fact]
        public void ConfigureServices_RegistersOneRetentionServiceWithoutHangfire()
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:SQLite"] = "Filename=:memory:",
                    ["Stripe:UseMockServices"] = "true"
                })
                .Build();
            var services = new ServiceCollection();

            new Startup(configuration).ConfigureServices(services);

            Assert.Single(services.Where(descriptor =>
                descriptor.ServiceType == typeof(IHostedService) &&
                descriptor.ImplementationType == typeof(MessageLogRetentionService)));
            Assert.Single(services.Where(descriptor =>
                descriptor.ServiceType == typeof(MessageLogRetentionRunner)));
            Assert.DoesNotContain(services, descriptor =>
                descriptor.ServiceType.FullName != null &&
                descriptor.ServiceType.FullName.Contains("HangfireServer", System.StringComparison.Ordinal));
        }

        [Fact]
        public async Task RunOnceSafelyAsync_LogsBoundedContextWithoutExceptionDetails()
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Maintenance:MessageLogRetention:Enabled"] = "true",
                    ["Maintenance:MessageLogRetention:DryRun"] = "false",
                    ["Maintenance:MessageLogRetention:RetentionDays"] = "30",
                    ["Maintenance:MessageLogRetention:BatchSize"] = "10"
                })
                .Build();
            using var provider = new ServiceCollection().BuildServiceProvider();
            var logger = new RecordingLogger<MessageLogRetentionService>();
            var runner = new MessageLogRetentionRunner(
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<MessageLogRetentionRunner>.Instance);
            var service = new MessageLogRetentionService(runner, logger, configuration);

            MessageLogRetentionSweepResult? result = await service.RunOnceSafelyAsync(
                new System.DateTime(2026, 8, 15, 12, 0, 0, System.DateTimeKind.Utc),
                default);

            Assert.Null(result);
            var warning = Assert.Single(logger.Entries.Where(entry =>
                entry.Level == LogLevel.Warning));
            Assert.Contains("cutoff=2026-07-16", warning.Message);
            Assert.Contains("dryRun=False", warning.Message);
            Assert.Contains("batchSize=10", warning.Message);
            Assert.Contains("completedBatches=0", warning.Message);
            Assert.Contains("deleted=0", warning.Message);
            Assert.Contains("InvalidOperationException", warning.Message);
            Assert.DoesNotContain("No service for type", warning.Message);
            Assert.Null(warning.Exception);
        }

        private static MessageLogRetentionService CreateService(
            ServiceProvider provider,
            IConfiguration configuration)
        {
            var runner = new MessageLogRetentionRunner(
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<MessageLogRetentionRunner>.Instance);
            return new MessageLogRetentionService(
                runner,
                NullLogger<MessageLogRetentionService>.Instance,
                configuration);
        }
    }
}
