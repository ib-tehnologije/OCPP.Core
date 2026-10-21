using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OCPP.Core.Database;
using OCPP.Core.Server.Payments;
using OCPP.Core.Server.Payments.Providers;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    /// <summary>
    /// End-to-end payment lifecycle through the real dependency injection of the server
    /// (<see cref="Startup.ConfigureServices"/>): the real <see cref="PaymentCoordinator"/>, the real gateways and the
    /// in-process PSP mocks (<c>Saferpay:UseMockServices</c>, <c>Stripe:UseMockServices</c>) over a SQLite database.
    /// </summary>
    public class PaymentProviderIntegrationTests
    {
        private const string ReturnBaseUrl = "https://return.example.com";
        private const string ChargePointId = "CP-INTEGRATION";
        private const string ChargeTagId = "TAG-INTEGRATION";
        private const string MockPayerEmail = "integration-payer@example.com";

        [Fact]
        public async Task Saferpay_CreateConfirmStartAndPartialCapture_ThroughRealServices()
        {
            using var stack = new ServerStack(new Dictionary<string, string?>
            {
                ["Payments:Provider"] = "Saferpay",
                ["Payments:ReturnBaseUrl"] = ReturnBaseUrl,
                ["Saferpay:Enabled"] = "true",
                ["Saferpay:UseMockServices"] = "true",
                ["Saferpay:MockPayerEmail"] = MockPayerEmail
            });

            var coordinator = stack.Coordinator;
            Assert.IsType<PaymentCoordinator>(coordinator);
            Assert.True(coordinator.IsEnabled);
            Assert.Equal(PaymentProviderNames.Saferpay, stack.Registry.Active.ProviderName);

            var (reservationId, capturedAuthorization) = await RunLifecycleAsync(
                stack,
                expectedProvider: PaymentProviderNames.Saferpay,
                confirmWithSessionReference: false);

            Assert.Equal(PaymentAuthorizationState.Captured, capturedAuthorization.State);
            Assert.Equal(150, capturedAuthorization.AmountReceivedMinor);
            Assert.Equal(reservationId, capturedAuthorization.OwnerReservationId);
        }

        [Fact]
        public async Task Saferpay_SignedNotificationAuthorizesThroughRealServices()
        {
            const string notifyBaseUrl = "https://ocpp.example.com/API";
            const string notifySecret = "integration-notify-secret";
            using var stack = new ServerStack(new Dictionary<string, string?>
            {
                ["Payments:Provider"] = "Saferpay",
                ["Payments:ReturnBaseUrl"] = ReturnBaseUrl,
                ["Saferpay:Enabled"] = "true",
                ["Saferpay:UseMockServices"] = "true",
                ["Saferpay:MockPayerEmail"] = MockPayerEmail,
                ["Saferpay:NotifyBaseUrl"] = notifyBaseUrl,
                ["Saferpay:NotifySecret"] = notifySecret
            });
            var coordinator = stack.Coordinator;
            using var db = stack.CreateDbContext();
            var created = await SeedAndCreateCheckoutAsync(coordinator, db);
            var reservationId = created.Reservation.ReservationId;

            // A notification without a valid signature changes nothing.
            var forged = await coordinator.HandleWebhookAsync(
                db,
                new PaymentWebhookRequest
                {
                    Provider = "Saferpay",
                    Method = "GET",
                    Query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["reservationId"] = reservationId.ToString(),
                        ["sig"] = "not-a-valid-signature"
                    }
                },
                CancellationToken.None);
            Assert.Equal(400, forged.StatusCode);
            Assert.Empty(forged.AuthorizedReservationIds);
            Assert.Equal(PaymentReservationStatus.Pending, ReloadReservation(db, reservationId).Status);

            // The notify URL Saferpay calls, signed with the same secret the server uses.
            var signer = new Payments.Providers.Saferpay.SaferpayNotificationSigner(new Payments.Providers.Saferpay.SaferpayOptions
            {
                NotifyBaseUrl = notifyBaseUrl,
                NotifySecret = notifySecret
            });
            var notifyUrl = new Uri(signer.BuildSuccessNotifyUrl(reservationId));
            Assert.Equal("/API/Payments/Webhook/Saferpay", notifyUrl.AbsolutePath);

            var notified = await coordinator.HandleWebhookAsync(
                db,
                new PaymentWebhookRequest
                {
                    Provider = "Saferpay",
                    Method = "GET",
                    Query = ParseQuery(notifyUrl.Query)
                },
                CancellationToken.None);

            Assert.Equal(200, notified.StatusCode);
            Assert.Contains(reservationId, notified.AuthorizedReservationIds);
            var reservation = ReloadReservation(db, reservationId);
            Assert.Equal(PaymentReservationStatus.Authorized, reservation.Status);
            Assert.Equal(PaymentProviderNames.Saferpay, reservation.PaymentProvider);
            Assert.False(string.IsNullOrWhiteSpace(reservation.StripePaymentIntentId));
            Assert.Equal(MockPayerEmail, reservation.PayerEmail);
        }

        [Fact]
        public async Task Stripe_DefaultProviderCreateConfirmStartAndPartialCapture_ThroughRealServices()
        {
            // No Payments:Provider: the default Stripe deployment keeps working through the same services.
            using var stack = new ServerStack(new Dictionary<string, string?>
            {
                ["Stripe:Enabled"] = "true",
                ["Stripe:UseMockServices"] = "true",
                ["Stripe:ApiKey"] = "mock_test_key",
                ["Stripe:ReturnBaseUrl"] = ReturnBaseUrl,
                ["Stripe:MockCustomerEmail"] = MockPayerEmail
            });

            Assert.True(stack.Coordinator.IsEnabled);
            Assert.Equal(PaymentProviderNames.Stripe, stack.Registry.Active.ProviderName);

            var (_, capturedAuthorization) = await RunLifecycleAsync(
                stack,
                expectedProvider: PaymentProviderNames.Stripe,
                confirmWithSessionReference: true);

            Assert.Equal(PaymentAuthorizationState.Captured, capturedAuthorization.State);
            Assert.Equal(150, capturedAuthorization.AmountReceivedMinor);
        }

        private static async Task<(Guid ReservationId, PaymentAuthorizationSnapshot CapturedAuthorization)> RunLifecycleAsync(
            ServerStack stack,
            string expectedProvider,
            bool confirmWithSessionReference)
        {
            var coordinator = stack.Coordinator;
            using var db = stack.CreateDbContext();
            var created = await SeedAndCreateCheckoutAsync(coordinator, db);

            var reservation = created.Reservation;
            var reservationId = reservation.ReservationId;
            Assert.Equal(expectedProvider, reservation.PaymentProvider);
            Assert.Equal(PaymentReservationStatus.Pending, reservation.Status);
            Assert.Equal(450, reservation.MaxAmountCents);
            Assert.False(string.IsNullOrWhiteSpace(reservation.StripeCheckoutSessionId));
            Assert.Contains("/Payments/MockCheckout", created.CheckoutUrl);
            var checkoutQuery = ParseQuery(new Uri(created.CheckoutUrl).Query);
            Assert.Equal(reservationId.ToString(), checkoutQuery["reservationId"], ignoreCase: true);
            Assert.StartsWith($"{ReturnBaseUrl}/Payments/Success?reservationId={reservationId}", checkoutQuery["successUrl"]);

            // The browser return: Saferpay appends no checkout reference, Stripe fills in session_id.
            var confirmation = coordinator.ConfirmReservation(
                db,
                reservationId,
                confirmWithSessionReference ? reservation.StripeCheckoutSessionId : null);

            Assert.True(confirmation.Success, $"{confirmation.Status}: {confirmation.Error}");
            Assert.Equal(PaymentReservationStatus.Authorized, confirmation.Status);
            reservation = ReloadReservation(db, reservationId);
            Assert.Equal(PaymentReservationStatus.Authorized, reservation.Status);
            Assert.Equal(expectedProvider, reservation.PaymentProvider);
            Assert.False(string.IsNullOrWhiteSpace(reservation.StripePaymentIntentId));
            Assert.Equal(MockPayerEmail, reservation.PayerEmail);
            Assert.False(string.IsNullOrWhiteSpace(reservation.OcppIdTag));

            var startedAt = DateTime.UtcNow.AddMinutes(-30);
            var transaction = new Transaction
            {
                TransactionId = 4242,
                ChargePointId = ChargePointId,
                ConnectorId = 1,
                StartTagId = reservation.OcppIdTag,
                StartTime = startedAt,
                MeterStart = 0
            };
            db.Transactions.Add(transaction);
            db.SaveChanges();

            coordinator.MarkTransactionStarted(db, ChargePointId, 1, reservation.OcppIdTag, transaction.TransactionId);
            reservation = ReloadReservation(db, reservationId);
            Assert.Equal(PaymentReservationStatus.Charging, reservation.Status);
            Assert.Equal(transaction.TransactionId, reservation.TransactionId);

            // 2.5 kWh * 0.40 = 1.00 EUR energy + 0.50 EUR session fee = 150 minor units, below the 450 hold.
            transaction.MeterStop = 2.5;
            transaction.StopTime = startedAt.AddMinutes(25);
            transaction.StopReason = "EVDisconnected";
            db.SaveChanges();

            coordinator.CompleteReservation(db, transaction);

            reservation = ReloadReservation(db, reservationId);
            Assert.Equal(PaymentReservationStatus.Completed, reservation.Status);
            Assert.Equal(150, reservation.CapturedAmountCents);
            Assert.NotNull(reservation.CapturedAtUtc);

            // The owning gateway reports the PSP-side state of the captured authorization.
            var gateway = stack.Registry.Resolve(reservation.PaymentProvider);
            Assert.NotNull(gateway);
            Assert.Equal(expectedProvider, gateway.ProviderName);
            var authorization = gateway.GetAuthorization(reservation.StripePaymentIntentId);
            Assert.NotNull(authorization);
            return (reservationId, authorization);
        }

        private static async Task<PaymentSessionResult> SeedAndCreateCheckoutAsync(IPaymentCoordinator coordinator, OCPPCoreContext db)
        {
            db.ChargePoints.Add(new ChargePoint
            {
                ChargePointId = ChargePointId,
                Name = "Integration charger",
                MaxSessionKwh = 10,
                PricePerKwh = 0.40m,
                UserSessionFee = 0.50m
            });
            db.ChargeTags.Add(new ChargeTag { TagId = ChargeTagId, TagName = "Integration tag" });
            db.SaveChanges();

            return await coordinator.CreateCheckoutSessionAsync(
                db,
                new PaymentSessionRequest
                {
                    ChargePointId = ChargePointId,
                    ConnectorId = 1,
                    ChargeTagId = ChargeTagId,
                    Origin = "public"
                },
                CancellationToken.None);
        }

        private static ChargePaymentReservation ReloadReservation(OCPPCoreContext db, Guid reservationId)
        {
            db.ChangeTracker.Clear();
            return db.ChargePaymentReservations.AsNoTracking().Single(r => r.ReservationId == reservationId);
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int separator = part.IndexOf('=');
                string key = Uri.UnescapeDataString(separator < 0 ? part : part.Substring(0, separator));
                string value = separator < 0 ? string.Empty : Uri.UnescapeDataString(part.Substring(separator + 1));
                values[key] = value;
            }

            return values;
        }

        /// <summary>Server services built by <see cref="Startup.ConfigureServices"/> over a temporary SQLite file.</summary>
        private sealed class ServerStack : IDisposable
        {
            private readonly string _databasePath;
            private readonly ServiceProvider _provider;

            public ServerStack(IDictionary<string, string?> values)
            {
                _databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-payment-integration-{Guid.NewGuid():N}.sqlite");
                var settings = new Dictionary<string, string?>(values)
                {
                    ["ConnectionStrings:SQLite"] = $"Filename={_databasePath}",
                    ["Notifications:EnableCustomerEmails"] = "false",
                    ["Invoices:Enabled"] = "false"
                };
                IConfiguration configuration = new ConfigurationBuilder()
                    .AddInMemoryCollection(settings)
                    .Build();

                var services = new ServiceCollection();
                services.AddSingleton(configuration);
                new Startup(configuration).ConfigureServices(services);
                _provider = services.BuildServiceProvider();

                using var db = CreateDbContext();
                db.Database.EnsureCreated();
            }

            public IPaymentCoordinator Coordinator => _provider.GetRequiredService<IPaymentCoordinator>();

            public IPaymentGatewayRegistry Registry => _provider.GetRequiredService<IPaymentGatewayRegistry>();

            public OCPPCoreContext CreateDbContext() => _provider.GetRequiredService<OCPPCoreContext>();

            public void Dispose()
            {
                _provider.Dispose();
                SqliteConnection.ClearAllPools();
                foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
                {
                    try
                    {
                        File.Delete(path);
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
            }
        }
    }
}
