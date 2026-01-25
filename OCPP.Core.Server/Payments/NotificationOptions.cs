namespace OCPP.Core.Server.Payments
{
    public class NotificationOptions
    {
        public bool EnableCustomerEmails { get; set; } = false;
        public string FromAddress { get; set; }
        public string FromName { get; set; }
        public string ReplyToAddress { get; set; }
        public string BccAddress { get; set; }
        public SmtpOptions Smtp { get; set; } = new SmtpOptions();
    }

    public class SmtpOptions
    {
        public string Host { get; set; }
        public int Port { get; set; } = 587;
        public bool UseStartTls { get; set; } = true;
        public string Username { get; set; }
        public string Password { get; set; }
    }
}
