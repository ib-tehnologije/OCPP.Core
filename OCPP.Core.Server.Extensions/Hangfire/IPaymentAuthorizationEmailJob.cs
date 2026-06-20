using System;

namespace OCPP.Core.Server.Extensions.Hangfire
{
    public interface IPaymentAuthorizationEmailJob
    {
        void SendPaymentAuthorized(Guid reservationId, string toEmail, string checkoutSessionId, string statusUrl);
    }
}
