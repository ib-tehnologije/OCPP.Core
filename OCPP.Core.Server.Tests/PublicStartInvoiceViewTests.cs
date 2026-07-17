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
            Assert.Contains("name=\"RememberInvoiceBuyer\"", view);
            Assert.Contains("Other users of a shared device may see these details.", view);
            Assert.Contains("invoice-buyer-storage.js", view);
            Assert.Contains("invoiceBuyerStorage.load", view);
            Assert.Contains("invoiceBuyerStorage.save", view);
            Assert.Contains("invoiceBuyerStorage.clear", view);
            Assert.Contains("data-i18n=\"start.rememberInvoiceBuyer\"", view);
            Assert.Contains("data-i18n=\"start.rememberInvoiceBuyerWarning\"", view);
            Assert.DoesNotContain("Buyer data is collected after checkout", view);
        }

        [Fact]
        public void PublicPortalTranslations_DescribePreCheckoutInvoiceConfirmation()
        {
            var script = ReadProjectFile("OCPP.Core.Management", "wwwroot", "js", "public-portal.js");

            Assert.DoesNotContain("After checkout, review and confirm", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Buyer data is collected after checkout", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("submit company details now or later", script, StringComparison.OrdinalIgnoreCase);
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
