/*
 * OCPP.Core - https://github.com/dallmann-consulting/OCPP.Core
 */

namespace OCPP.Core.Server.Payments
{
    public class StripeOptions
    {
        public bool Enabled { get; set; } = false;
        public string ApiKey { get; set; }
        public string WebhookSecret { get; set; }
        public string Currency { get; set; } = "eur";
        public string ReturnBaseUrl { get; set; }
        public string ProductName { get; set; } = "EV charging session";
        public bool AllowInsecureWebhooks { get; set; } = false;
    }
}
