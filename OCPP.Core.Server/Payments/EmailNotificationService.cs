using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OCPP.Core.Database;
using Stripe.Checkout;

namespace OCPP.Core.Server.Payments
{
    public interface IEmailNotificationService
    {
        void SendPaymentAuthorized(string toEmail, ChargePaymentReservation reservation, Session session, string statusUrl);
        void SendChargingCompleted(string toEmail, ChargePaymentReservation reservation, Transaction transaction, ChargePoint chargePoint, string statusUrl);
        void SendIdleFeeWarning(string toEmail, ChargePaymentReservation reservation, Transaction transaction, ChargePoint chargePoint, DateTime idleFeeStartsAtUtc, TimeSpan remainingUntilIdleFee, string statusUrl);
        void SendSessionReceipt(string toEmail, ChargePaymentReservation reservation, Transaction transaction, ChargePoint chargePoint, string statusUrl, string invoiceNumber, string invoiceUrl);
        void SendR1InvoiceRequested(string toEmail, ChargePaymentReservation reservation, ChargePoint chargePoint, string statusUrl, string buyerCompanyName, string buyerOib);
        void SendR1InvoiceReady(string toEmail, ChargePaymentReservation reservation, Transaction transaction, ChargePoint chargePoint, string statusUrl, string invoiceNumber, string invoiceUrl);
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

        public void SendPaymentAuthorized(string toEmail, ChargePaymentReservation reservation, Session session, string statusUrl)
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
                string.IsNullOrWhiteSpace(statusUrl) ? null : "Open session",
                statusUrl,
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

        public void SendSessionReceipt(string toEmail, ChargePaymentReservation reservation, Transaction transaction, ChargePoint chargePoint, string statusUrl, string invoiceNumber, string invoiceUrl)
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
            if (!string.IsNullOrWhiteSpace(invoiceUrl))
            {
                callToActionText = "Open invoice";
                callToActionUrl = invoiceUrl;
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

        public void SendR1InvoiceReady(string toEmail, ChargePaymentReservation reservation, Transaction transaction, ChargePoint chargePoint, string statusUrl, string invoiceNumber, string invoiceUrl)
        {
            decimal totalCharged = ResolveTotalCharged(reservation, transaction);
            var details = new List<(string Label, string Value)>
            {
                ("Charge point", BuildChargePointLabel(reservation, chargePoint)),
                ("Transaction", transaction?.TransactionId.ToString(CultureInfo.InvariantCulture)),
                ("Total charged", FormatMoney(totalCharged, reservation?.Currency)),
                ("Invoice", invoiceNumber)
            };

            string callToActionText = "Open session";
            string callToActionUrl = statusUrl;
            if (!string.IsNullOrWhiteSpace(invoiceUrl))
            {
                callToActionText = "Open R1 invoice";
                callToActionUrl = invoiceUrl;
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

            string htmlBody = BuildHtmlBody(title, intro, details, actionText, actionUrl, footerText, eventName);
            if (TryWriteEmailSink(toEmail, subject, htmlBody, actionText, actionUrl, reservationId, eventName))
            {
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
                    Body = htmlBody,
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

        private bool TryWriteEmailSink(
            string toEmail,
            string subject,
            string htmlBody,
            string actionText,
            string actionUrl,
            Guid? reservationId,
            string eventName)
        {
            if (string.IsNullOrWhiteSpace(_options?.SinkDirectory))
            {
                return false;
            }

            try
            {
                Directory.CreateDirectory(_options.SinkDirectory);

                string safeEventName = new string((eventName ?? "Email")
                    .Where(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_')
                    .ToArray());
                if (string.IsNullOrWhiteSpace(safeEventName))
                {
                    safeEventName = "Email";
                }

                string reservationPart = reservationId.HasValue ? reservationId.Value.ToString("N") : "noreservation";
                string fileName = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{safeEventName}-{reservationPart}.json";
                string filePath = Path.Combine(_options.SinkDirectory, fileName);

                var payload = new
                {
                    eventName,
                    toEmail,
                    subject,
                    reservationId,
                    actionText,
                    actionUrl,
                    createdAtUtc = DateTime.UtcNow,
                    htmlBody
                };

                File.WriteAllText(filePath, JsonSerializer.Serialize(payload, new JsonSerializerOptions
                {
                    WriteIndented = true
                }));

                _logger.LogInformation(
                    "Wrote {EventName} email to sink {FilePath} reservation={ReservationId}",
                    eventName,
                    filePath,
                    reservationId);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to write {EventName} email sink reservation={ReservationId}", eventName, reservationId);
                return false;
            }
        }

        private static string BuildHtmlBody(
            string title,
            string intro,
            IEnumerable<(string Label, string Value)> details,
            string actionText,
            string actionUrl,
            string footerText,
            string eventName)
        {
            var template = ResolveTemplate(eventName, title, intro, footerText);
            var factRows = string.Empty;
            if (details != null)
            {
                var rows = details
                    .Where(d => !string.IsNullOrWhiteSpace(d.Value))
                    .Select(d =>
                        "<tr>" +
                        $"<td style=\"padding:14px 16px;border-bottom:1px solid #e2e8f0;font-size:13px;color:#64748b;font-family:system-ui,-apple-system,'Segoe UI',Arial,sans-serif;width:180px;vertical-align:top;\">{FormatDetailLabel(d.Label)}</td>" +
                        $"<td style=\"padding:14px 16px;border-bottom:1px solid #e2e8f0;font-size:13px;color:#0f172a;font-weight:600;font-family:system-ui,-apple-system,'Segoe UI',Arial,sans-serif;vertical-align:top;\">{Encode(d.Value)}</td>" +
                        "</tr>");
                factRows = string.Join(string.Empty, rows);
            }

            var actionMarkup = string.Empty;
            if (!string.IsNullOrWhiteSpace(actionText) && !string.IsNullOrWhiteSpace(actionUrl))
            {
                string localizedActionText = LocalizeActionText(actionText);
                actionMarkup =
                    "<tr><td style=\"padding:24px 32px 0 32px;\" align=\"center\">" +
                    "<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\"><tr>" +
                    "<td style=\"background-color:#059669;border-radius:8px;\">" +
                    $"<a href=\"{EncodeAttribute(actionUrl)}\" target=\"_blank\" style=\"display:inline-block;padding:14px 32px;font-size:14px;font-weight:600;color:#ffffff;font-family:system-ui,-apple-system,'Segoe UI',Arial,sans-serif;text-decoration:none;letter-spacing:0.2px;\">{localizedActionText} &rarr;</a>" +
                    "</td></tr></table></td></tr>";
            }

            var noteText = template.Note;
            var footerMarkup = string.IsNullOrWhiteSpace(noteText)
                ? string.Empty
                : "<tr><td style=\"padding:20px 32px 0 32px;\">" +
                  $"<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" width=\"100%\" style=\"background-color:{template.NoteBackground};border:1px solid {template.NoteBorder};border-radius:8px;\">" +
                  $"<tr><td style=\"padding:12px 16px;font-size:12px;color:{template.NoteColor};font-family:system-ui,-apple-system,'Segoe UI',Arial,sans-serif;line-height:1.5;\">{noteText}</td></tr>" +
                  "</table></td></tr>";

            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html><html lang=\"hr\" xmlns=\"http://www.w3.org/1999/xhtml\"><head>");
            sb.Append("<meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\"><meta http-equiv=\"X-UA-Compatible\" content=\"IE=edge\">");
            sb.AppendFormat("<title>{0}</title>", Encode(template.TitleText));
            sb.Append("</head>");
            sb.Append("<body style=\"margin:0;padding:0;background-color:#f4f6f9;font-family:system-ui,-apple-system,'Segoe UI',Arial,sans-serif;-webkit-font-smoothing:antialiased;\">");
            sb.Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" width=\"100%\" style=\"background-color:#f4f6f9;\"><tr><td style=\"padding:32px 16px;\" align=\"center\">");
            sb.Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" width=\"600\" style=\"max-width:600px;background-color:#ffffff;border:1px solid #e5e7eb;border-radius:12px;overflow:hidden;box-shadow:0 1px 3px rgba(0,0,0,0.06),0 1px 2px rgba(0,0,0,0.04);\">");
            sb.Append("<tr><td style=\"background-color:#ffffff;padding:28px 32px 20px;text-align:center;border-bottom:3px solid #059669;\">");
            sb.Append("<img src=\"https://evcharge.hr/img/evcharge-logo-light.png\" alt=\"EV.Charge - powered by Tehnoline Telekom\" width=\"240\" style=\"display:block;margin:0 auto;max-width:240px;height:auto;border:0;\">");
            sb.Append("</td></tr>");
            sb.Append("<tr><td style=\"padding:32px 32px 0 32px;\" align=\"center\"><table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\"><tr>");
            sb.AppendFormat("<td style=\"background-color:{0};border:1px solid {1};border-radius:20px;padding:6px 16px;font-size:12px;font-weight:600;color:{2};font-family:system-ui,-apple-system,'Segoe UI',Arial,sans-serif;letter-spacing:0.3px;text-transform:uppercase;\">{3}</td>",
                template.BadgeBackground,
                template.BadgeBorder,
                template.BadgeColor,
                template.BadgeHtml);
            sb.Append("</tr></table></td></tr>");
            sb.Append("<tr><td style=\"padding:20px 32px 0 32px;\">");
            sb.AppendFormat("<h1 style=\"margin:0 0 8px 0;font-size:22px;font-weight:700;color:#0f172a;font-family:system-ui,-apple-system,'Segoe UI',Arial,sans-serif;text-align:center;line-height:1.3;\"><span style=\"color:#0f172a;\">{0}</span> <span style=\"color:#94a3b8;font-weight:400;\">/ {1}</span></h1>",
                Encode(template.TitleHr),
                Encode(template.TitleEn));
            sb.AppendFormat("<p style=\"margin:0 0 24px 0;font-size:15px;color:#475569;font-family:system-ui,-apple-system,'Segoe UI',Arial,sans-serif;text-align:center;line-height:1.6;\">{0} <span style=\"color:#94a3b8;\">/ {1}</span></p>",
                Encode(template.IntroHr),
                Encode(template.IntroEn));
            sb.Append("</td></tr>");

            if (!string.IsNullOrWhiteSpace(factRows))
            {
                sb.Append("<tr><td style=\"padding:0 32px;\">");
                sb.Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" width=\"100%\" style=\"background-color:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;overflow:hidden;\">");
                sb.Append(factRows);
                sb.Append("</table></td></tr>");
            }

            sb.Append(actionMarkup);
            sb.Append(footerMarkup);
            sb.Append("<tr><td style=\"padding:28px 32px 0 32px;\"><table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" width=\"100%\"><tr><td style=\"border-top:1px solid #e5e7eb;\"></td></tr></table></td></tr>");
            sb.Append("<tr><td style=\"padding:20px 32px 28px 32px;text-align:center;\">");
            sb.Append("<p style=\"margin:0 0 4px 0;font-size:11px;color:#94a3b8;font-family:system-ui,-apple-system,'Segoe UI',Arial,sans-serif;line-height:1.6;\">Tehnoline Telekom d.o.o. | Bože Gumpca 49, 52100 Pula</p>");
            sb.Append("<p style=\"margin:0 0 4px 0;font-size:11px;color:#94a3b8;font-family:system-ui,-apple-system,'Segoe UI',Arial,sans-serif;line-height:1.6;\">ev@tehnoline.hr | +385 52 355 050</p>");
            sb.Append("<p style=\"margin:0;font-size:11px;font-family:system-ui,-apple-system,'Segoe UI',Arial,sans-serif;line-height:1.6;\"><a href=\"https://evcharge.hr\" style=\"color:#059669;text-decoration:none;font-weight:600;\">evcharge.hr</a></p>");
            sb.Append("</td></tr></table>");
            sb.Append("</td></tr></table>");
            sb.Append("</body></html>");
            return sb.ToString();
        }

        private static EmailTemplateMetadata ResolveTemplate(string eventName, string fallbackTitle, string fallbackIntro, string fallbackNote)
        {
            switch (eventName)
            {
                case "PaymentAuthorized":
                    return new EmailTemplateMetadata(
                        "Plaćanje autorizirano",
                        "Payment Authorized",
                        "Vaša kartična rezervacija je potvrđena. Punjenje može započeti.",
                        "Your card hold is confirmed. Charging can start now.",
                        "&#10003; Autorizirano",
                        "#ecfdf5",
                        "#a7f3d0",
                        "#059669",
                        "&#9888;&#65039; Ako niste vi pokrenuli ovu sesiju, odgovorite na ovaj email. <span style=\"color:#b45309;\">/ If you did not initiate this session, reply to this email.</span>",
                        "#fffbeb",
                        "#fde68a",
                        "#92400e");
                case "ChargingCompleted":
                    return new EmailTemplateMetadata(
                        "Punjenje završeno",
                        "Charging Completed",
                        "Punjenje je završeno. Molimo odspojite vozilo.",
                        "Charging is finished. Please disconnect your vehicle.",
                        "&#9889; Punjenje završeno",
                        "#ecfdf5",
                        "#a7f3d0",
                        "#059669");
                case "IdleFeeWarning":
                    return new EmailTemplateMetadata(
                        "Upozorenje: naknada za zadržavanje",
                        "Idle Fee Warning",
                        "Punjenje je završeno, ali vozilo je još priključeno.",
                        "Charging is complete but your vehicle is still connected.",
                        "&#9888; Naknada uskoro",
                        "#fef2f2",
                        "#fecaca",
                        "#dc2626",
                        "Odspajanje prije početka naplate pomaže izbjeći dodatne troškove. <span style=\"color:#b91c1c;\">/ Disconnecting before idle billing starts helps avoid extra charges.</span>",
                        "#fef2f2",
                        "#fecaca",
                        "#991b1b");
                case "SessionReceipt":
                    return new EmailTemplateMetadata(
                        "Račun za punjenje",
                        "Session Receipt",
                        "Pregled vašeg punjenja.",
                        "Here is your charging summary.",
                        "&#128451; Račun",
                        "#f0f9ff",
                        "#bae6fd",
                        "#0284c7",
                        "Povrat razlike na karticu ovisi o banci i može potrajati. <span style=\"color:#94a3b8;\">/ Card refunds are handled by your bank and may take a few days.</span>",
                        "#f8fafc",
                        "#e2e8f0",
                        "#64748b");
                case "R1InvoiceRequested":
                    return new EmailTemplateMetadata(
                        "R1 račun zatražen",
                        "R1 Invoice Requested",
                        "Zatražili ste poslovni (R1) račun.",
                        "You requested a company (R1) invoice.",
                        "&#128203; R1 zatražen",
                        "#faf5ff",
                        "#e9d5ff",
                        "#7c3aed",
                        "Ako neki podatak nedostaje ili ga treba ispraviti, odgovorite prije zaključenja računa. <span style=\"color:#64748b;\">/ If any company data is missing or needs correction, reply before the invoice is finalized.</span>",
                        "#f8fafc",
                        "#e2e8f0",
                        "#64748b");
                case "R1InvoiceReady":
                    return new EmailTemplateMetadata(
                        "R1 račun spreman",
                        "R1 Invoice Ready",
                        "Vaš poslovni račun je dostupan.",
                        "Your company invoice is available.",
                        "&#10003; R1 spreman",
                        "#ecfdf5",
                        "#a7f3d0",
                        "#059669");
                default:
                    return new EmailTemplateMetadata(
                        string.IsNullOrWhiteSpace(fallbackTitle) ? "EV.Charge obavijest" : fallbackTitle,
                        string.IsNullOrWhiteSpace(fallbackTitle) ? "EV.Charge notification" : fallbackTitle,
                        string.IsNullOrWhiteSpace(fallbackIntro) ? "Obavijest o punjenju." : fallbackIntro,
                        string.IsNullOrWhiteSpace(fallbackIntro) ? "Charging notification." : fallbackIntro,
                        "EV.Charge",
                        "#ecfdf5",
                        "#a7f3d0",
                        "#059669",
                        Encode(fallbackNote),
                        "#f8fafc",
                        "#e2e8f0",
                        "#64748b");
            }
        }

        private static string FormatDetailLabel(string label)
        {
            var localized = label switch
            {
                "Reservation" => ("Rezervacija", "Reservation"),
                "Charge point" => ("Punjač", "Charge point"),
                "Authorized hold" => ("Autorizirani iznos", "Authorized hold"),
                "Checkout session" => ("Sesija", "Checkout session"),
                "Energy delivered" => ("Isporučena energija", "Energy delivered"),
                "Charging duration" => ("Trajanje punjenja", "Charging duration"),
                "Total charged" => ("Ukupno naplaćeno", "Total charged"),
                "Idle fee starts" => ("Početak naknade", "Idle fee starts"),
                "Grace remaining" => ("Preostalo", "Grace remaining"),
                "Idle fee rate" => ("Naknada", "Idle fee rate"),
                "Transaction" => ("Transakcija", "Transaction"),
                "Energy" => ("Energija", "Energy"),
                "Energy cost" => ("Cijena energije", "Energy cost"),
                "Session fee" => ("Naknada za sesiju", "Session fee"),
                "Idle fee" => ("Naknada za zadržavanje", "Idle fee"),
                "Refund to card" => ("Povrat na karticu", "Refund to card"),
                "Invoice" => ("Račun", "Invoice"),
                "Company" => ("Tvrtka", "Company"),
                "OIB" => ("OIB", "OIB"),
                _ => (label, label)
            };

            return $"{Encode(localized.Item1)} / <span style=\"color:#94a3b8;\">{Encode(localized.Item2)}</span>";
        }

        private static string LocalizeActionText(string actionText)
        {
            return actionText switch
            {
                "Open invoice" => "Otvori račun / Open invoice",
                "Open R1 invoice" => "Otvori R1 račun / Open R1 invoice",
                "Open session" => "Otvori sesiju / Open session",
                _ => Encode(actionText)
            };
        }

        private sealed class EmailTemplateMetadata
        {
            public EmailTemplateMetadata(
                string titleHr,
                string titleEn,
                string introHr,
                string introEn,
                string badgeHtml,
                string badgeBackground,
                string badgeBorder,
                string badgeColor,
                string note = null,
                string noteBackground = "#f8fafc",
                string noteBorder = "#e2e8f0",
                string noteColor = "#64748b")
            {
                TitleHr = titleHr;
                TitleEn = titleEn;
                IntroHr = introHr;
                IntroEn = introEn;
                BadgeHtml = badgeHtml;
                BadgeBackground = badgeBackground;
                BadgeBorder = badgeBorder;
                BadgeColor = badgeColor;
                Note = note;
                NoteBackground = noteBackground;
                NoteBorder = noteBorder;
                NoteColor = noteColor;
            }

            public string TitleHr { get; }
            public string TitleEn { get; }
            public string IntroHr { get; }
            public string IntroEn { get; }
            public string BadgeHtml { get; }
            public string BadgeBackground { get; }
            public string BadgeBorder { get; }
            public string BadgeColor { get; }
            public string Note { get; }
            public string NoteBackground { get; }
            public string NoteBorder { get; }
            public string NoteColor { get; }
            public string TitleText => $"{TitleHr} / {TitleEn}";
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
