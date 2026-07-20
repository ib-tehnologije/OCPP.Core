using System;
using System.IO;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    public class PublicStatusInvoiceViewTests
    {
        [Fact]
        public void PublicStatusView_DoesNotOfferLateInvoiceBuyerEntry()
        {
            var view = ReadView();

            Assert.DoesNotContain("id=\"r1-submit\"", view);
            Assert.DoesNotContain("submitR1Details", view);
            Assert.DoesNotContain("submit company details now or later", view, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("requestR1InvoiceUrl", view);
            Assert.Contains("id=\"done-invoice-message\"", view);
            Assert.Contains("invoice.customerMessage", view);
            Assert.Contains("invoice.customerBuyerDataLocked", view);
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
