using System.Collections.Generic;

namespace OCPP.Core.Management.Models
{
    public class EmailSettings
    {
        public string FromAddress { get; set; }
        public string FromName { get; set; }
        public string ReplyToAddress { get; set; }
        public bool EnableOwnerReportEmails { get; set; }
        public SmtpSettings Smtp { get; set; } = new SmtpSettings();
        public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>();
    }

    public class SmtpSettings
    {
        public string Host { get; set; }
        public int Port { get; set; } = 587;
        public bool UseStartTls { get; set; } = true;
        public string Username { get; set; }
        public string Password { get; set; }
    }
}
