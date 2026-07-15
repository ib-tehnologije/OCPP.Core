using System;
using System.IO;
using Microsoft.Extensions.Configuration;
using OCPP.Core.Server.Payments;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    public class MockStripeServicesTests
    {
        [Fact]
        public void MockStripeStore_RestoresValidDiagnosticsSnapshot()
        {
            string directory = Path.Combine(Path.GetTempPath(), $"mock-stripe-restore-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                File.WriteAllText(
                    Path.Combine(directory, "mock-stripe-store.json"),
                    """
                    {
                      "generatedAtUtc": "2026-07-15T10:00:00.000Z",
                      "sessions": [
                        {
                          "id": "cs_test_seeded",
                          "url": "http://127.0.0.1/mock-checkout",
                          "paymentIntentId": "pi_test_seeded",
                          "status": "complete",
                          "paymentStatus": "paid",
                          "metadata": { "reservation_id": "10000000-0000-4000-8000-000000000001" }
                        }
                      ],
                      "paymentIntents": [
                        {
                          "id": "pi_test_seeded",
                          "status": "requires_capture",
                          "amount": 1000,
                          "amountReceived": 0,
                          "metadata": { "reservation_id": "10000000-0000-4000-8000-000000000001" }
                        }
                      ]
                    }
                    """);
                IConfiguration configuration = new ConfigurationBuilder()
                    .AddInMemoryCollection(new System.Collections.Generic.Dictionary<string, string?>
                    {
                        ["Stripe:MockDiagnosticsDirectory"] = directory
                    })
                    .Build();

                var store = new MockStripeStore(configuration);

                Assert.True(store.Sessions.TryGetValue("cs_test_seeded", out var session));
                Assert.Equal("pi_test_seeded", session.PaymentIntentId);
                Assert.Equal("10000000-0000-4000-8000-000000000001", session.Metadata["reservation_id"]);
                Assert.True(store.PaymentIntents.TryGetValue("pi_test_seeded", out var paymentIntent));
                Assert.Equal(1000, paymentIntent.Amount);
                Assert.Equal("10000000-0000-4000-8000-000000000001", paymentIntent.Metadata["reservation_id"]);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
