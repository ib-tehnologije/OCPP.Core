using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OCPP.Core.Server.Payments;
using OCPP.Core.Server.Payments.Providers;
using Stripe;
using Stripe.Checkout;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    public class PaymentProviderRegistrationTests
    {
        [Fact]
        public void Startup_DefaultsToStripeAndResolvesCoordinator()
        {
            var configuration = BuildConfiguration(new Dictionary<string, string?>
            {
                ["Stripe:UseMockServices"] = "true"
            });
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(configuration);

            new Startup(configuration).ConfigureServices(services);
            using var provider = services.BuildServiceProvider();

            var settings = provider.GetRequiredService<PaymentCheckoutSettings>();
            var registry = provider.GetRequiredService<IPaymentGatewayRegistry>();
            var coordinator = provider.GetRequiredService<IPaymentCoordinator>();

            Assert.Equal(PaymentProviderNames.Stripe, settings.DefaultProvider);
            Assert.Equal(PaymentProviderNames.Stripe, registry.Default.ProviderName);
            Assert.IsType<StripePaymentGateway>(registry.Default);
            Assert.Same(registry.Default, registry.Resolve(null));
            Assert.IsType<PaymentCoordinator>(coordinator);
            Assert.False(coordinator.IsEnabled);
            Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(MockStripeStore));
            Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IPaymentCoordinator));
            // The idle-fee warning service is activated by DI from the provider-neutral checkout settings.
            Assert.Single(provider.GetServices<IHostedService>().OfType<IdleFeeWarningEmailService>());
        }

        [Fact]
        public void Startup_RegistersMockStripeStoreOnlyInMockMode()
        {
            var configuration = BuildConfiguration(new Dictionary<string, string?>());
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(configuration);

            new Startup(configuration).ConfigureServices(services);
            using var provider = services.BuildServiceProvider();

            Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(MockStripeStore));
            Assert.Equal(PaymentProviderNames.Stripe, provider.GetRequiredService<IPaymentGatewayRegistry>().Default.ProviderName);
        }

        [Theory]
        [InlineData("stripe")]
        [InlineData(" STRIPE ")]
        [InlineData("")]
        public void AddPaymentProviders_ProviderNameIsCaseInsensitive(string configuredProvider)
        {
            var configuration = BuildConfiguration(new Dictionary<string, string?>
            {
                ["Payments:Provider"] = configuredProvider,
                ["Stripe:Enabled"] = "true",
                ["Stripe:ApiKey"] = "test",
                ["Stripe:ReturnBaseUrl"] = "https://return.example.com"
            });
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(configuration);

            services.AddPaymentProviders(configuration);
            using var provider = services.BuildServiceProvider();

            Assert.Equal(PaymentProviderNames.Stripe, provider.GetRequiredService<PaymentCheckoutSettings>().DefaultProvider);
            Assert.Equal(PaymentProviderNames.Stripe, provider.GetRequiredService<IPaymentGatewayRegistry>().Default.ProviderName);
            Assert.True(provider.GetRequiredService<IPaymentCoordinator>().IsEnabled);
        }

        [Fact]
        public void AddPaymentProviders_UnknownProviderFailsWhileConfiguringServices()
        {
            var configuration = BuildConfiguration(new Dictionary<string, string?>
            {
                ["Payments:Provider"] = "RetiredPay"
            });

            var direct = Assert.Throws<InvalidOperationException>(() =>
                new ServiceCollection().AddPaymentProviders(configuration));
            var startup = Assert.Throws<InvalidOperationException>(() =>
                new Startup(configuration).ConfigureServices(new ServiceCollection()));

            Assert.Contains("RetiredPay", direct.Message);
            Assert.Contains("Payments:Provider", startup.Message);
        }

        [Fact]
        public void AddPaymentProviders_ActiveProviderWithoutRegisteredGatewayThrowsClearly()
        {
            var configuration = BuildConfiguration(new Dictionary<string, string?>
            {
                ["Payments:Provider"] = "Saferpay"
            });
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddPaymentProviders(configuration);
            // Keep only the Stripe gateway, whatever other gateways are registered by default.
            services.RemoveAll<IPaymentGateway>();
            services.AddSingleton<IPaymentGateway>(sp => sp.GetRequiredService<StripePaymentGateway>());
            using var provider = services.BuildServiceProvider();

            Assert.Equal(PaymentProviderNames.Saferpay, provider.GetRequiredService<PaymentCheckoutSettings>().DefaultProvider);
            var error = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IPaymentGatewayRegistry>());
            Assert.Contains("Saferpay", error.Message);
            Assert.Contains("no 'Saferpay' payment gateway is registered", error.Message);
        }

        [Fact]
        public void AddPaymentProviders_WarnsOnceWhenActiveProviderIsNotConfiguredButAnotherIs()
        {
            // Mid-switch: Saferpay is selected but not usable yet, Stripe is still configured for its reservations.
            var configuration = BuildConfiguration(new Dictionary<string, string?>
            {
                ["Payments:Provider"] = "Saferpay",
                ["Payments:ReturnBaseUrl"] = "https://return.example.com",
                ["Saferpay:Enabled"] = "false",
                ["Stripe:Enabled"] = "true",
                ["Stripe:ApiKey"] = "test"
            });
            var logs = new RecordingLoggerProvider();
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.AddProvider(logs));
            services.AddSingleton<IConfiguration>(configuration);
            services.AddPaymentProviders(configuration);
            using var provider = services.BuildServiceProvider();

            var coordinator = provider.GetRequiredService<IPaymentCoordinator>();
            provider.GetRequiredService<IPaymentGatewayRegistry>();
            provider.GetRequiredService<IPaymentCoordinator>();

            // Logging only: the gates are unchanged.
            Assert.False(coordinator.IsEnabled);
            Assert.True(coordinator.IsOperational);
            var warning = Assert.Single(logs.Entries, entry => entry.Level == LogLevel.Warning);
            Assert.Contains("'Saferpay'", warning.Message);
            Assert.Contains("not configured", warning.Message);
            Assert.Contains("New checkouts on charge points without their own provider are disabled", warning.Message);
            Assert.Contains("Stripe", warning.Message);
        }

        [Theory]
        [InlineData(null, "true", "https://return.example.com")]
        [InlineData("Saferpay", "false", "https://return.example.com")]
        public void AddPaymentProviders_DoesNotWarnWhenActiveProviderIsConfiguredOrNothingIs(
            string? activeProvider,
            string stripeEnabled,
            string returnBaseUrl)
        {
            var configuration = BuildConfiguration(new Dictionary<string, string?>
            {
                ["Payments:Provider"] = activeProvider,
                ["Payments:ReturnBaseUrl"] = returnBaseUrl,
                ["Stripe:Enabled"] = stripeEnabled,
                ["Stripe:ApiKey"] = "test"
            });
            var logs = new RecordingLoggerProvider();
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.AddProvider(logs));
            services.AddSingleton<IConfiguration>(configuration);
            services.AddPaymentProviders(configuration);
            using var provider = services.BuildServiceProvider();

            provider.GetRequiredService<IPaymentCoordinator>();

            Assert.DoesNotContain(logs.Entries, entry => entry.Level >= LogLevel.Warning);
        }

        [Fact]
        public void AddPaymentProviders_WarnsWhenReturnBaseUrlIsMissingButAProviderIsConfigured()
        {
            var configuration = BuildConfiguration(new Dictionary<string, string?>
            {
                ["Stripe:Enabled"] = "true",
                ["Stripe:ApiKey"] = "test"
            });
            var logs = new RecordingLoggerProvider();
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.AddProvider(logs));
            services.AddSingleton<IConfiguration>(configuration);
            services.AddPaymentProviders(configuration);
            using var provider = services.BuildServiceProvider();

            var coordinator = provider.GetRequiredService<IPaymentCoordinator>();

            Assert.False(coordinator.IsEnabled);
            Assert.False(coordinator.IsOperational);
            var warning = Assert.Single(logs.Entries, entry => entry.Level == LogLevel.Warning);
            Assert.Contains("Payments:ReturnBaseUrl", warning.Message);
        }

        [Fact]
        public void Registry_ResolvesPersistedProvidersAndRejectsDuplicates()
        {
            var stripe = CreateStripeGateway();
            var fake = new FakePaymentGateway("FakePay");
            var registry = new PaymentGatewayRegistry(new IPaymentGateway[] { stripe, fake }, "fakepay");

            Assert.Same(fake, registry.Default);
            Assert.Same(stripe, registry.Resolve(null));
            Assert.Same(stripe, registry.Resolve(" "));
            Assert.Same(stripe, registry.Resolve("STRIPE"));
            Assert.Same(fake, registry.Resolve("FakePay"));
            Assert.Null(registry.Resolve("RetiredPay"));
            Assert.Equal(2, registry.All.Count);
            Assert.Throws<InvalidOperationException>(() =>
                new PaymentGatewayRegistry(new IPaymentGateway[] { stripe, CreateStripeGateway() }, PaymentProviderNames.Stripe));
            Assert.Throws<InvalidOperationException>(() =>
                new PaymentGatewayRegistry(new IPaymentGateway[] { stripe }, PaymentProviderNames.Saferpay));
        }

        [Fact]
        public void StripeGateway_MapsSessionAndPaymentIntentToNeutralSnapshots()
        {
            var reservationId = Guid.NewGuid();
            var sessions = new FakeSessionService
            {
                GetResponse = new Session
                {
                    Id = "cs_map",
                    Status = "open",
                    PaymentStatus = "unpaid",
                    Url = "https://checkout.example.com/cs_map",
                    PaymentIntentId = "pi_map",
                    CustomerDetails = new SessionCustomerDetails { Email = "payer@example.com", Name = "Pat Payer" },
                    Metadata = new Dictionary<string, string> { ["reservation_id"] = reservationId.ToString() }
                }
            };
            var intents = new FakePaymentIntentService
            {
                GetResponse = new PaymentIntent
                {
                    Id = "pi_map",
                    Status = "requires_payment_method",
                    Amount = 500,
                    AmountCapturable = 0,
                    Currency = "eur",
                    LastPaymentError = new StripeError { Message = "card_declined" },
                    Metadata = new Dictionary<string, string> { ["reservation_id"] = reservationId.ToString() }
                }
            };
            var gateway = CreateStripeGateway(sessions, intents);

            var checkout = gateway.GetCheckout("cs_map");
            var authorization = gateway.GetAuthorization("pi_map");

            Assert.Equal(PaymentCheckoutState.Open, checkout.State);
            Assert.Equal("open", checkout.RawStatus);
            Assert.False(checkout.IsPaid);
            Assert.Equal("https://checkout.example.com/cs_map", checkout.ResumeUrl);
            Assert.Equal("pi_map", checkout.AuthorizationReference);
            Assert.Equal("payer@example.com", checkout.CustomerEmail);
            Assert.Equal("Pat Payer", checkout.CustomerName);
            Assert.Equal(reservationId, checkout.OwnerReservationId);
            Assert.Equal(PaymentAuthorizationState.Pending, authorization.State);
            Assert.True(authorization.IsCancelable);
            Assert.Equal(500, authorization.AmountMinor);
            Assert.Equal("card_declined", authorization.FailureMessage);
            Assert.Equal(reservationId, authorization.OwnerReservationId);

            sessions.GetResponse.Status = "complete";
            sessions.GetResponse.PaymentStatus = "paid";
            intents.GetResponse.Status = "requires_action";
            var completed = gateway.GetCheckout("cs_map");
            var actionRequired = gateway.GetAuthorization("pi_map");

            Assert.Equal(PaymentCheckoutState.Complete, completed.State);
            Assert.True(completed.IsPaid);
            Assert.Null(completed.ResumeUrl);
            Assert.Equal(PaymentAuthorizationState.Pending, actionRequired.State);
            Assert.False(actionRequired.IsCancelable);
        }

        [Theory]
        [InlineData(HttpStatusCode.ServiceUnavailable, "api_error", "timeout", true)]
        [InlineData(HttpStatusCode.TooManyRequests, "rate_limit_error", null, true)]
        [InlineData(HttpStatusCode.BadRequest, "invalid_request_error", "payment_intent_unexpected_state", false)]
        [InlineData(HttpStatusCode.BadRequest, "card_error", null, false)]
        public void StripeGateway_ConvertsStripeExceptionsToProviderExceptions(
            HttpStatusCode statusCode,
            string type,
            string? code,
            bool transient)
        {
            var intents = new FakePaymentIntentService
            {
                CancelException = new StripeException(
                    statusCode,
                    new StripeError { Type = type, Code = code, Message = "provider says no" },
                    "Stripe request failed")
            };
            var gateway = CreateStripeGateway(intents: intents);

            var error = Assert.Throws<PaymentProviderException>(() => gateway.Cancel("pi_fail", "cancel:key"));

            Assert.Equal(PaymentProviderNames.Stripe, error.Provider);
            Assert.Equal(code ?? type, error.Code);
            Assert.Equal(transient, error.IsTransient);
            Assert.Equal((int)statusCode, error.HttpStatusCode);
            Assert.Equal("provider says no", error.ProviderMessage);
            Assert.Equal("Stripe request failed", error.Message);
            Assert.IsType<StripeException>(error.InnerException);
            Assert.Equal("cancel:key", intents.LastCancelRequestOptions?.IdempotencyKey);
        }

        [Fact]
        public void StripeGateway_RejectsWebhooksWithoutSecretAndMapsEventKinds()
        {
            var noSecret = new StripePaymentGateway(
                new StripeOptions { Enabled = true, ApiKey = "test" },
                new FakeSessionService(),
                new FakePaymentIntentService(),
                new FakeEventFactory(),
                NullLogger.Instance);
            var rejected = noSecret.ParseWebhook(new PaymentWebhookRequest { Body = "{}" });

            Assert.False(rejected.Accepted);
            Assert.Equal(200, rejected.StatusCode);

            var reservationId = Guid.NewGuid();
            var created = new DateTime(2026, 10, 20, 10, 0, 0, DateTimeKind.Utc);
            var events = new FakeEventFactory
            {
                EventToReturn = new Event
                {
                    Id = "evt_map",
                    Type = "payment_intent.amount_capturable_updated",
                    Created = created,
                    Data = new EventData
                    {
                        Object = new PaymentIntent
                        {
                            Id = "pi_evt",
                            Status = "requires_capture",
                            AmountCapturable = 500,
                            Metadata = new Dictionary<string, string> { ["reservation_id"] = reservationId.ToString() }
                        }
                    }
                }
            };
            var gateway = new StripePaymentGateway(
                new StripeOptions { Enabled = true, ApiKey = "test", WebhookSecret = "whsec_test" },
                new FakeSessionService(),
                new FakePaymentIntentService(),
                events,
                NullLogger.Instance);

            var parsed = gateway.ParseWebhook(new PaymentWebhookRequest
            {
                Body = "{}",
                Headers = new Dictionary<string, string> { ["stripe-signature"] = "t=1,v1=abc" }
            });

            var webhookEvent = Assert.Single(parsed.Events);
            Assert.True(parsed.Accepted);
            Assert.Equal("evt_map", webhookEvent.EventId);
            Assert.Equal(PaymentWebhookEventKind.AuthorizationCapturable, webhookEvent.Kind);
            Assert.Equal(created, webhookEvent.CreatedAtUtc);
            Assert.Equal("pi_evt", webhookEvent.Authorization?.AuthorizationReference);
            Assert.Equal(reservationId, webhookEvent.ReservationId);
        }

        [Theory]
        [InlineData("network", "network_error")]
        [InlineData("timeout", "timeout")]
        public void StripeGateway_MapsTransportFailuresToTransientProviderErrors(string failure, string expectedCode)
        {
            Exception transportFailure = failure == "network"
                ? new System.Net.Http.HttpRequestException("connection refused")
                : new System.Threading.Tasks.TaskCanceledException("request timed out");
            var gateway = new StripePaymentGateway(
                new StripeOptions { Enabled = true, ApiKey = "test" },
                new FakeSessionService(),
                new ThrowingPaymentIntentService(transportFailure),
                new FakeEventFactory(),
                NullLogger.Instance);

            var capture = Assert.Throws<PaymentProviderException>(() => gateway.Capture("pi_transport", 100, "eur", "capture:key"));
            var lookup = Assert.Throws<PaymentProviderException>(() => gateway.GetAuthorization("pi_transport"));

            foreach (var error in new[] { capture, lookup })
            {
                Assert.True(error.IsTransient);
                Assert.Equal(expectedCode, error.Code);
                Assert.Equal(PaymentProviderNames.Stripe, error.Provider);
                Assert.Same(transportFailure, error.InnerException);
            }
        }

        private sealed class ThrowingPaymentIntentService : IStripePaymentIntentService
        {
            private readonly Exception _exception;

            public ThrowingPaymentIntentService(Exception exception) => _exception = exception;

            public PaymentIntent Get(string id) => throw _exception;
            public PaymentIntent Update(string id, PaymentIntentUpdateOptions options, RequestOptions requestOptions = null!) => throw _exception;
            public PaymentIntent Capture(string id, PaymentIntentCaptureOptions options, RequestOptions requestOptions = null!) => throw _exception;
            public PaymentIntent Cancel(string id, RequestOptions requestOptions = null!) => throw _exception;
        }

        private static StripePaymentGateway CreateStripeGateway(
            FakeSessionService? sessions = null,
            FakePaymentIntentService? intents = null) =>
            new(
                new StripeOptions { Enabled = true, ApiKey = "test", ReturnBaseUrl = "https://return.example.com" },
                sessions ?? new FakeSessionService(),
                intents ?? new FakePaymentIntentService(),
                new FakeEventFactory(),
                NullLogger.Instance);

        private static IConfiguration BuildConfiguration(IDictionary<string, string?> values)
        {
            var settings = new Dictionary<string, string?>(values)
            {
                ["ConnectionStrings:SQLite"] = "Filename=:memory:"
            };
            return new ConfigurationBuilder()
                .AddInMemoryCollection(settings)
                .Build();
        }

        private sealed class RecordingLoggerProvider : ILoggerProvider
        {
            public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

            public ILogger CreateLogger(string categoryName) => new RecordingLogger(Entries);

            public void Dispose()
            {
            }

            private sealed class RecordingLogger : ILogger
            {
                private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries;

                public RecordingLogger(ConcurrentQueue<(LogLevel Level, string Message)> entries)
                {
                    _entries = entries;
                }

                public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

                public bool IsEnabled(LogLevel logLevel) => true;

                public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                    _entries.Enqueue((logLevel, formatter(state, exception)));
            }
        }
    }
}
