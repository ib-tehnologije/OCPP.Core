using System;
using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stripe.Checkout;
using OCPP.Core.Database;

namespace OCPP.Core.Server.Payments
{
    public interface IEmailNotificationService
    {
        void SendPaymentAuthorized(string toEmail, ChargePaymentReservation reservation, Session session);
    }

    public class EmailNotificationService : IEmailNotificationService
    {
        private readonly NotificationOptions _options;
        private readonly ILogger<EmailNotificationService> _logger;

        public EmailNotificationService(IOptions<NotificationOptions> options, ILogger<EmailNotificationService> logger)
        {
            _options = options?.Value ?? new NotificationOptions();
            _logger = logger;
        }

        public void SendPaymentAuthorized(string toEmail, ChargePaymentReservation reservation, Session session)
        {
            if (!_options.EnableCustomerEmails)
            {
                _logger.LogDebug("Email notifications disabled; skipping email for reservation {ReservationId}", reservation?.ReservationId);
                return;
            }

            if (string.IsNullOrWhiteSpace(toEmail))
            {
                _logger.LogDebug("No recipient email available for reservation {ReservationId}; skipping email.", reservation?.ReservationId);
                return;
            }

            if (string.IsNullOrWhiteSpace(_options?.Smtp?.Host) || string.IsNullOrWhiteSpace(_options.FromAddress))
            {
                _logger.LogWarning("Email notification skipped because SMTP configuration is incomplete.");
                return;
            }

            try
            {
                using var message = new MailMessage
                {
                    From = new MailAddress(_options.FromAddress, _options.FromName),
                    Subject = "Charging payment authorized",
                    Body = BuildBody(reservation, session, toEmail),
                    IsBodyHtml = false
                };

                message.To.Add(new MailAddress(toEmail));

                if (!string.IsNullOrWhiteSpace(_options.ReplyToAddress))
                {
                    message.ReplyToList.Add(new MailAddress(_options.ReplyToAddress));
                }

                if (!string.IsNullOrWhiteSpace(_options.BccAddress))
                {
                    message.Bcc.Add(new MailAddress(_options.BccAddress));
                }

                using var client = new SmtpClient(_options.Smtp.Host, _options.Smtp.Port)
                {
                    EnableSsl = _options.Smtp.UseStartTls,
                    Credentials = string.IsNullOrWhiteSpace(_options.Smtp.Username)
                        ? CredentialCache.DefaultNetworkCredentials
                        : new NetworkCredential(_options.Smtp.Username, _options.Smtp.Password)
                };

                client.Send(message);
                _logger.LogInformation("Sent payment authorization email to {Email} for reservation {ReservationId}", toEmail, reservation.ReservationId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send payment authorization email to {Email} for reservation {ReservationId}", toEmail, reservation?.ReservationId);
            }
        }

        private static string BuildBody(ChargePaymentReservation reservation, Session session, string toEmail)
        {
            var line1 = $"Hi, your payment was authorized and the charging session is starting.";
            var line2 = $"Reservation: {reservation?.ReservationId}";
            var line3 = $"Charge point: {reservation?.ChargePointId} / Connector: {reservation?.ConnectorId}";
            var line4 = $"Max authorized amount: {reservation?.MaxAmountCents / 100.0m:0.00} {reservation?.Currency?.ToUpperInvariant()}";
            var line5 = $"Stripe session: {session?.Id}";
            return string.Join(Environment.NewLine, line1, line2, line3, line4, line5);
        }
    }
}
