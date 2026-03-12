using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using OCPP.Core.Database;
using OCPP.Core.Server.Payments;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    public class PaymentFlowOptionsResolverTests
    {
        [Fact]
        public void Resolve_UsesDatabaseQuietHoursWhenEnabled()
        {
            using var context = CreateContext();
            context.PublicPortalSettings.Add(new PublicPortalSettings
            {
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-10),
                UpdatedAtUtc = DateTime.UtcNow,
                IdleFeeExcludedWindowEnabled = true,
                IdleFeeExcludedWindow = "20:00-08:00"
            });
            context.SaveChanges();

            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Payments:IdleFeeExcludedWindow"] = "22:00-06:00",
                    ["Payments:IdleFeeExcludedTimeZoneId"] = "Europe/Zagreb"
                })
                .Build();

            var resolved = PaymentFlowOptionsResolver.Resolve(configuration, context, new PaymentFlowOptions());

            Assert.Equal("20:00-08:00", resolved.IdleFeeExcludedWindow);
            Assert.Equal("Europe/Zagreb", resolved.IdleFeeExcludedTimeZoneId);
        }

        [Fact]
        public void Resolve_DisablesConfigQuietHoursWhenDatabaseFlagIsFalse()
        {
            using var context = CreateContext();
            context.PublicPortalSettings.Add(new PublicPortalSettings
            {
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-10),
                UpdatedAtUtc = DateTime.UtcNow,
                IdleFeeExcludedWindowEnabled = false,
                IdleFeeExcludedWindow = null
            });
            context.SaveChanges();

            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Payments:IdleFeeExcludedWindow"] = "22:00-06:00"
                })
                .Build();

            var resolved = PaymentFlowOptionsResolver.Resolve(configuration, context, new PaymentFlowOptions());

            Assert.Null(resolved.IdleFeeExcludedWindow);
            Assert.Equal("Europe/Zagreb", resolved.IdleFeeExcludedTimeZoneId);
        }

        private static OCPPCoreContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<OCPPCoreContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            return new OCPPCoreContext(options);
        }
    }
}
