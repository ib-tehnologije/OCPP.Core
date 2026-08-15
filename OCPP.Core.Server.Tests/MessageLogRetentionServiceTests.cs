using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
