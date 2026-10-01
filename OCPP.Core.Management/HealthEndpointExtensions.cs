using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Newtonsoft.Json;
using OCPP.Core.Database;

namespace OCPP.Core.Management
{
    public static class HealthEndpointExtensions
    {
        private const string ReadyTag = "ready";

        public static IServiceCollection AddOcppHealthChecks(this IServiceCollection services)
        {
            services.AddHealthChecks()
                .AddCheck<OcppDatabaseHealthCheck>("database", tags: new[] { ReadyTag });

            return services;
        }

        public static IEndpointRouteBuilder MapOcppHealthChecks(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapHealthChecks("/health/live", new HealthCheckOptions
            {
                Predicate = _ => false,
                ResponseWriter = WriteHealthCheckResponseAsync
            });

            endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions
            {
                Predicate = registration => registration.Tags.Contains(ReadyTag),
                ResponseWriter = WriteHealthCheckResponseAsync
            });

            return endpoints;
        }

        public static Task WriteHealthCheckResponseAsync(HttpContext context, HealthReport report)
        {
            context.Response.ContentType = "application/json";
            var payload = new
            {
                status = report.Status.ToString(),
                durationMs = report.TotalDuration.TotalMilliseconds,
                checks = report.Entries.Select(entry => new
                {
                    name = entry.Key,
                    status = entry.Value.Status.ToString(),
                    description = entry.Value.Description,
                    durationMs = entry.Value.Duration.TotalMilliseconds
                })
            };

            return context.Response.WriteAsync(JsonConvert.SerializeObject(payload));
        }

        private sealed class OcppDatabaseHealthCheck : IHealthCheck
        {
            private readonly OCPPCoreContext _dbContext;

            public OcppDatabaseHealthCheck(OCPPCoreContext dbContext)
            {
                _dbContext = dbContext;
            }

            public async Task<HealthCheckResult> CheckHealthAsync(
                HealthCheckContext context,
                CancellationToken cancellationToken = default)
            {
                try
                {
                    bool canConnect = await _dbContext.Database.CanConnectAsync(cancellationToken);
                    return canConnect
                        ? HealthCheckResult.Healthy("Database connection available.")
                        : HealthCheckResult.Unhealthy("Database connection unavailable.");
                }
                catch (Exception exp)
                {
                    return HealthCheckResult.Unhealthy("Database connection check failed.", exp);
                }
            }
        }
    }
}
