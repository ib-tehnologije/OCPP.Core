using System;
using System.IO;
using Microsoft.Extensions.Configuration;
using OCPP.Core.Server.Payments;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    public class MockStripeServicesTests
    {
        private static IConfiguration CreateConfiguration(string directory) =>
            new ConfigurationBuilder()
                .AddInMemoryCollection(new System.Collections.Generic.Dictionary<string, string?>
                {
                    ["Stripe:MockDiagnosticsDirectory"] = directory
                })
                .Build();

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
                var store = new MockStripeStore(CreateConfiguration(directory));

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

        [Fact]
        public void MockStripeStore_FailsClosedAtomicallyForCaseCollidingMetadata()
        {
            string directory = Path.Combine(Path.GetTempPath(), $"mock-stripe-collision-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                File.WriteAllText(
                    Path.Combine(directory, "mock-stripe-store.json"),
                    """
                    {
                      "sessions": [
                        {
                          "id": "cs_valid_before_collision",
                          "paymentIntentId": "pi_valid_before_collision",
                          "metadata": { "reservation_id": "first" }
                        },
                        {
                          "id": "cs_case_collision",
                          "paymentIntentId": "pi_case_collision",
                          "metadata": {
                            "reservation_id": "lower",
                            "RESERVATION_ID": "upper"
                          }
                        }
                      ],
                      "paymentIntents": [
                        {
                          "id": "pi_valid_before_collision",
                          "metadata": { "reservation_id": "first" }
                        }
                      ]
                    }
                    """);
                MockStripeStore? store = null;

                var exception = Record.Exception(() => store = new MockStripeStore(CreateConfiguration(directory)));

                Assert.Null(exception);
                Assert.NotNull(store);
                Assert.Empty(store!.Sessions);
                Assert.Empty(store.PaymentIntents);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Theory]
        [InlineData("not-json")]
        [InlineData("{ \"sessions\": {}, \"paymentIntents\": [] }")]
        public void MockStripeStore_FailsClosedForInvalidOrMalformedSnapshot(string contents)
        {
            string directory = Path.Combine(Path.GetTempPath(), $"mock-stripe-invalid-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                File.WriteAllText(Path.Combine(directory, "mock-stripe-store.json"), contents);

                MockStripeStore? store = null;

                var exception = Record.Exception(() => store = new MockStripeStore(CreateConfiguration(directory)));

                Assert.Null(exception);
                Assert.NotNull(store);
                Assert.Empty(store!.Sessions);
                Assert.Empty(store.PaymentIntents);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Fact]
        public void MockStripeStore_LeavesStoreEmptyWhenSnapshotIsMissing()
        {
            string directory = Path.Combine(Path.GetTempPath(), $"mock-stripe-missing-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                var store = new MockStripeStore(CreateConfiguration(directory));

                Assert.Empty(store.Sessions);
                Assert.Empty(store.PaymentIntents);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Fact]
        public void MockStripeStore_LeavesStoreEmptyWhenDiagnosticsDirectoryIsUnreadable()
        {
            string diagnosticsPath = Path.GetTempFileName();
            try
            {
                MockStripeStore? store = null;

                var exception = Record.Exception(() => store = new MockStripeStore(CreateConfiguration(diagnosticsPath)));

                Assert.Null(exception);
                Assert.NotNull(store);
                Assert.Empty(store!.Sessions);
                Assert.Empty(store.PaymentIntents);
            }
            finally
            {
                File.Delete(diagnosticsPath);
            }
        }
    }
}
