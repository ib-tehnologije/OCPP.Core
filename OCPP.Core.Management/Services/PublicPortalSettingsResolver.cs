using System;
using System.Linq;
using Microsoft.Extensions.Configuration;
using OCPP.Core.Database;
using OCPP.Core.Management.Models;

namespace OCPP.Core.Management.Services
{
    public interface IPublicPortalSettingsResolver
    {
        PublicPortalViewModel Resolve();
        PublicPortalSettingsEditViewModel ResolveForEditor();
    }

    public class PublicPortalSettingsResolver : IPublicPortalSettingsResolver
    {
        private readonly IConfiguration _configuration;
        private readonly OCPPCoreContext _dbContext;

        public PublicPortalSettingsResolver(IConfiguration configuration, OCPPCoreContext dbContext)
        {
            _configuration = configuration;
            _dbContext = dbContext;
        }

        public PublicPortalViewModel Resolve()
        {
            var dbSettings = _dbContext.PublicPortalSettings
                .OrderByDescending(x => x.UpdatedAtUtc)
                .FirstOrDefault();

            var brandName = Pick(dbSettings?.BrandName, "PublicPortal:BrandName", "OCPP Core");
            var tagline = Pick(dbSettings?.Tagline, "PublicPortal:Tagline", "Use fast chargers with clear pricing and instant session start.");
            var supportEmail = Pick(dbSettings?.SupportEmail, "PublicPortal:SupportEmail", _configuration["Email:ReplyToAddress"], _configuration["Email:FromAddress"], "support@example.com");

            return new PublicPortalViewModel
            {
                BrandName = brandName,
                Tagline = tagline,
                SupportEmail = supportEmail,
                SupportPhone = Pick(dbSettings?.SupportPhone, "PublicPortal:SupportPhone", string.Empty),
                HelpUrl = Pick(dbSettings?.HelpUrl, "PublicPortal:HelpUrl", string.Empty),
                FooterCompanyLine = Pick(dbSettings?.FooterCompanyLine, "PublicPortal:FooterCompanyLine", brandName),
                FooterAddressLine = Pick(dbSettings?.FooterAddressLine, "PublicPortal:FooterAddressLine", string.Empty),
                FooterLegalLine = Pick(dbSettings?.FooterLegalLine, "PublicPortal:FooterLegalLine", tagline),
                CanonicalBaseUrl = NormalizeBaseUrl(Pick(dbSettings?.CanonicalBaseUrl, "PublicPortal:CanonicalBaseUrl", string.Empty)),
                SeoTitle = Pick(dbSettings?.SeoTitle, "PublicPortal:SeoTitle", brandName),
                SeoDescription = Pick(dbSettings?.SeoDescription, "PublicPortal:SeoDescription", tagline),
                HeaderLogoUrl = Pick(dbSettings?.HeaderLogoUrl, "PublicPortal:HeaderLogoUrl", string.Empty),
                FooterLogoUrl = Pick(dbSettings?.FooterLogoUrl, "PublicPortal:FooterLogoUrl", string.Empty),
                QrScannerEnabled = dbSettings?.QrScannerEnabled ?? _configuration.GetValue<bool?>("PublicPortal:QrScannerEnabled") ?? true
            };
        }

        public PublicPortalSettingsEditViewModel ResolveForEditor()
        {
            var resolved = Resolve();
            var dbSettings = _dbContext.PublicPortalSettings
                .OrderByDescending(x => x.UpdatedAtUtc)
                .FirstOrDefault();

            return new PublicPortalSettingsEditViewModel
            {
                PublicPortalSettingsId = dbSettings?.PublicPortalSettingsId,
                BrandName = resolved.BrandName,
                Tagline = resolved.Tagline,
                SupportEmail = resolved.SupportEmail,
                SupportPhone = resolved.SupportPhone,
                HelpUrl = resolved.HelpUrl,
                FooterCompanyLine = resolved.FooterCompanyLine,
                FooterAddressLine = resolved.FooterAddressLine,
                FooterLegalLine = resolved.FooterLegalLine,
                CanonicalBaseUrl = resolved.CanonicalBaseUrl,
                SeoTitle = resolved.SeoTitle,
                SeoDescription = resolved.SeoDescription,
                HeaderLogoUrl = resolved.HeaderLogoUrl,
                FooterLogoUrl = resolved.FooterLogoUrl,
                QrScannerEnabled = resolved.QrScannerEnabled
            };
        }

        private string Pick(string primaryValue, params string[] fallbacks)
        {
            if (!string.IsNullOrWhiteSpace(primaryValue))
            {
                return primaryValue.Trim();
            }

            foreach (var fallback in fallbacks ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(fallback))
                {
                    continue;
                }

                if (fallback.Contains(':'))
                {
                    var configValue = _configuration[fallback];
                    if (!string.IsNullOrWhiteSpace(configValue))
                    {
                        return configValue.Trim();
                    }
                }
                else
                {
                    return fallback.Trim();
                }
            }

            return string.Empty;
        }

        private static string NormalizeBaseUrl(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().TrimEnd('/');
        }
    }
}
