namespace OCPP.Core.Management.Models
{
    public class OwnerReportScheduleSettings
    {
        public bool Enabled { get; set; } = false;
        public string Cron { get; set; } = "0 6 1 * *"; // default: 06:00 on day 1 each month
        public bool UsePreviousMonth { get; set; } = true;
        public string SendTestTo { get; set; }
    }
}
