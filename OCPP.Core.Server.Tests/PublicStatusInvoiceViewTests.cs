using System;
using System.IO;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    public class PublicStatusInvoiceViewTests
    {
        [Fact]
        public void PublicStatusView_OffersPrefilledBuyerEditingAndIssuedInvoiceLock()
        {
            var view = ReadView();

            Assert.Contains("id=\"r1-edit-section\"", view);
            Assert.Contains("id=\"buyerCompanyName\"", view);
            Assert.Contains("id=\"buyerTaxIdentifier\"", view);
            Assert.Contains("id=\"buyerDataVersion\"", view);
            Assert.Contains("id=\"r1-submit\"", view);
            Assert.Contains("submitR1Details", view);
            Assert.Contains("requestR1InvoiceUrl", view);
            Assert.Contains("data?.invoiceBuyer", view);
            Assert.Contains("buyer.editable", view);
            Assert.Contains("id=\"done-invoice-message\"", view);
            Assert.Contains("invoice.customerMessage", view);
            Assert.Contains("invoice.customerBuyerDataLocked", view);
            Assert.Contains("statusRequestGeneration", view);
            Assert.Contains("generation !== statusRequestGeneration", view);
            Assert.Contains("id=\"r1-edit-support\"", view);
            Assert.Contains("status.r1.savedMetadataPending", view);
        }

        [Fact]
        public void PublicStatusView_UsesServerProvidedStartDeadlineForCountdown()
        {
            var view = ReadView();

            Assert.Contains("data?.startDeadlineAtUtc", view);
            Assert.Contains("deadline.getTime() - Date.now()", view);
            Assert.Contains("status.hint.awaitingPlugTimed", view);
            Assert.DoesNotContain("5 minutes", view, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("5-minute", view, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void PublicStatusView_RendersNonBlockingViesOutcomes()
        {
            var view = ReadView();

            Assert.Contains("id=\"vat-verification-alert\"", view);
            Assert.Contains("id=\"vat-verification-alert-text\"", view);
            Assert.Contains("data?.invoiceBuyerVatVerificationStatus", view);
            Assert.Contains("status.vat.invalid", view);
            Assert.Contains("status.vat.unavailable", view);
            Assert.Contains("vatVerificationAlert.className = \"alert-box warning public-cap-alert\"", view);
            Assert.Contains("vatVerificationAlert.className = \"alert-box info public-cap-alert\"", view);
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
