using Stripe.Checkout;

namespace OCPP.Core.Server.Payments
{
    public interface IStripeCheckoutSessionReader
    {
        Session Get(string id);
    }
}
