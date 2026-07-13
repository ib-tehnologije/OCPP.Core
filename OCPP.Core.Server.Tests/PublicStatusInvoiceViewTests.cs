using System;
using System.IO;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    public class PublicStatusInvoiceViewTests
    {
        [Fact]
        public void PublicStatusView_RequiresCountryAwareReviewAndExplicitConfirmation()
        {
            var view = ReadView();

            Assert.Contains("id=\"r1-country\"", view);
            Assert.Contains("id=\"r1-street\"", view);
            Assert.Contains("id=\"r1-postal-code\"", view);
            Assert.Contains("id=\"r1-city\"", view);
            Assert.Contains("id=\"r1-email\"", view);
            Assert.Contains("id=\"r1-tax-identifier\"", view);
            Assert.Contains("id=\"r1-registration-number\"", view);
            Assert.Contains("id=\"r1-vat-registration\"", view);
            Assert.Contains("id=\"r1-review\"", view);
            Assert.Contains("id=\"r1-confirm\"", view);
            Assert.Contains("buyerDataConfirmed: r1Confirm.checked", view);
        }

        private static string ReadView()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                var path = Path.Combine(directory.FullName, "OCPP.Core.Management", "Views", "Payments", "PublicStatus.cshtml");
                if (File.Exists(path)) return File.ReadAllText(path);
                directory = directory.Parent;
            }
            throw new FileNotFoundException("Could not locate Views/Payments/PublicStatus.cshtml.");
        }
    }
}
