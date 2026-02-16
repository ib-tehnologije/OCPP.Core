using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OCPP.Core.Database;
using Stripe.Checkout;

namespace OCPP.Core.Server.Payments
{
    public interface IEmailNotificationService
    {
        void SendPaymentAuthorized(string toEmail, ChargePaymentReservation reservation, Session session);
        void SendChargingCompleted(string toEmail, ChargePaymentReservation reservation, Transaction transaction, ChargePoint chargePoint, string statusUrl);
        void SendIdleFeeWarning(string toEmail, ChargePaymentReservation reservation, Transaction transaction, ChargePoint chargePoint, DateTime idleFeeStartsAtUtc, TimeSpan remainingUntilIdleFee, string statusUrl);
        void SendSessionReceipt(string toEmail, ChargePaymentReservation reservation, Transaction transaction, ChargePoint chargePoint, string statusUrl, string invoiceNumber, string invoicePdfUrl);
        void SendR1InvoiceRequested(string toEmail, ChargePaymentReservation reservation, ChargePoint chargePoint, string statusUrl, string buyerCompanyName, string buyerOib);
        void SendR1InvoiceReady(string toEmail, ChargePaymentReservation reservation, Transaction transaction, ChargePoint chargePoint, string statusUrl, string invoicePdfUrl);
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
            var details = new List<(string Label, string Value)>
            {
                ("Reservation", reservation?.ReservationId.ToString()),
                ("Charge point", BuildChargePointLabel(reservation, null)),
                ("Authorized hold", FormatAmountFromCents(reservation?.MaxAmountCents, reservation?.Currency)),
                ("Checkout session", session?.Id)
            };

            SendTemplatedEmail(
                toEmail,
                "Charging payment authorized",
                "Payment authorized",
                "Your card hold is confirmed. Charging can start now.",
                details,
                null,
                null,
                "If you did not initiate this charging session, reply to this email.",
                reservation?.ReservationId,
                "PaymentAuthorized");
        }

        public void SendChargingCompleted(string toEmail, ChargePaymentReservation reservation, Transaction transaction, ChargePoint chargePoint, string statusUrl)
        {
            decimal totalCharged = ResolveTotalCharged(reservation, transaction);
            var details = new List<(string Label, string Value)>
            {
                ("Charge point", BuildChargePointLabel(reservation, chargePoint)),
                ("Energy delivered", transaction != null ? $"{transaction.EnergyKwh:0.###} kWh" : null),
                ("Charging duration", FormatDuration(transaction)),
                ("Total charged", FormatMoney(totalCharged, reservation?.Currency))
            };

            SendTemplatedEmail(
                toEmail,
                "Your vehicle is charged",
                "Charging completed",
                "Charging is finished. Please disconnect your vehicle when convenient.",
                details,
                string.IsNullOrWhiteSpace(statusUrl) ? null : "Open session",
                statusUrl,
                null,
                reservation?.ReservationId,
                "ChargingCompleted");
        }

        public void SendIdleFeeWarning(string toEmail, ChargePaymentReservation reservation, Transaction transaction, ChargePoint chargePoint, DateTime idleFeeStartsAtUtc, TimeSpan remainingUntilIdleFee, string statusUrl)
        {
            int minutesLeft = Math.Max(1, (int)Math.Ceiling(Math.Max(0, remainingUntilIdleFee.TotalMinutes)));
            var details = new List<(string Label, string Value)>
            {
                ("Charge point", BuildChargePointLabel(reservation, chargePoint)),
                ("Idle fee starts", FormatUtc(idleFeeStartsAtUtc)),
                ("Grace remaining", $"{minutesLeft} min"),
                ("Idle fee rate", reservation != null ? $"{reservation.UsageFeePerMinute:0.00} / min {NormalizeCurrency(reservation.Currency)}" : null)
            };

            SendTemplatedEmail(
                toEmail,
                $"Idle fee starts in {minutesLeft} min",
                "Idle fee warning",
                "Charging appears complete while the connector is still occupied.",
                details,
                string.IsNullOrWhiteSpace(statusUrl) ? null : "Open session",
                statusUrl,
                "Disconnecting before idle billing starts helps avoid extra charges.",
                reservation?.ReservationId,
                "IdleFeeWarning");
        }

        public void SendSessionReceipt(string toEmail, ChargePaymentReservation reservation, Transaction transaction, ChargePoint chargePoint, string statusUrl, string invoiceNumber, string invoicePdfUrl)
        {
            decimal authorizedHold = ConvertToAmount(reservation?.MaxAmountCents ?? 0);
            decimal totalCharged = ResolveTotalCharged(reservation, transaction);
            decimal estimatedRefund = Math.Max(0m, authorizedHold - totalCharged);

            var details = new List<(string Label, string Value)>
            {
                ("Charge point", BuildChargePointLabel(reservation, chargePoint)),
                ("Transaction", transaction?.TransactionId.ToString(CultureInfo.InvariantCulture)),
                ("Energy", transaction != null ? $"{transaction.EnergyKwh:0.###} kWh" : null),
                ("Energy cost", transaction != null ? FormatMoney(transaction.EnergyCost, reservation?.Currency) : null),
                ("Session fee", transaction != null ? FormatMoney(transaction.UserSessionFeeAmount, reservation?.Currency) : null),
                ("Idle fee", transaction != null ? FormatMoney(transaction.IdleUsageFeeAmount, reservation?.Currency) : null),
                ("Total charged", FormatMoney(totalCharged, reservation?.Currency)),
                ("Authorized hold", FormatMoney(authorizedHold, reservation?.Currency)),
                ("Refund to card", FormatMoney(estimatedRefund, reservation?.Currency)),
                ("Invoice", invoiceNumber)
            };

            string callToActionText = "Open session";
            string callToActionUrl = statusUrl;
            if (!string.IsNullOrWhiteSpace(invoicePdfUrl))
            {
                callToActionText = "Download invoice PDF";
                callToActionUrl = invoicePdfUrl;
            }

            SendTemplatedEmail(
                toEmail,
                $"Charging receipt #{transaction?.TransactionId.ToString(CultureInfo.InvariantCulture) ?? reservation?.ReservationId.ToString() ?? "session"}",
                "Session closed",
                "Here is your charging summary.",
                details,
                callToActionText,
                callToActionUrl,
                "Card refunds are handled by your bank and can take a short time to appear.",
                reservation?.ReservationId,
                "SessionReceipt");
        }

        public void SendR1InvoiceRequested(string toEmail, ChargePaymentReservation reservation, ChargePoint chargePoint, string statusUrl, string buyerCompanyName, string buyerOib)
        {
            var details = new List<(string Label, string Value)>
            {
                ("Charge point", BuildChargePointLabel(reservation, chargePoint)),
                ("Reservation", reservation?.ReservationId.ToString()),
                ("Company", buyerCompanyName),
                ("OIB", buyerOib)
            };

            SendTemplatedEmail(
                toEmail,
                "Enter details for your R1 invoice",
                "R1 invoice requested",
                "You requested a company (R1) invoice for this charging session.",
                details,
                string.IsNullOrWhiteSpace(statusUrl) ? null : "Open session",
                statusUrl,
                "If any company data is missing or needs correction, reply before the session is finalized.",
                reservation?.ReservationId,
                "R1InvoiceRequested");
        }

        public void SendR1InvoiceReady(string toEmail, ChargePaymentReservation reservation, Transaction transaction, ChargePoint chargePoint, string statusUrl, string invoicePdfUrl)
        {
            decimal totalCharged = ResolveTotalCharged(reservation, transaction);
            var details = new List<(string Label, string Value)>
            {
                ("Charge point", BuildChargePointLabel(reservation, chargePoint)),
                ("Transaction", transaction?.TransactionId.ToString(CultureInfo.InvariantCulture)),
                ("Total charged", FormatMoney(totalCharged, reservation?.Currency))
            };

            string callToActionText = "Open session";
            string callToActionUrl = statusUrl;
            if (!string.IsNullOrWhiteSpace(invoicePdfUrl))
            {
                callToActionText = "Download R1 invoice";
                callToActionUrl = invoicePdfUrl;
            }

            SendTemplatedEmail(
                toEmail,
                "Your R1 invoice is ready",
                "R1 invoice ready",
                "Your company invoice is available.",
                details,
                callToActionText,
                callToActionUrl,
                null,
                reservation?.ReservationId,
                "R1InvoiceReady");
        }

        private void SendTemplatedEmail(
            string toEmail,
            string subject,
            string title,
            string intro,
            IReadOnlyCollection<(string Label, string Value)> details,
            string actionText,
            string actionUrl,
            string footerText,
            Guid? reservationId,
            string eventName)
        {
            if (!_options.EnableCustomerEmails)
            {
                _logger.LogDebug("Email notifications disabled; skipping {EventName} email reservation={ReservationId}", eventName, reservationId);
                return;
            }

            if (string.IsNullOrWhiteSpace(toEmail))
            {
                _logger.LogDebug("No recipient email available for {EventName}; reservation={ReservationId}", eventName, reservationId);
                return;
            }

            if (string.IsNullOrWhiteSpace(_options?.Smtp?.Host) || string.IsNullOrWhiteSpace(_options.FromAddress))
            {
                _logger.LogWarning("Email notification skipped for {EventName} because SMTP configuration is incomplete.", eventName);
                return;
            }

            try
            {
                using var message = new MailMessage
                {
                    From = new MailAddress(_options.FromAddress, _options.FromName),
                    Subject = subject,
                    Body = BuildHtmlBody(title, intro, details, actionText, actionUrl, footerText),
                    IsBodyHtml = true
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
                _logger.LogInformation("Sent {EventName} email to {Email} reservation={ReservationId}", eventName, toEmail, reservationId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send {EventName} email to {Email} reservation={ReservationId}", eventName, toEmail, reservationId);
            }
        }

        private static string BuildHtmlBody(
            string title,
            string intro,
            IEnumerable<(string Label, string Value)> details,
            string actionText,
            string actionUrl,
            string footerText)
        {
            var factRows = string.Empty;
            if (details != null)
            {
                var rows = details
                    .Where(d => !string.IsNullOrWhiteSpace(d.Value))
                    .Select(d =>
                        $"<tr><td style=\"padding:6px 0;color:#6b7280;font-size:13px;width:160px;vertical-align:top;\">{Encode(d.Label)}</td>" +
                        $"<td style=\"padding:6px 0;color:#111827;font-size:13px;vertical-align:top;\">{Encode(d.Value)}</td></tr>");
                factRows = string.Join(string.Empty, rows);
            }

            var actionMarkup = string.Empty;
            if (!string.IsNullOrWhiteSpace(actionText) && !string.IsNullOrWhiteSpace(actionUrl))
            {
                actionMarkup =
                    $"<p style=\"margin:20px 0 0;\"><a href=\"{EncodeAttribute(actionUrl)}\" " +
                    "style=\"display:inline-block;background:#0f4c81;color:#ffffff;text-decoration:none;padding:10px 16px;" +
                    "border-radius:6px;font-size:13px;font-weight:600;\">" +
                    $"{Encode(actionText)}</a></p>";
            }

            var footerMarkup = string.IsNullOrWhiteSpace(footerText)
                ? string.Empty
                : $"<p style=\"margin:16px 0 0;color:#6b7280;font-size:12px;line-height:1.5;\">{Encode(footerText)}</p>";

            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html><html><body style=\"margin:0;padding:0;background:#f3f4f6;font-family:Segoe UI,Arial,sans-serif;color:#111827;\">");
            sb.Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" width=\"100%\" style=\"padding:24px 12px;\">");
            sb.Append("<tr><td align=\"center\">");
            sb.Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" width=\"100%\" style=\"max-width:640px;background:#ffffff;border:1px solid #e5e7eb;border-radius:10px;overflow:hidden;\">");
            sb.Append("<tr><td style=\"background:#0f4c81;padding:18px 24px;color:#ffffff;font-size:16px;font-weight:600;\">OCPP Core Charging</td></tr>");
            sb.Append("<tr><td style=\"padding:24px;\">");
            sb.AppendFormat("<h2 style=\"margin:0 0 10px;font-size:20px;line-height:1.3;color:#111827;\">{0}</h2>", Encode(title));
            if (!string.IsNullOrWhiteSpace(intro))
            {
                sb.AppendFormat("<p style=\"margin:0 0 16px;font-size:14px;color:#374151;line-height:1.6;\">{0}</p>", Encode(intro));
            }

            if (!string.IsNullOrWhiteSpace(factRows))
            {
                sb.Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" width=\"100%\" style=\"border-top:1px solid #e5e7eb;padding-top:8px;\">");
                sb.Append(factRows);
                sb.Append("</table>");
            }

            sb.Append(actionMarkup);
            sb.Append(footerMarkup);
            sb.Append("</td></tr>");
            sb.Append("<tr><td style=\"padding:14px 24px;border-top:1px solid #e5e7eb;color:#9ca3af;font-size:11px;\">This is an automated notification.</td></tr>");
            sb.Append("</table>");
            sb.Append("</td></tr></table>");
            sb.Append("</body></html>");
            return sb.ToString();
        }

        private static string BuildChargePointLabel(ChargePaymentReservation reservation, ChargePoint chargePoint)
        {
            string chargePointLabel = chargePoint?.Name;
            if (string.IsNullOrWhiteSpace(chargePointLabel))
            {
                chargePointLabel = reservation?.ChargePointId;
            }

            if (string.IsNullOrWhiteSpace(chargePointLabel))
            {
                chargePointLabel = "Unknown charge point";
            }

            var connectorId = reservation?.ConnectorId ?? 0;
            return connectorId > 0 ? $"{chargePointLabel} / Connector {connectorId}" : chargePointLabel;
        }

        private static decimal ResolveTotalCharged(ChargePaymentReservation reservation, Transaction transaction)
        {
            if (reservation?.CapturedAmountCents.HasValue == true)
            {
                return ConvertToAmount(reservation.CapturedAmountCents.Value);
            }

            if (transaction == null)
            {
                return 0m;
            }

            return Math.Round(
                transaction.EnergyCost + transaction.UserSessionFeeAmount + transaction.UsageFeeAmount,
                2,
                MidpointRounding.AwayFromZero);
        }

        private static decimal ConvertToAmount(long cents) =>
            Math.Round(cents / 100m, 2, MidpointRounding.AwayFromZero);

        private static string FormatAmountFromCents(long? cents, string currency)
        {
            if (!cents.HasValue) return null;
            return FormatMoney(ConvertToAmount(cents.Value), currency);
        }

        private static string FormatMoney(decimal amount, string currency)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0:0.00} {1}",
                amount,
                NormalizeCurrency(currency));
        }

        private static string NormalizeCurrency(string currency)
        {
            if (string.IsNullOrWhiteSpace(currency))
            {
                return "EUR";
            }

            return currency.ToUpperInvariant();
        }

        private static string FormatDuration(Transaction transaction)
        {
            if (transaction == null || transaction.StartTime == default)
            {
                return null;
            }

            var stop = transaction.StopTime ?? DateTime.UtcNow;
            if (stop <= transaction.StartTime)
            {
                return "00:00:00";
            }

            var duration = stop - transaction.StartTime;
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0:00}:{1:00}:{2:00}",
                (int)duration.TotalHours,
                duration.Minutes,
                duration.Seconds);
        }

        private static string FormatUtc(DateTime utcValue)
        {
            return utcValue.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
        }

        private static string Encode(string value) => WebUtility.HtmlEncode(value ?? string.Empty);

        private static string EncodeAttribute(string value) => WebUtility.HtmlEncode(value ?? string.Empty);
    }
}
