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
        public void Resolve_DefaultsStartWindowToFiveMinutes()
        {
            using var context = CreateContext();

            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>())
                .Build();

            var resolved = PaymentFlowOptionsResolver.Resolve(configuration, context);

            Assert.Equal(5, resolved.StartWindowMinutes);
        }

        [Fact]
        public void Resolve_UsesConfiguredStartWindowMinutes()
        {
            using var context = CreateContext();

            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Payments:StartWindowMinutes"] = "10"
                })
                .Build();

            var resolved = PaymentFlowOptionsResolver.Resolve(configuration, context);

            Assert.Equal(10, resolved.StartWindowMinutes);
        }

        [Fact]
        public void Resolve_DefaultsMinimumSessionFeeToOneKwh()
        {
            using var context = CreateContext();

            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>())
                .Build();

            var resolved = PaymentFlowOptionsResolver.Resolve(configuration, context);

            Assert.Equal(1.0m, resolved.MinimumSessionFeeKwh);
        }

        [Fact]
        public void Resolve_DefaultsMinimumChargeAmountToFiftyCents()
        {
            using var context = CreateContext();

            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>())
                .Build();

            var resolved = PaymentFlowOptionsResolver.Resolve(configuration, context);

            Assert.Equal(50, resolved.MinimumChargeAmountCents);
        }

        [Fact]
        public void Resolve_UsesConfiguredMinimumChargeAmount()
        {
            using var context = CreateContext();

            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Payments:MinimumChargeAmountCents"] = "25"
                })
                .Build();

            var resolved = PaymentFlowOptionsResolver.Resolve(configuration, context);

            Assert.Equal(25, resolved.MinimumChargeAmountCents);
        }

        [Fact]
        public void Resolve_ClampsNegativeMinimumChargeAmountToZero()
        {
            using var context = CreateContext();

            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Payments:MinimumChargeAmountCents"] = "-1"
                })
                .Build();

            var resolved = PaymentFlowOptionsResolver.Resolve(configuration, context);

            Assert.Equal(0, resolved.MinimumChargeAmountCents);
        }

        [Fact]
        public void Resolve_ClampsNegativeMinimumSessionFeeToZero()
        {
            using var context = CreateContext();

            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Payments:MinimumSessionFeeKwh"] = "-1"
                })
                .Build();

            var resolved = PaymentFlowOptionsResolver.Resolve(configuration, context);

            Assert.Equal(0m, resolved.MinimumSessionFeeKwh);
        }

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
