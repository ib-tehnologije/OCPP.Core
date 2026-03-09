using System.ComponentModel.DataAnnotations;

namespace OCPP.Core.Management.Models
{
    public class PublicPortalSettingsEditViewModel
    {
        public int? PublicPortalSettingsId { get; set; }

        [Display(Name = "Brand name")]
        [StringLength(200)]
        public string BrandName { get; set; }

        [Display(Name = "Tagline")]
        [StringLength(300)]
        public string Tagline { get; set; }

        [Display(Name = "Support email")]
        [EmailAddress]
        [StringLength(200)]
        public string SupportEmail { get; set; }

        [Display(Name = "Support phone")]
        [StringLength(100)]
        public string SupportPhone { get; set; }

        [Display(Name = "Help URL")]
        [StringLength(500)]
        public string HelpUrl { get; set; }

        [Display(Name = "Footer company line")]
        [StringLength(300)]
        public string FooterCompanyLine { get; set; }

        [Display(Name = "Footer address line")]
        [StringLength(300)]
        public string FooterAddressLine { get; set; }

        [Display(Name = "Footer legal line")]
        [StringLength(300)]
        public string FooterLegalLine { get; set; }

        [Display(Name = "Canonical public base URL")]
        [StringLength(500)]
        public string CanonicalBaseUrl { get; set; }

        [Display(Name = "SEO title")]
        [StringLength(300)]
        public string SeoTitle { get; set; }

        [Display(Name = "SEO description")]
        [StringLength(500)]
        public string SeoDescription { get; set; }

        [Display(Name = "Header logo URL")]
        [StringLength(500)]
        public string HeaderLogoUrl { get; set; }

        [Display(Name = "Footer logo URL")]
        [StringLength(500)]
        public string FooterLogoUrl { get; set; }

        [Display(Name = "Enable QR scanner")]
        public bool QrScannerEnabled { get; set; } = true;
    }
}
