using System;
using System.IO;
using System.Linq;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    public class PublicStartInvoiceViewTests
    {
        [Fact]
        public void PublicStartView_CollectsCompleteConfirmedBuyerBeforeCheckout()
        {
            var view = ReadView();

            Assert.Contains("name=\"BuyerCountry\"", view);
            Assert.Contains("name=\"BuyerCompanyName\"", view);
            Assert.Contains("name=\"BuyerStreet\"", view);
            Assert.Contains("name=\"BuyerPostalCode\"", view);
            Assert.Contains("name=\"BuyerCity\"", view);
            Assert.Contains("name=\"BuyerEmail\"", view);
            Assert.Contains("name=\"BuyerTaxIdentifier\"", view);
            Assert.Contains("name=\"BuyerRegistrationNumber\"", view);
            Assert.Contains("name=\"BuyerIdentifierIsVatRegistration\"", view);
            Assert.Contains("name=\"BuyerDataConfirmed\"", view);
            Assert.DoesNotContain("RememberInvoiceBuyer", view);
            Assert.DoesNotContain("rememberInvoiceBuyer", view);
            Assert.DoesNotContain("invoiceBuyerStorage", view);
            Assert.DoesNotContain("invoice-buyer-storage.js", view);
            Assert.DoesNotContain("localStorage", view);
            Assert.Contains("i.required = enabled", view);
            Assert.DoesNotContain("country?.value !== 'HR'", view);
            Assert.Contains("const invalidateConfirmation", view);
            Assert.Contains("confirmation.checked = false", view);
            Assert.DoesNotContain("Buyer data is collected after checkout", view);

            var model = ReadProjectFile("OCPP.Core.Management", "Models", "PublicStartViewModel.cs");
            var controller = ReadProjectFile("OCPP.Core.Management", "Controllers", "PublicController.cs");
            Assert.DoesNotContain("RememberInvoiceBuyer", model);
            Assert.DoesNotContain("RememberInvoiceBuyer", controller);
        }

        [Fact]
        public void PublicPortalTranslations_DescribePreCheckoutInvoiceConfirmation()
        {
            var script = ReadProjectFile("OCPP.Core.Management", "wwwroot", "js", "public-portal.js");

            Assert.DoesNotContain("After checkout, review and confirm", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Buyer data is collected after checkout", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("submit company details now or later", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("start.rememberInvoiceBuyer", script, StringComparison.Ordinal);
            Assert.DoesNotContain("start.rememberInvoiceBuyerWarning", script, StringComparison.Ordinal);
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
