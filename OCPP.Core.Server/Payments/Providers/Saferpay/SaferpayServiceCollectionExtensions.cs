/*
 * OCPP.Core - https://github.com/dallmann-consulting/OCPP.Core
 */

using System;
using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace OCPP.Core.Server.Payments.Providers.Saferpay
{
    public static class SaferpayServiceCollectionExtensions
    {
        public const string ConfigurationSectionName = "Saferpay";

        private const int DefaultTimeoutSeconds = 100;

        /// <summary>
        /// Registers the Saferpay gateway as an <see cref="IPaymentGateway"/> (it joins the
        /// <c>IEnumerable&lt;IPaymentGateway&gt;</c> seen by the gateway registry), binds <see cref="SaferpayOptions"/>
        /// from the <c>Saferpay</c> section and configures the named HTTP client <c>Saferpay</c>. With
        /// <c>Saferpay:UseMockServices=true</c> the client talks to the in-process <see cref="MockSaferpayApiHandler"/>.
        /// </summary>
        public static IServiceCollection AddSaferpayPaymentGateway(this IServiceCollection services, IConfiguration configuration)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));

            services.Configure<SaferpayOptions>(configuration.GetSection(ConfigurationSectionName));

            // Singleton so mock state survives handler rotation and is shared by every client instance.
            services.TryAddSingleton<MockSaferpayApiHandler>();

            services.AddHttpClient(SaferpayApiClient.HttpClientName, (serviceProvider, client) =>
                {
                    var options = serviceProvider.GetRequiredService<IOptions<SaferpayOptions>>().Value;
                    int timeoutSeconds = options.TimeoutSeconds > 0 ? options.TimeoutSeconds : DefaultTimeoutSeconds;
                    client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
                })
                .ConfigurePrimaryHttpMessageHandler(serviceProvider =>
                {
                    var options = serviceProvider.GetRequiredService<IOptions<SaferpayOptions>>().Value;
                    return options.UseMockServices
                        ? serviceProvider.GetRequiredService<MockSaferpayApiHandler>()
                        : new HttpClientHandler();
                })
                .RedactLoggedHeaders(new[] { "Authorization" });

            services.TryAddEnumerable(ServiceDescriptor.Singleton<IPaymentGateway, SaferpayPaymentGateway>());
            return services;
        }
    }
}
