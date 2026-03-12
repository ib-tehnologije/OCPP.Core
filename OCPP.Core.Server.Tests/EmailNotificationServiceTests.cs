using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OCPP.Core.Database;
using OCPP.Core.Server.Payments;
using Stripe.Checkout;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    public class EmailNotificationServiceTests
    {
        [Fact]
        public void SendPaymentAuthorized_WritesSinkWithOpenSessionLink()
        {
            string sinkDirectory = Path.Combine(Path.GetTempPath(), $"ocpp-email-sink-{Guid.NewGuid():N}");

            try
            {
                var service = new EmailNotificationService(
                    Options.Create(new NotificationOptions
                    {
                        EnableCustomerEmails = true,
                        SinkDirectory = sinkDirectory
                    }),
                    NullLogger<EmailNotificationService>.Instance);

                var reservation = new ChargePaymentReservation
                {
                    ReservationId = Guid.NewGuid(),
                    ChargePointId = "CP-EMAIL",
                    ConnectorId = 1,
                    Currency = "eur",
                    MaxAmountCents = 4670
                };

                const string statusUrl = "https://example.test/Payments/Status?reservationId=abc&origin=public";
                service.SendPaymentAuthorized(
                    "driver@example.com",
                    reservation,
                    new Session { Id = "sess_email_sink" },
                    statusUrl);

                string sinkFile = Assert.Single(Directory.GetFiles(sinkDirectory, "*.json"));
                using var payload = JsonDocument.Parse(File.ReadAllText(sinkFile));
                var root = payload.RootElement;

                Assert.Equal("PaymentAuthorized", root.GetProperty("eventName").GetString());
                Assert.Equal("driver@example.com", root.GetProperty("toEmail").GetString());
                Assert.Equal("Open session", root.GetProperty("actionText").GetString());
                Assert.Equal(statusUrl, root.GetProperty("actionUrl").GetString());
                Assert.Contains("Payments/Status?reservationId=abc", root.GetProperty("htmlBody").GetString(), StringComparison.Ordinal);
            }
            finally
            {
                if (Directory.Exists(sinkDirectory))
                {
                    Directory.Delete(sinkDirectory, recursive: true);
                }
            }
        }
    }
}
