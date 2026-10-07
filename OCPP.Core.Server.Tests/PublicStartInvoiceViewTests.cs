using System;
using System.IO;
using System.Linq;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    public class PublicStartInvoiceViewTests
    {
        [Fact]
        public void PublicStartView_CollectsOnlyR1EmailBeforeCheckout()
        {
            var view = ReadView();

            Assert.Contains("name=\"RequestR1Invoice\"", view);
            Assert.Contains("name=\"BuyerEmail\"", view);
            Assert.Contains("email.required = enabled", view);
            Assert.DoesNotContain("name=\"BuyerCountry\"", view);
            Assert.DoesNotContain("name=\"BuyerCompanyName\"", view);
            Assert.DoesNotContain("name=\"BuyerTaxIdentifier\"", view);
            Assert.DoesNotContain("name=\"BuyerDataConfirmed\"", view);
            Assert.DoesNotContain("RememberInvoiceBuyer", view);
            Assert.DoesNotContain("localStorage", view);

            var model = ReadProjectFile("OCPP.Core.Management", "Models", "PublicStartViewModel.cs");
            var controller = ReadProjectFile("OCPP.Core.Management", "Controllers", "PublicController.cs");
            Assert.DoesNotContain("RememberInvoiceBuyer", model);
            Assert.DoesNotContain("RememberInvoiceBuyer", controller);
        }

        [Fact]
        public void PublicPortalTranslations_DescribeR1DetailsDuringCharging()
        {
            var script = ReadProjectFile("OCPP.Core.Management", "wwwroot", "js", "public-portal.js");

            Assert.DoesNotContain("After checkout, review and confirm", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Buyer data is collected after checkout", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("submit company details now or later", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("start.rememberInvoiceBuyer", script, StringComparison.Ordinal);
            Assert.DoesNotContain("start.rememberInvoiceBuyerWarning", script, StringComparison.Ordinal);
            Assert.Contains("\"start.r1Email\"", script, StringComparison.Ordinal);
            Assert.Contains("\"status.r1.pendingPrompt\"", script, StringComparison.Ordinal);
            Assert.Contains("\"status.vat.invalid\"", script, StringComparison.Ordinal);
            Assert.Contains("\"status.vat.unavailable\"", script, StringComparison.Ordinal);
            Assert.DoesNotContain(
                "Foreign identifiers are issued exactly as entered and are not registry-verified.",
                script,
                StringComparison.Ordinal);
        }

        private static string ReadView()
        {
            return ReadProjectFile("OCPP.Core.Management", "Views", "Public", "Start.cshtml");
        }

        private static string ReadProjectFile(params string[] parts)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                var path = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
                if (File.Exists(path)) return File.ReadAllText(path);
                directory = directory.Parent;
            }
            throw new FileNotFoundException($"Could not locate {string.Join('/', parts)}.");
        }
    }
}
