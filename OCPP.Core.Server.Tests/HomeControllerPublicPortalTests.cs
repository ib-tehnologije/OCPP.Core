using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using OCPP.Core.Database;
using OCPP.Core.Management;
using OCPP.Core.Management.Controllers;
using OCPP.Core.Management.Models;
using OCPP.Core.Management.Services;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    public class HomeControllerPublicPortalTests
    {
        [Fact]
        public void PublicPortal_Post_SavesTrimmedValuesAndRedirects()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"portal-save-{Guid.NewGuid():N}.sqlite");

            try
            {
                using var context = CreateContext(databasePath);
                var controller = CreateController(context);

                var result = controller.PublicPortal(new PublicPortalSettingsEditViewModel
                {
                    BrandName = "  Tehnoline.EV  ",
                    Tagline = "  Fast charging  ",
                    SupportEmail = "support@example.test",
                    SupportPhone = "  +385 52 355 050  ",
                    HelpUrl = " https://help.example.test/start ",
                    FooterCompanyLine = "  Footer Co  ",
                    FooterAddressLine = "  Pula  ",
                    FooterLegalLine = "  All rights reserved  ",
                    CanonicalBaseUrl = " https://ev.example.test/ ",
                    SeoTitle = "  SEO title  ",
                    SeoDescription = "  SEO desc  ",
                    HeaderLogoUrl = " https://cdn.example.test/header.png ",
                    FooterLogoUrl = " https://cdn.example.test/footer.png ",
                    QrScannerEnabled = false
                });

                var redirect = Assert.IsType<RedirectToActionResult>(result);
                Assert.Equal(nameof(HomeController.PublicPortal), redirect.ActionName);

                var saved = Assert.Single(context.PublicPortalSettings);
                Assert.Equal("Tehnoline.EV", saved.BrandName);
                Assert.Equal("Fast charging", saved.Tagline);
                Assert.Equal("+385 52 355 050", saved.SupportPhone);
                Assert.Equal("https://help.example.test/start", saved.HelpUrl);
                Assert.Equal("Footer Co", saved.FooterCompanyLine);
                Assert.Equal("Pula", saved.FooterAddressLine);
                Assert.Equal("All rights reserved", saved.FooterLegalLine);
                Assert.Equal("https://ev.example.test/", saved.CanonicalBaseUrl);
                Assert.Equal("SEO title", saved.SeoTitle);
                Assert.Equal("SEO desc", saved.SeoDescription);
                Assert.Equal("https://cdn.example.test/header.png", saved.HeaderLogoUrl);
                Assert.Equal("https://cdn.example.test/footer.png", saved.FooterLogoUrl);
                Assert.False(saved.QrScannerEnabled);
                Assert.Equal("Public portal settings were saved successfully.", controller.TempData["InfoMessage"]);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Fact]
        public void PublicPortal_Post_UpdatesExistingSettingsWithoutCreatingDuplicateRow()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"portal-update-{Guid.NewGuid():N}.sqlite");

            try
            {
                int settingsId;
                using (var setupContext = CreateContext(databasePath))
                {
                    var entity = new PublicPortalSettings
                    {
                        CreatedAtUtc = DateTime.UtcNow.AddHours(-1),
                        UpdatedAtUtc = DateTime.UtcNow.AddHours(-1),
                        BrandName = "Initial",
                        QrScannerEnabled = true
                    };
                    setupContext.PublicPortalSettings.Add(entity);
                    setupContext.SaveChanges();
                    settingsId = entity.PublicPortalSettingsId;
                }

                using var context = CreateContext(databasePath);
                var controller = CreateController(context);

                var result = controller.PublicPortal(new PublicPortalSettingsEditViewModel
                {
                    BrandName = "Updated brand",
                    SeoTitle = "Updated SEO",
                    QrScannerEnabled = true
                });

                Assert.IsType<RedirectToActionResult>(result);
                Assert.Single(context.PublicPortalSettings);

                var saved = context.PublicPortalSettings.Single();
                Assert.Equal(settingsId, saved.PublicPortalSettingsId);
                Assert.Equal("Updated brand", saved.BrandName);
                Assert.Equal("Updated SEO", saved.SeoTitle);
                Assert.True(saved.QrScannerEnabled);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        private static HomeController CreateController(OCPPCoreContext dbContext)
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>())
                .Build();

            var resolver = new PublicPortalSettingsResolver(configuration, dbContext);
            var controller = new HomeController(
                null!,
                new TestStringLocalizer<HomeController>(),
                resolver,
                null!,
                NullLoggerFactory.Instance,
                configuration,
                dbContext)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = CreateAdminHttpContext()
                },
                TempData = new TempDataDictionary(CreateAdminHttpContext(), new TestTempDataProvider())
            };

            return controller;
        }

        private static HttpContext CreateAdminHttpContext()
        {
            var context = new DefaultHttpContext();
            context.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, "admin"),
                new Claim(ClaimTypes.Role, Constants.AdminRoleName)
            }, "TestAuth"));
            return context;
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

        private sealed class TestStringLocalizer<T> : IStringLocalizer<T>
        {
            public LocalizedString this[string name] => new LocalizedString(name, name);

            public LocalizedString this[string name, params object[] arguments] =>
                new LocalizedString(name, string.Format(name, arguments));

            public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) =>
                Array.Empty<LocalizedString>();

            public IStringLocalizer WithCulture(System.Globalization.CultureInfo culture) => this;
        }

        private sealed class TestTempDataProvider : ITempDataProvider
        {
            private Dictionary<string, object> _values = new Dictionary<string, object>();

            public IDictionary<string, object> LoadTempData(HttpContext context) =>
                new Dictionary<string, object>(_values);

            public void SaveTempData(HttpContext context, IDictionary<string, object> values)
            {
                _values = values?.ToDictionary(x => x.Key, x => x.Value) ?? new Dictionary<string, object>();
            }
        }
    }
}
