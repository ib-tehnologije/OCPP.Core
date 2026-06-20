using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using OCPP.Core.Database;
using OCPP.Core.Management.Services;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    public class PublicPortalSettingsResolverTests
    {
        [Fact]
        public void Resolve_UsesDatabaseValuesBeforeConfigAndDefaults()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"portal-resolver-{Guid.NewGuid():N}.sqlite");

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    setupContext.PublicPortalSettings.Add(new PublicPortalSettings
                    {
                        CreatedAtUtc = DateTime.UtcNow.AddMinutes(-10),
                        UpdatedAtUtc = DateTime.UtcNow,
                        BrandName = "  DB Brand  ",
                        SupportPhone = "  +385123  ",
                        CanonicalBaseUrl = " https://db.example.test/ ",
                        QrScannerEnabled = false,
                        IdleFeeExcludedWindowEnabled = true,
                        IdleFeeExcludedWindow = "20:00-08:00"
                    });
                    setupContext.SaveChanges();
                }

                using var actionContext = CreateContext(databasePath);
                IConfiguration configuration = new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["PublicPortal:BrandName"] = "Config Brand",
                        ["PublicPortal:Tagline"] = "Config tagline",
                        ["PublicPortal:SupportEmail"] = "portal@example.test",
                        ["PublicPortal:SeoDescription"] = "Config SEO",
                        ["PublicPortal:QrScannerEnabled"] = "true",
                        ["Email:ReplyToAddress"] = "reply@example.test"
                    })
                    .Build();

                var resolver = new PublicPortalSettingsResolver(configuration, actionContext);

                var resolved = resolver.Resolve();
                var editor = resolver.ResolveForEditor();

                Assert.Equal("DB Brand", resolved.BrandName);
                Assert.Equal("Config tagline", resolved.Tagline);
                Assert.Equal("portal@example.test", resolved.SupportEmail);
                Assert.Equal("+385123", resolved.SupportPhone);
                Assert.Equal("DB Brand", resolved.FooterCompanyLine);
                Assert.Equal("Config tagline", resolved.FooterLegalLine);
                Assert.Equal("Config SEO", resolved.SeoDescription);
                Assert.Equal("https://db.example.test", resolved.CanonicalBaseUrl);
                Assert.False(resolved.QrScannerEnabled);
                Assert.True(editor.IdleFeeExcludedWindowEnabled);
                Assert.Equal("20:00-08:00", editor.IdleFeeExcludedWindow);

                Assert.NotNull(editor.PublicPortalSettingsId);
                Assert.Equal("DB Brand", editor.BrandName);
                Assert.Equal("Config tagline", editor.Tagline);
                Assert.Equal("portal@example.test", editor.SupportEmail);
                Assert.Equal("https://db.example.test", editor.CanonicalBaseUrl);
                Assert.False(editor.QrScannerEnabled);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Fact]
        public void Resolve_UsesConfigAndEmailFallbacksWhenDatabaseIsEmpty()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"portal-resolver-empty-{Guid.NewGuid():N}.sqlite");

            try
            {
                using var context = CreateContext(databasePath);
                IConfiguration configuration = new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["PublicPortal:BrandName"] = "Config Brand",
                        ["PublicPortal:Tagline"] = "Config tagline",
                        ["Email:ReplyToAddress"] = "reply@example.test",
                        ["Payments:IdleFeeExcludedWindow"] = "22:00-06:00"
                    })
                    .Build();

                var resolver = new PublicPortalSettingsResolver(configuration, context);

                var resolved = resolver.Resolve();

                Assert.Equal("Config Brand", resolved.BrandName);
                Assert.Equal("Config tagline", resolved.Tagline);
                Assert.Equal("reply@example.test", resolved.SupportEmail);
                Assert.Equal("Config Brand", resolved.FooterCompanyLine);
                Assert.Equal("Config tagline", resolved.FooterLegalLine);
                Assert.True(resolved.QrScannerEnabled);

                var editor = resolver.ResolveForEditor();
                Assert.True(editor.IdleFeeExcludedWindowEnabled);
                Assert.Equal("22:00-06:00", editor.IdleFeeExcludedWindow);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        private static OCPPCoreContext CreateContext(string databasePath)
        {
            var options = new DbContextOptionsBuilder<OCPPCoreContext>()
                .UseSqlite($"Data Source={databasePath}")
                .Options;

            var context = new OCPPCoreContext(options);
            context.Database.EnsureCreated();
            return context;
        }

        private static void TryDelete(string databasePath)
        {
            try
            {
                if (File.Exists(databasePath))
                {
                    File.Delete(databasePath);
                }
            }
            catch
            {
                // best effort cleanup for temp DBs
            }
        }
    }
}
