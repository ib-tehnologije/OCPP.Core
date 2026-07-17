using System;
using System.IO;
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
            Assert.DoesNotContain("Buyer data is collected after checkout", view);
        }

        private static string ReadView()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                var path = Path.Combine(directory.FullName, "OCPP.Core.Management", "Views", "Public", "Start.cshtml");
                if (File.Exists(path)) return File.ReadAllText(path);
                directory = directory.Parent;
            }
            throw new FileNotFoundException("Could not locate Views/Public/Start.cshtml.");
        }
    }
}
