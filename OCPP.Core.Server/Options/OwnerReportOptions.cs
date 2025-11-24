using System;

namespace OCPP.Core.Server.Options
{
    public class OwnerReportOptions
    {
        public bool Enabled { get; set; }

        public int SendDayOfMonth { get; set; } = 1;

        /// <summary>
        /// Time of day in HH:mm (UTC) when the report should be sent.
        /// </summary>
        public string SendTimeUtc { get; set; } = "02:00";

        public int CheckIntervalMinutes { get; set; } = 60;

        public string EmailSubject { get; set; } = "Monthly charging report - {0:MMMM yyyy}";

        internal TimeSpan GetSendTime()
        {
            if (TimeSpan.TryParse(SendTimeUtc, out var time))
            {
                return time;
            }

            return TimeSpan.Zero;
        }
    }
}
