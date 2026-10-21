/*
 * OCPP.Core - https://github.com/dallmann-consulting/OCPP.Core
 */

using System;
using System.Linq;
using Hangfire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OCPP.Core.Server.Payments.Invoices;
using OCPP.Core.Server.Payments.Providers.Saferpay;

namespace OCPP.Core.Server.Payments.Providers
{
    public static class PaymentProviderServiceCollectionExtensions
    {
        /// <summary>
        /// Registers the payment gateways, the gateway registry and the provider-neutral payment coordinator.
        /// <c>Payments:Provider</c> selects the gateway for new checkouts (Stripe when empty); an unknown value
        /// throws here, while services are being configured, so a typo never falls back to another provider.
        /// </summary>
        public static IServiceCollection AddPaymentProviders(this IServiceCollection services, IConfiguration configuration)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));

            services.Configure<PaymentProviderOptions>(configuration.GetSection("Payments"));
            services.Configure<StripeOptions>(configuration.GetSection("Stripe"));

            var providerOptions = configuration.GetSection("Payments").Get<PaymentProviderOptions>() ?? new PaymentProviderOptions();
            var stripeOptions = configuration.GetSection("Stripe").Get<StripeOptions>() ?? new StripeOptions();
            var checkoutSettings = PaymentCheckoutSettings.Resolve(providerOptions, stripeOptions);
            services.AddSingleton(checkoutSettings);

            // Stripe stays registered whichever provider is active, so existing Stripe reservations can
            // still be captured, released and receive webhooks after Payments:Provider changes.
            bool useMockStripeServices = configuration.GetValue<bool>("Stripe:UseMockServices");
            if (useMockStripeServices)
            {
                services.AddSingleton(sp => new MockStripeStore(sp.GetService<IConfiguration>() ?? configuration));
            }
            services.AddSingleton(sp => new StripePaymentGateway(
                sp.GetRequiredService<IOptions<StripeOptions>>().Value,
                useMockStripeServices
                    ? new MockStripeSessionService(
                        sp.GetRequiredService<MockStripeStore>(),
                        sp.GetService<IConfiguration>() ?? configuration)
                    : new StripeSessionServiceWrapper(),
                useMockStripeServices
                    ? new MockStripePaymentIntentService(sp.GetRequiredService<MockStripeStore>())
                    : new StripePaymentIntentServiceWrapper(),
                new StripeEventFactoryWrapper(),
                sp.GetService<ILogger<StripePaymentGateway>>()));
            services.AddSingleton<IPaymentGateway>(sp => sp.GetRequiredService<StripePaymentGateway>());

            services.AddSaferpayPaymentGateway(configuration);

            services.AddSingleton<IPaymentGatewayRegistry>(sp =>
            {
                var settings = sp.GetRequiredService<PaymentCheckoutSettings>();
                var registry = new PaymentGatewayRegistry(sp.GetServices<IPaymentGateway>(), settings.ActiveProvider);
                // The registry is a singleton, so this runs once, when the payment services are first built.
                LogProviderReadiness(
                    registry,
                    settings,
                    sp.GetService<ILoggerFactory>()?.CreateLogger(typeof(PaymentGatewayRegistry)));
                return registry;
            });
            services.AddSingleton<IPaymentCoordinator>(sp => new PaymentCoordinator(
                sp.GetRequiredService<IPaymentGatewayRegistry>(),
                sp.GetRequiredService<PaymentCheckoutSettings>(),
                sp.GetRequiredService<IOptions<PaymentFlowOptions>>(),
                sp.GetService<ILogger<PaymentCoordinator>>(),
                () => DateTime.UtcNow,
                sp.GetService<IEmailNotificationService>(),
                sp.GetService<StartChargingMediator>(),
                sp.GetService<IBackgroundJobClient>(),
                sp.GetService<IInvoiceIntegrationService>(),
                sp.GetService<IConfiguration>() ?? configuration,
                sp.GetService<IViesVerificationService>()));

            return services;
        }

        /// <summary>
        /// Logs why payments are only partly available. Logging only: the gates themselves are
        /// <see cref="IPaymentCoordinator.IsEnabled"/> (new checkouts) and <see cref="IPaymentCoordinator.IsOperational"/>
        /// (existing reservations). Silent when every gateway is unconfigured, the normal state of a deployment
        /// without online payments.
        /// </summary>
        internal static void LogProviderReadiness(IPaymentGatewayRegistry registry, PaymentCheckoutSettings settings, ILogger logger)
        {
            if (registry?.Active == null || logger == null)
            {
                return;
            }

            var active = registry.Active;
            var configured = registry.All
                .Where(gateway => gateway != null && gateway.IsConfigured)
                .Select(gateway => gateway.ProviderName)
                .ToList();
            if (configured.Count == 0)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(settings?.ReturnBaseUrl))
            {
                logger.LogWarning(
                    "Payments => Payments:ReturnBaseUrl (or Stripe:ReturnBaseUrl) is empty, so payments are disabled although payment provider(s) {ConfiguredProviders} are configured.",
                    string.Join(", ", configured));
                return;
            }

            if (active.IsConfigured)
            {
                return;
            }

            logger.LogWarning(
                "Payments => Payments:Provider selects '{ActiveProvider}', but that payment provider is not configured (check Enabled and the credentials in its configuration section). New checkouts are disabled; existing reservations of the configured provider(s) {ConfiguredProviders} are still processed.",
                active.ProviderName,
                string.Join(", ", configured));
        }
    }
}
