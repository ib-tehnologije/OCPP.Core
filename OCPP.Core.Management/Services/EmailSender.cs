using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Mail;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OCPP.Core.Management.Models;

namespace OCPP.Core.Management.Services
{
    public interface IEmailSender
    {
        Task<EmailSendResult> SendAsync(string toEmail, string subject, string body, IEnumerable<EmailAttachment> attachments = null, Dictionary<string, string> headers = null);
    }

    public class EmailSendResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
    }

    public class EmailSender : IEmailSender
    {
        private readonly EmailSettings _settings;
        private readonly ILogger<EmailSender> _logger;

        public EmailSender(IOptions<EmailSettings> settings, ILogger<EmailSender> logger)
        {
            _settings = settings?.Value ?? new EmailSettings();
            _logger = logger;
        }

        public async Task<EmailSendResult> SendAsync(string toEmail, string subject, string body, IEnumerable<EmailAttachment> attachments = null, Dictionary<string, string> headers = null)
        {
            if (!_settings.EnableOwnerReportEmails)
            {
                return new EmailSendResult { Success = false, Error = "Email sending disabled." };
            }

            if (string.IsNullOrWhiteSpace(_settings?.Smtp?.Host) || string.IsNullOrWhiteSpace(_settings.FromAddress))
            {
                return new EmailSendResult { Success = false, Error = "Email settings are not configured." };
            }

            try
            {
                using var message = new MailMessage
                {
                    From = new MailAddress(_settings.FromAddress, _settings.FromName),
                    Subject = subject,
                    Body = body,
                    IsBodyHtml = false
                };

                if (!string.IsNullOrWhiteSpace(_settings.ReplyToAddress))
                {
                    message.ReplyToList.Add(new MailAddress(_settings.ReplyToAddress));
                }

                message.To.Add(new MailAddress(toEmail));

                if (_settings.Headers != null)
                {
                    foreach (var header in _settings.Headers)
                    {
                        if (message.Headers[header.Key] == null)
                        {
                            message.Headers.Add(header.Key, header.Value);
                        }
                    }
                }

                if (headers != null)
                {
                    foreach (var header in headers)
                    {
                        if (message.Headers[header.Key] == null)
                        {
                            message.Headers.Add(header.Key, header.Value);
                        }
                    }
                }

                if (attachments != null)
                {
                    foreach (var att in attachments)
                    {
                        message.Attachments.Add(new Attachment(new System.IO.MemoryStream(att.Content), att.FileName, att.ContentType));
                    }
                }

                using var client = new SmtpClient(_settings.Smtp.Host, _settings.Smtp.Port)
                {
                    EnableSsl = _settings.Smtp.UseStartTls,
                    Credentials = string.IsNullOrWhiteSpace(_settings.Smtp.Username)
                        ? CredentialCache.DefaultNetworkCredentials
                        : new NetworkCredential(_settings.Smtp.Username, _settings.Smtp.Password)
                };

                await client.SendMailAsync(message);
                return new EmailSendResult { Success = true };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "EmailSender: Error sending email to {To}", toEmail);
                return new EmailSendResult { Success = false, Error = ex.Message };
            }
        }
    }
}
