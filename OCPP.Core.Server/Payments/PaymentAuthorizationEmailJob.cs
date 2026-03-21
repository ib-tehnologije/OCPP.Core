using System;
using Hangfire;
using Microsoft.Extensions.Logging;
using OCPP.Core.Database;
using OCPP.Core.Server.Extensions.Hangfire;
using Stripe.Checkout;

namespace OCPP.Core.Server.Payments
{
    public class PaymentAuthorizationEmailJob : IPaymentAuthorizationEmailJob
    {
        private readonly OCPPCoreContext _dbContext;
        private readonly IEmailNotificationService _emailService;
        private readonly ILogger<PaymentAuthorizationEmailJob> _logger;

        public PaymentAuthorizationEmailJob(
            OCPPCoreContext dbContext,
            IEmailNotificationService emailService,
            ILogger<PaymentAuthorizationEmailJob> logger)
        {
            _dbContext = dbContext;
            _emailService = emailService;
            _logger = logger;
        }

        [Queue("payments")]
        public void SendPaymentAuthorized(Guid reservationId, string toEmail, string checkoutSessionId, string statusUrl)
        {
            if (reservationId == Guid.Empty)
            {
                _logger.LogWarning("PaymentAuthorizationEmailJob => Missing reservation id; email not sent.");
                return;
            }

            var reservation = _dbContext.ChargePaymentReservations.Find(reservationId);
            if (reservation == null)
            {
                _logger.LogWarning("PaymentAuthorizationEmailJob => Reservation not found reservation={ReservationId}", reservationId);
                return;
            }

            var session = new Session { Id = checkoutSessionId };
            try
            {
                _emailService?.SendPaymentAuthorized(toEmail, reservation, session, statusUrl);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PaymentAuthorizationEmailJob => Failed to send payment authorization email reservation={ReservationId}", reservationId);
            }
        }
    }
}
