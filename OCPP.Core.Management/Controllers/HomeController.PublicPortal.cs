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
            entity.HelpUrl = Normalize(model.HelpUrl);
            entity.FooterCompanyLine = Normalize(model.FooterCompanyLine);
            entity.FooterAddressLine = Normalize(model.FooterAddressLine);
            entity.FooterLegalLine = Normalize(model.FooterLegalLine);
            entity.CanonicalBaseUrl = NormalizeUrl(model.CanonicalBaseUrl);
            entity.SeoTitle = Normalize(model.SeoTitle);
            entity.SeoDescription = Normalize(model.SeoDescription);
            entity.HeaderLogoUrl = NormalizeUrl(model.HeaderLogoUrl);
            entity.FooterLogoUrl = NormalizeUrl(model.FooterLogoUrl);
            entity.QrScannerEnabled = model.QrScannerEnabled;
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
    }
}
