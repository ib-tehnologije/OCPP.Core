using System;
using System.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OCPP.Core.Database;
using OCPP.Core.Management.Models;

namespace OCPP.Core.Management.Controllers
{
    public partial class HomeController : BaseController
    {
        [Authorize]
        [HttpGet]
        public IActionResult PublicPortal()
        {
            if (User == null || !User.IsInRole(Constants.AdminRoleName))
            {
                TempData["ErrMsgKey"] = "AccessDenied";
                return RedirectToAction("Error", new { Id = string.Empty });
            }

            return View("PublicPortal", _publicPortalSettingsResolver.ResolveForEditor());
        }

        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult PublicPortal(PublicPortalSettingsEditViewModel model)
        {
            if (User == null || !User.IsInRole(Constants.AdminRoleName))
            {
                TempData["ErrMsgKey"] = "AccessDenied";
                return RedirectToAction("Error", new { Id = string.Empty });
            }

            if (!ModelState.IsValid)
            {
                return View("PublicPortal", model);
            }

            var normalizedIdleWindow = NormalizeIdleWindow(model.IdleFeeExcludedWindow);
            if (model.IdleFeeExcludedWindowEnabled && string.IsNullOrWhiteSpace(normalizedIdleWindow))
            {
                ModelState.AddModelError(nameof(model.IdleFeeExcludedWindow), "Enter a non-billing idle window in HH:mm-HH:mm format.");
                return View("PublicPortal", model);
            }

            var entity = DbContext.PublicPortalSettings
                .OrderByDescending(x => x.UpdatedAtUtc)
                .FirstOrDefault();

            if (entity == null)
            {
                entity = new PublicPortalSettings
                {
                    CreatedAtUtc = DateTime.UtcNow
                };
                DbContext.PublicPortalSettings.Add(entity);
            }

            entity.BrandName = Normalize(model.BrandName);
            entity.Tagline = Normalize(model.Tagline);
            entity.SupportEmail = Normalize(model.SupportEmail);
            entity.SupportPhone = Normalize(model.SupportPhone);
            entity.HelpUrl = NormalizePublicLinkUrl(model.HelpUrl);
            entity.FooterCompanyLine = Normalize(model.FooterCompanyLine);
            entity.FooterAddressLine = Normalize(model.FooterAddressLine);
            entity.FooterLegalLine = Normalize(model.FooterLegalLine);
            entity.CanonicalBaseUrl = NormalizeUrl(model.CanonicalBaseUrl);
            entity.SeoTitle = Normalize(model.SeoTitle);
            entity.SeoDescription = Normalize(model.SeoDescription);
            entity.HeaderLogoUrl = NormalizeUrl(model.HeaderLogoUrl);
            entity.FooterLogoUrl = NormalizeUrl(model.FooterLogoUrl);
            entity.QrScannerEnabled = model.QrScannerEnabled;
            entity.IdleFeeExcludedWindowEnabled = model.IdleFeeExcludedWindowEnabled;
            entity.IdleFeeExcludedWindow = model.IdleFeeExcludedWindowEnabled ? normalizedIdleWindow : null;
            entity.UpdatedAtUtc = DateTime.UtcNow;

            DbContext.SaveChanges();
            TempData["InfoMessage"] = "Public portal settings were saved successfully.";

            return RedirectToAction(nameof(PublicPortal));
        }

        private static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static string NormalizeUrl(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static string NormalizePublicLinkUrl(string value)
        {
            var normalized = NormalizeUrl(value);
            if (normalized == null)
            {
                return normalized;
            }

            if (normalized.StartsWith("//", StringComparison.Ordinal))
            {
                return $"https:{normalized}";
            }

            if (normalized.StartsWith("/", StringComparison.Ordinal) ||
                normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
            {
                return normalized;
            }

            return $"https://{normalized}";
        }

        private static string NormalizeIdleWindow(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var parts = value.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 2 ||
                !TimeSpan.TryParse(parts[0], out var start) ||
                !TimeSpan.TryParse(parts[1], out var end))
            {
                return null;
            }

            return $"{start:hh\\:mm}-{end:hh\\:mm}";
        }
    }
}
