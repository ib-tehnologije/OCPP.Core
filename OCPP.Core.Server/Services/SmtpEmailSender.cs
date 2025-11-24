using System;
using System.Net;
using System.Net.Mail;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OCPP.Core.Server.Options;

namespace OCPP.Core.Server.Services
{
    public class SmtpEmailSender : IEmailSender
    {
        private readonly ILogger<SmtpEmailSender> _logger;
        private readonly IOptionsMonitor<EmailOptions> _optionsMonitor;

        public SmtpEmailSender(ILogger<SmtpEmailSender> logger, IOptionsMonitor<EmailOptions> optionsMonitor)
        {
            _logger = logger;
            _optionsMonitor = optionsMonitor;
        }

        public async Task SendEmailAsync(string recipient, string subject, string body, CancellationToken cancellationToken = default)
        {
            var options = _optionsMonitor.CurrentValue;
            if (!options.Enabled)
            {
                _logger.LogDebug("Email sending disabled. Skipping message to {Recipient}", recipient);
                return;
            }

            if (string.IsNullOrWhiteSpace(options.Host) || string.IsNullOrWhiteSpace(options.FromAddress))
            {
                _logger.LogWarning("Email settings incomplete. Host or FromAddress missing.");
                return;
            }

            using var message = new MailMessage
            {
                From = new MailAddress(options.FromAddress, string.IsNullOrWhiteSpace(options.FromName) ? options.FromAddress : options.FromName),
                Subject = subject,
                Body = body,
                IsBodyHtml = false
            };
            message.To.Add(new MailAddress(recipient));

            using var client = new SmtpClient(options.Host, options.Port)
            {
                EnableSsl = options.UseSsl
            };

            if (!string.IsNullOrWhiteSpace(options.Username))
            {
                client.Credentials = new NetworkCredential(options.Username, options.Password);
            }
            else
            {
                client.UseDefaultCredentials = true;
            }

            try
            {
                await client.SendMailAsync(message, cancellationToken);
                _logger.LogInformation("Sent e-mail to {Recipient} with subject '{Subject}'", recipient, subject);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send e-mail to {Recipient}", recipient);
                throw;
            }
        }
    }
}
