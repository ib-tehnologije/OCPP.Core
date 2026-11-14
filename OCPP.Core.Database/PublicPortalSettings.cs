using System;

namespace OCPP.Core.Database
{
    public class PublicPortalSettings
    {
        public int PublicPortalSettingsId { get; set; }
        public string BrandName { get; set; }
        public string Tagline { get; set; }
        public string SupportEmail { get; set; }
        public string SupportPhone { get; set; }
        public string HelpUrl { get; set; }
        public string FooterCompanyLine { get; set; }
        public string FooterAddressLine { get; set; }
        public string FooterLegalLine { get; set; }
        public string CanonicalBaseUrl { get; set; }
        public string SeoTitle { get; set; }
        public string SeoDescription { get; set; }
        public string HeaderLogoUrl { get; set; }
        public string FooterLogoUrl { get; set; }
        public bool? QrScannerEnabled { get; set; }
        public bool? LightThemeEnabled { get; set; }
        public bool? IdleFeeExcludedWindowEnabled { get; set; }
        public string IdleFeeExcludedWindow { get; set; }
        // Links to the operator's general terms and privacy policy pages (for example static pages under wwwroot).
        public string TermsUrl { get; set; }
        public string PrivacyUrl { get; set; }
        // Version label of the current general terms (for example their effective date), stored with each acceptance.
        public string TermsVersion { get; set; }
        // When true and terms exist, the start form requires the driver to accept them before paying.
        public bool? RequireTermsAcceptance { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
    }
}
