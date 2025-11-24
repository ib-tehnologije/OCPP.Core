namespace OCPP.Core.Server.Options
{
    public class EmailOptions
    {
        public bool Enabled { get; set; }

        public string Host { get; set; }

        public int Port { get; set; } = 25;

        public bool UseSsl { get; set; }

        public string Username { get; set; }

        public string Password { get; set; }

        public string FromAddress { get; set; }

        public string FromName { get; set; }
    }
}
