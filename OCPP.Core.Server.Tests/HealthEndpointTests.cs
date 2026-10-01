using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    public class HealthEndpointTests
    {
        [Fact]
        public void ServerStartup_RegistersHealthCheckService()
        {
            var services = new ServiceCollection();
            var configuration = BuildConfiguration();
            services.AddSingleton(configuration);

            var startup = new OCPP.Core.Server.Startup(configuration);
            startup.ConfigureServices(services);

            using var provider = services.BuildServiceProvider();
            Assert.NotNull(provider.GetService<HealthCheckService>());
        }

        [Fact]
        public void ManagementStartup_RegistersHealthCheckService()
        {
            var services = new ServiceCollection();
            var configuration = BuildConfiguration();
            services.AddSingleton(configuration);

            var startup = new OCPP.Core.Management.Startup(configuration);
            startup.ConfigureServices(services);

            using var provider = services.BuildServiceProvider();
            Assert.NotNull(provider.GetService<HealthCheckService>());
        }

        [Fact]
        public void ServerHealthEndpoints_MapLiveAndReadyRoutes()
        {
            var endpoints = CreateEndpointRouteBuilder();

            OCPP.Core.Server.HealthEndpointExtensions.MapOcppHealthChecks(endpoints);

            AssertHealthRoutes(endpoints);
        }

        [Fact]
        public void ManagementHealthEndpoints_MapLiveAndReadyRoutes()
        {
            var endpoints = CreateEndpointRouteBuilder();

            OCPP.Core.Management.HealthEndpointExtensions.MapOcppHealthChecks(endpoints);

            AssertHealthRoutes(endpoints);
        }

        [Fact]
        public async Task ServerHealthResponseWriter_ReturnsJsonStatus()
        {
            var result = await WriteServerHealthResponseAsync();

            Assert.Equal("application/json", result.ContentType);
            Assert.Contains("\"status\":\"Healthy\"", result.Body);
            Assert.Contains("\"durationMs\":", result.Body);
        }

        [Fact]
        public async Task ManagementHealthResponseWriter_ReturnsJsonStatus()
        {
            var result = await WriteManagementHealthResponseAsync();

            Assert.Equal("application/json", result.ContentType);
            Assert.Contains("\"status\":\"Healthy\"", result.Body);
            Assert.Contains("\"durationMs\":", result.Body);
        }

        private static async Task<HealthResponse> WriteServerHealthResponseAsync()
        {
            var context = CreateHttpContext();
            await OCPP.Core.Server.HealthEndpointExtensions.WriteHealthCheckResponseAsync(context, CreateHealthyReport());
            return await ReadHealthResponseAsync(context);
        }

        private static async Task<HealthResponse> WriteManagementHealthResponseAsync()
        {
            var context = CreateHttpContext();
            await OCPP.Core.Management.HealthEndpointExtensions.WriteHealthCheckResponseAsync(context, CreateHealthyReport());
            return await ReadHealthResponseAsync(context);
        }

        private static IConfiguration BuildConfiguration()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-health-config-{Guid.NewGuid():N}.sqlite");

            return new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:SqlServer"] = "",
                    ["ConnectionStrings:SQLite"] = $"Filename={databasePath};foreign keys=True",
                    ["ApiKey"] = "configured-api-key",
                    ["Stripe:UseMockServices"] = "true"
                })
                .Build();
        }

        private static TestEndpointRouteBuilder CreateEndpointRouteBuilder()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddRouting();
            services.AddHealthChecks();
            return new TestEndpointRouteBuilder(services.BuildServiceProvider());
        }

        private static void AssertHealthRoutes(TestEndpointRouteBuilder endpoints)
        {
            var patterns = endpoints.DataSources
                .SelectMany(dataSource => dataSource.Endpoints)
                .OfType<RouteEndpoint>()
                .Select(endpoint => endpoint.RoutePattern.RawText)
                .ToList();

            Assert.Contains("/health/live", patterns);
            Assert.Contains("/health/ready", patterns);
        }

        private static DefaultHttpContext CreateHttpContext()
        {
            return new DefaultHttpContext
            {
                Response =
                {
                    Body = new MemoryStream()
                }
            };
        }

        private static HealthReport CreateHealthyReport()
        {
            return new HealthReport(new Dictionary<string, HealthReportEntry>(), TimeSpan.FromMilliseconds(3));
        }

        private static async Task<HealthResponse> ReadHealthResponseAsync(DefaultHttpContext context)
        {
            context.Response.Body.Position = 0;
            using var reader = new StreamReader(context.Response.Body);
            return new HealthResponse(
                context.Response.ContentType,
                await reader.ReadToEndAsync());
        }

        private sealed class TestEndpointRouteBuilder : IEndpointRouteBuilder
        {
            public TestEndpointRouteBuilder(IServiceProvider serviceProvider)
            {
                ServiceProvider = serviceProvider;
            }

            public IServiceProvider ServiceProvider { get; }
            public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();
            public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
        }

        private sealed record HealthResponse(string? ContentType, string Body);
    }
}
