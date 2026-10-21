/*
 * OCPP.Core - https://github.com/dallmann-consulting/OCPP.Core
 */

using Stripe;
using Stripe.Checkout;

namespace OCPP.Core.Server.Payments
{
    // Thin seams over the Stripe SDK services so the Stripe gateway can be tested with fakes
    // and run against the in-process mock (Stripe:UseMockServices).

    internal interface IStripeSessionService
    {
        Session Create(SessionCreateOptions options, RequestOptions requestOptions = null);
        Session Get(string id);
        Session Update(string id, SessionUpdateOptions options, RequestOptions requestOptions = null);
    }

    internal interface IStripePaymentIntentService
    {
        PaymentIntent Get(string id);
        PaymentIntent Update(string id, PaymentIntentUpdateOptions options, RequestOptions requestOptions = null);
        PaymentIntent Capture(string id, PaymentIntentCaptureOptions options, RequestOptions requestOptions = null);
        PaymentIntent Cancel(string id, RequestOptions requestOptions = null);
    }

    internal interface IStripeEventFactory
    {
        Event ConstructEvent(string payload, string signatureHeader, string webhookSecret, bool throwOnApiVersionMismatch = true);
    }

    internal class StripeSessionServiceWrapper : IStripeSessionService
    {
        private readonly SessionService _inner = new SessionService();

        public Session Create(SessionCreateOptions options, RequestOptions requestOptions = null) => _inner.Create(options, requestOptions);

        public Session Get(string id) => _inner.Get(id);

        public Session Update(string id, SessionUpdateOptions options, RequestOptions requestOptions = null) =>
            _inner.Update(id, options, requestOptions);
    }

    internal class StripePaymentIntentServiceWrapper : IStripePaymentIntentService
    {
        private readonly PaymentIntentService _inner = new PaymentIntentService();

        public PaymentIntent Get(string id) => _inner.Get(id);

        public PaymentIntent Update(string id, PaymentIntentUpdateOptions options, RequestOptions requestOptions = null) =>
            _inner.Update(id, options, requestOptions);

        public PaymentIntent Capture(string id, PaymentIntentCaptureOptions options, RequestOptions requestOptions = null) => _inner.Capture(id, options, requestOptions);

        public PaymentIntent Cancel(string id, RequestOptions requestOptions = null) =>
            _inner.Cancel(id, options: null, requestOptions: requestOptions);
    }

    internal class StripeEventFactoryWrapper : IStripeEventFactory
    {
        public Event ConstructEvent(string payload, string signatureHeader, string webhookSecret, bool throwOnApiVersionMismatch = true) =>
            EventUtility.ConstructEvent(payload, signatureHeader, webhookSecret, throwOnApiVersionMismatch: throwOnApiVersionMismatch);
    }
}
