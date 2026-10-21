/*
 * OCPP.Core - https://github.com/dallmann-consulting/OCPP.Core
 */

using System;
using Hangfire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OCPP.Core.Server.Payments.Invoices;
using OCPP.Core.Server.Payments.Providers;

namespace OCPP.Core.Server.Payments
{
    /// <summary>
    /// Stripe-only <see cref="PaymentCoordinator"/>: a single-gateway registry with the Stripe gateway
    /// and checkout settings taken from the <c>Stripe</c> section. Kept for callers (and tests) that
    /// construct the coordinator directly from <see cref="StripeOptions"/>.
    /// </summary>
    public class StripePaymentCoordinator : PaymentCoordinator
    {
        public StripePaymentCoordinator(
            IOptions<StripeOptions> options,
            IOptions<PaymentFlowOptions> flowOptions,
            ILogger<StripePaymentCoordinator> logger,
            IConfiguration configuration = null)
            : this(options, flowOptions, logger, new StripeSessionServiceWrapper(), new StripePaymentIntentServiceWrapper(), new StripeEventFactoryWrapper(), () => DateTime.UtcNow, null, null, null, null, configuration)
        {
        }

        internal StripePaymentCoordinator(
            IOptions<StripeOptions> options,
            IOptions<PaymentFlowOptions> flowOptions,
            ILogger<StripePaymentCoordinator> logger,
            IStripeSessionService sessionService,
            IStripePaymentIntentService paymentIntentService,
            IStripeEventFactory eventFactory,
            Func<DateTime> utcNow,
            IEmailNotificationService emailNotificationService = null,
            StartChargingMediator startMediator = null,
            IBackgroundJobClient backgroundJobClient = null,
            IInvoiceIntegrationService invoiceIntegrationService = null,
            IConfiguration configuration = null,
            IViesVerificationService viesVerificationService = null)
            : this(
                options?.Value ?? new StripeOptions(),
                flowOptions,
                logger,
                sessionService,
                paymentIntentService,
                eventFactory,
                utcNow,
                emailNotificationService,
                startMediator,
                backgroundJobClient,
                invoiceIntegrationService,
                configuration,
                viesVerificationService)
        {
        }

        private StripePaymentCoordinator(
            StripeOptions stripeOptions,
            IOptions<PaymentFlowOptions> flowOptions,
            ILogger<StripePaymentCoordinator> logger,
            IStripeSessionService sessionService,
            IStripePaymentIntentService paymentIntentService,
            IStripeEventFactory eventFactory,
            Func<DateTime> utcNow,
            IEmailNotificationService emailNotificationService,
            StartChargingMediator startMediator,
            IBackgroundJobClient backgroundJobClient,
            IInvoiceIntegrationService invoiceIntegrationService,
            IConfiguration configuration,
            IViesVerificationService viesVerificationService)
            : base(
                new PaymentGatewayRegistry(
                    new IPaymentGateway[]
                    {
                        new StripePaymentGateway(stripeOptions, sessionService, paymentIntentService, eventFactory, logger)
                    },
                    PaymentProviderNames.Stripe),
                PaymentCheckoutSettings.Resolve(new PaymentProviderOptions(), stripeOptions),
                flowOptions,
                logger,
                utcNow,
                emailNotificationService,
                startMediator,
                backgroundJobClient,
                invoiceIntegrationService,
                configuration,
                viesVerificationService)
        {
        }
    }
}
