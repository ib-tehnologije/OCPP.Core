using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OCPP.Core.Database;
using OCPP.Core.Server.Options;

namespace OCPP.Core.Server.Services
{
    public class MonthlyOwnerReportService : BackgroundService
    {
        private readonly ILogger<MonthlyOwnerReportService> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IEmailSender _emailSender;
        private readonly IOptionsMonitor<OwnerReportOptions> _reportOptions;
        private readonly IOptionsMonitor<EmailOptions> _emailOptions;

        public MonthlyOwnerReportService(
            ILogger<MonthlyOwnerReportService> logger,
            IServiceScopeFactory scopeFactory,
            IEmailSender emailSender,
            IOptionsMonitor<OwnerReportOptions> reportOptions,
            IOptionsMonitor<EmailOptions> emailOptions)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;
            _emailSender = emailSender;
            _reportOptions = reportOptions;
            _emailOptions = emailOptions;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogError(ex, "Error while generating monthly owner reports");
                }

                var intervalMinutes = Math.Max(15, _reportOptions.CurrentValue.CheckIntervalMinutes);
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), stoppingToken).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    // Ignore cancellation during delay
                }
            }
        }

        private async Task RunAsync(CancellationToken stoppingToken)
        {
            var reportOptions = _reportOptions.CurrentValue;
            var emailOptions = _emailOptions.CurrentValue;

            if (!reportOptions.Enabled)
            {
                _logger.LogDebug("Monthly owner reporting disabled.");
                return;
            }

            if (!emailOptions.Enabled)
            {
                _logger.LogWarning("Monthly owner reporting enabled but e-mail delivery is disabled.");
                return;
            }

            var now = DateTime.UtcNow;
            var sendDay = Math.Clamp(reportOptions.SendDayOfMonth, 1, DateTime.DaysInMonth(now.Year, now.Month));
            var sendTime = reportOptions.GetSendTime();

            if (now.Day < sendDay || (now.Day == sendDay && now.TimeOfDay < sendTime))
            {
                _logger.LogDebug("Waiting until configured send time ({SendDay}/{SendTime}) to dispatch owner reports.", sendDay, sendTime);
                return;
            }

            var periodStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-1);
            var periodEnd = periodStart.AddMonths(1);

            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<OCPPCoreContext>();

            var owners = await context.ChargeStationOwners
                .Include(o => o.ChargePoints)
                .ToListAsync(stoppingToken)
                .ConfigureAwait(false);

            foreach (var owner in owners)
            {
                if (owner.LastReportYear == periodStart.Year && owner.LastReportMonth == periodStart.Month)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(owner.Email))
                {
                    _logger.LogWarning("Skipping owner {OwnerId} because no e-mail address is configured.", owner.OwnerId);
                    continue;
                }

                var summaries = await context.Transactions
                    .Where(t => t.ChargePoint.OwnerId == owner.OwnerId &&
                                t.StopTime.HasValue &&
                                t.StopTime.Value >= periodStart &&
                                t.StopTime.Value < periodEnd &&
                                t.MeterStop.HasValue)
                    .GroupBy(t => new { t.ChargePointId, t.ChargePoint.Name, t.ChargePoint.PricePerKwh })
                    .Select(g => new OwnerReportLine
                    {
                        ChargePointId = g.Key.ChargePointId,
                        ChargePointName = g.Key.Name,
                        PricePerKwh = g.Key.PricePerKwh,
                        Energy = g.Sum(t => Math.Max(0, t.MeterStop.Value - t.MeterStart))
                    })
                    .ToListAsync(stoppingToken)
                    .ConfigureAwait(false);

                var culture = CultureInfo.InvariantCulture;
                var subject = string.Format(culture, reportOptions.EmailSubject ?? "Monthly charging report - {0:MMMM yyyy}", periodStart);

                var totalEnergy = summaries.Sum(s => s.Energy);
                decimal totalRevenue = summaries.Sum(s => (decimal)s.Energy * (s.PricePerKwh ?? 0m));
                var provisionPercentage = Math.Clamp(owner.ProvisionPercentage, 0m, 100m);
                decimal provisionAmount = totalRevenue * (provisionPercentage / 100m);
                decimal payoutAmount = totalRevenue - provisionAmount;

                var builder = new StringBuilder();
                builder.AppendLine(string.Format(culture, "Charging summary for {0:MMMM yyyy}", periodStart));
                builder.AppendLine();

                if (summaries.Count == 0)
                {
                    builder.AppendLine("No completed transactions were recorded for this period.");
                }
                else
                {
                    builder.AppendLine("Charge point;Energy (kWh);Price per kWh;Amount");
                    foreach (var summary in summaries)
                    {
                        decimal amount = (decimal)summary.Energy * (summary.PricePerKwh ?? 0m);
                        builder.AppendLine(string.Join(';',
                            (summary.ChargePointName ?? summary.ChargePointId) ?? "-",
                            summary.Energy.ToString("0.##", culture),
                            (summary.PricePerKwh ?? 0m).ToString("0.####", culture),
                            amount.ToString("0.00", culture)));
                    }

                    builder.AppendLine();
                    builder.AppendLine(string.Format(culture, "Total energy: {0:0.##} kWh", totalEnergy));
                    builder.AppendLine(string.Format(culture, "Total amount: {0:0.00}", totalRevenue));
                }

                builder.AppendLine(string.Format(culture, "Provision ({0:0.##}%): {1:0.00}", provisionPercentage, provisionAmount));
                builder.AppendLine(string.Format(culture, "Amount to invoice: {0:0.00}", payoutAmount));

                try
                {
                    await _emailSender.SendEmailAsync(owner.Email, subject, builder.ToString(), stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send monthly report to owner {OwnerId}", owner.OwnerId);
                    continue;
                }

                owner.LastReportYear = periodStart.Year;
                owner.LastReportMonth = periodStart.Month;
                owner.LastReportSentAt = DateTime.UtcNow;

                await context.SaveChangesAsync(stoppingToken).ConfigureAwait(false);

                _logger.LogInformation("Monthly report sent to owner {OwnerId}", owner.OwnerId);
            }
        }

        private class OwnerReportLine
        {
            public string ChargePointId { get; set; }

            public string ChargePointName { get; set; }

            public decimal? PricePerKwh { get; set; }

            public double Energy { get; set; }
        }
    }
}
