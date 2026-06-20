using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClosedXML.Excel;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OCPP.Core.Database;
using OCPP.Core.Management.Models;

namespace OCPP.Core.Management.Services
{
    public interface IOwnerReportService
    {
        OwnerReportViewModel BuildOwnerReport(DateTime? startDate, DateTime? stopDate);
        XLWorkbook CreateOwnerReportWorkbook(OwnerReportViewModel report);
        Task<OwnerReportSendResult> SendOwnerReportsAsync(DateTime? startDate, DateTime? stopDate, string overrideRecipient = null);
        void ScheduleRecurringReport();
        Task RunScheduledReport();
    }

    public class OwnerReportSendResult
    {
        public int Sent { get; set; }
        public int Failed { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
    }

    public class OwnerReportService : IOwnerReportService
    {
        private readonly OCPPCoreContext _dbContext;
        private readonly IEmailSender _emailSender;
        private readonly ILogger<OwnerReportService> _logger;
        private readonly IConfiguration _config;
        private readonly Microsoft.Extensions.Localization.IStringLocalizer<Controllers.HomeController> _localizer;
        private readonly IOptions<OwnerReportScheduleSettings> _scheduleSettings;

        public OwnerReportService(
            OCPPCoreContext dbContext,
            IEmailSender emailSender,
            ILogger<OwnerReportService> logger,
            IConfiguration config,
            Microsoft.Extensions.Localization.IStringLocalizer<Controllers.HomeController> localizer,
            IOptions<OwnerReportScheduleSettings> scheduleSettings)
        {
            _dbContext = dbContext;
            _emailSender = emailSender;
            _logger = logger;
            _config = config;
            _localizer = localizer;
            _scheduleSettings = scheduleSettings;
        }

        public OwnerReportViewModel BuildOwnerReport(DateTime? startDate, DateTime? stopDate)
        {
            startDate ??= new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1).AddMonths(-1);
            stopDate ??= new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1).AddDays(-1);

            startDate = startDate.Value.Date;
            stopDate = stopDate.Value.Date;

            DateTime dbStartDate = startDate.Value.ToUniversalTime();
            DateTime dbStopDate = stopDate.Value.AddDays(1).ToUniversalTime();

            var transactions = _dbContext.Transactions
                .Include(t => t.ChargePoint)
                .ThenInclude(cp => cp.Owner)
                .Where(t => t.StartTime >= dbStartDate && t.StartTime < dbStopDate)
                .AsNoTracking()
                .ToList();

            var owners = transactions
                .GroupBy(t => t.ChargePoint?.OwnerId)
                .Select(g =>
                {
                    var cp = g.Select(x => x.ChargePoint).FirstOrDefault(c => c != null);
                    string ownerName = cp?.Owner?.Name ?? _localizer["OwnerUnassigned"].Value;
                    string ownerEmail = cp?.Owner?.Email;

                    return new OwnerReportItem
                    {
                        OwnerId = g.Key,
                        OwnerName = ownerName,
                        OwnerEmail = ownerEmail,
                        EnergyKwh = g.Sum(t => t.EnergyKwh),
                        EnergyRevenue = g.Sum(t => t.EnergyCost),
                        UsageFeeTotal = g.Sum(t => t.UsageFeeAmount),
                        UserSessionFeeTotal = g.Sum(t => t.UserSessionFeeAmount),
                        OwnerSessionFeeTotal = g.Sum(t => t.OwnerSessionFeeAmount),
                        OperatorCommissionTotal = g.Sum(t => t.OperatorCommissionAmount),
                        OperatorRevenueTotal = g.Sum(t => t.OperatorRevenueTotal),
                        OwnerPayoutTotal = g.Sum(t => t.OwnerPayoutTotal),
                        SessionCount = g.Count(),
                        Transactions = g.Select(t => new OwnerReportTransaction
                        {
                            TransactionId = t.TransactionId,
                            ChargePointId = t.ChargePointId,
                            ChargePointName = t.ChargePoint?.Name,
                            ConnectorId = t.ConnectorId,
                            StartTime = t.StartTime,
                            StopTime = t.StopTime,
                            EnergyKwh = t.EnergyKwh,
                            EnergyRevenue = t.EnergyCost,
                            UsageFee = t.UsageFeeAmount,
                            UserSessionFee = t.UserSessionFeeAmount,
                            OwnerSessionFee = t.OwnerSessionFeeAmount,
                            OperatorCommission = t.OperatorCommissionAmount,
                            OperatorRevenueTotal = t.OperatorRevenueTotal,
                            OwnerPayoutTotal = t.OwnerPayoutTotal
                        }).OrderBy(t => t.StartTime).ToList()
                    };
                })
                .OrderBy(o => o.OwnerName)
                .ToList();

            return new OwnerReportViewModel
            {
                StartDate = startDate.Value,
                StopDate = stopDate.Value,
                Owners = owners
            };
        }

        public XLWorkbook CreateOwnerReportWorkbook(OwnerReportViewModel report)
        {
            var workbook = new XLWorkbook();
            var summarySheet = workbook.Worksheets.Add(_localizer["OwnerReportSummary"]);

            summarySheet.Cell(1, 1).Value = _localizer["OwnerName"].Value;
            summarySheet.Cell(1, 2).Value = _localizer["OwnerEmail"].Value;
            summarySheet.Cell(1, 3).Value = _localizer["EnergyKwh"].Value;
            summarySheet.Cell(1, 4).Value = _localizer["EnergyRevenue"].Value;
            summarySheet.Cell(1, 5).Value = _localizer["UsageFees"].Value;
            summarySheet.Cell(1, 6).Value = _localizer["UserSessionFees"].Value;
            summarySheet.Cell(1, 7).Value = _localizer["OwnerSessionFees"].Value;
            summarySheet.Cell(1, 8).Value = _localizer["GrossTotal"].Value;
            summarySheet.Cell(1, 9).Value = _localizer["OperatorCommission"].Value;
            summarySheet.Cell(1, 10).Value = _localizer["OperatorRevenue"].Value;
            summarySheet.Cell(1, 11).Value = _localizer["OwnerPayout"].Value;
            summarySheet.Cell(1, 12).Value = _localizer["SessionCount"].Value;

            var row = 2;
            foreach (var owner in report.Owners)
            {
                summarySheet.Cell(row, 1).Value = owner.OwnerName;
                summarySheet.Cell(row, 2).Value = owner.OwnerEmail;
                summarySheet.Cell(row, 3).Value = Math.Round(owner.EnergyKwh, 3);
                summarySheet.Cell(row, 4).Value = owner.EnergyRevenue;
                summarySheet.Cell(row, 5).Value = owner.UsageFeeTotal;
                summarySheet.Cell(row, 6).Value = owner.UserSessionFeeTotal;
                summarySheet.Cell(row, 7).Value = owner.OwnerSessionFeeTotal;
                summarySheet.Cell(row, 8).Value = owner.GrossTotal;
                summarySheet.Cell(row, 9).Value = owner.OperatorCommissionTotal;
                summarySheet.Cell(row, 10).Value = owner.OperatorRevenueTotal;
                summarySheet.Cell(row, 11).Value = owner.OwnerPayoutTotal;
                summarySheet.Cell(row, 12).Value = owner.SessionCount;
                row++;
            }
            summarySheet.Columns().AdjustToContents();

            var detailsSheet = workbook.Worksheets.Add(_localizer["OwnerReportDetails"]);
            detailsSheet.Cell(1, 1).Value = _localizer["OwnerName"].Value;
            detailsSheet.Cell(1, 2).Value = _localizer["ChargePoint"].Value;
            detailsSheet.Cell(1, 3).Value = _localizer["Connector"].Value;
            detailsSheet.Cell(1, 4).Value = _localizer["StartTime"].Value;
            detailsSheet.Cell(1, 5).Value = _localizer["StopTime"].Value;
            detailsSheet.Cell(1, 6).Value = _localizer["EnergyKwh"].Value;
            detailsSheet.Cell(1, 7).Value = _localizer["EnergyRevenue"].Value;
            detailsSheet.Cell(1, 8).Value = _localizer["UsageFees"].Value;
            detailsSheet.Cell(1, 9).Value = _localizer["UserSessionFees"].Value;
            detailsSheet.Cell(1, 10).Value = _localizer["OwnerSessionFees"].Value;
            detailsSheet.Cell(1, 11).Value = _localizer["OperatorCommission"].Value;
            detailsSheet.Cell(1, 12).Value = _localizer["OperatorRevenue"].Value;
            detailsSheet.Cell(1, 13).Value = _localizer["OwnerPayout"].Value;

            row = 2;
            foreach (var owner in report.Owners)
            {
                foreach (var tr in owner.Transactions)
                {
                    detailsSheet.Cell(row, 1).Value = owner.OwnerName;
                    detailsSheet.Cell(row, 2).Value = string.IsNullOrEmpty(tr.ChargePointName) ? tr.ChargePointId : tr.ChargePointName;
                    detailsSheet.Cell(row, 3).Value = tr.ConnectorId;
                    detailsSheet.Cell(row, 4).Value = tr.StartTime.ToLocalTime();
                    detailsSheet.Cell(row, 5).Value = tr.StopTime?.ToLocalTime();
                    detailsSheet.Cell(row, 6).Value = Math.Round(tr.EnergyKwh, 3);
                    detailsSheet.Cell(row, 7).Value = tr.EnergyRevenue;
                    detailsSheet.Cell(row, 8).Value = tr.UsageFee;
                    detailsSheet.Cell(row, 9).Value = tr.UserSessionFee;
                    detailsSheet.Cell(row, 10).Value = tr.OwnerSessionFee;
                    detailsSheet.Cell(row, 11).Value = tr.OperatorCommission;
                    detailsSheet.Cell(row, 12).Value = tr.OperatorRevenueTotal;
                    detailsSheet.Cell(row, 13).Value = tr.OwnerPayoutTotal;
                    row++;
                }
            }
            detailsSheet.Columns().AdjustToContents();
            return workbook;
        }

        public async Task<OwnerReportSendResult> SendOwnerReportsAsync(DateTime? startDate, DateTime? stopDate, string overrideRecipient = null)
        {
            var report = BuildOwnerReport(startDate, stopDate);

            var emailSettings = new EmailSettings();
            _config.GetSection("Email").Bind(emailSettings);

            if (emailSettings == null || !emailSettings.EnableOwnerReportEmails)
            {
                return new OwnerReportSendResult { Sent = 0, Failed = report.Owners.Count, Errors = new List<string> { "Email disabled" } };
            }

            var ownersToSend = report.Owners.Where(o => !string.IsNullOrWhiteSpace(o.OwnerEmail) || !string.IsNullOrWhiteSpace(overrideRecipient)).ToList();
            var result = new OwnerReportSendResult();

            foreach (var owner in ownersToSend)
            {
                var recipient = string.IsNullOrWhiteSpace(overrideRecipient) ? owner.OwnerEmail : overrideRecipient;
                if (string.IsNullOrWhiteSpace(recipient))
                {
                    result.Failed++;
                    result.Errors.Add("Missing recipient");
                    continue;
                }

                var ownerReport = new OwnerReportViewModel
                {
                    StartDate = report.StartDate,
                    StopDate = report.StopDate,
                    Owners = new List<OwnerReportItem> { owner }
                };

                using var workbook = CreateOwnerReportWorkbook(ownerReport);
                using var stream = new MemoryStream();
                workbook.SaveAs(stream);

                var subject = string.Format(_localizer["OwnerReportSubject"].Value, report.StartDate.ToString("yyyy-MM-dd"), report.StopDate.ToString("yyyy-MM-dd"));
                var body = string.Format(_localizer["OwnerReportBody"].Value, owner.OwnerName ?? _localizer["OwnerUnassigned"].Value, report.StartDate.ToString("yyyy-MM-dd"), report.StopDate.ToString("yyyy-MM-dd"));

                if (!string.IsNullOrWhiteSpace(overrideRecipient))
                {
                    body += " (test delivery)";
                }

                var attachment = new EmailAttachment(
                    $"OwnerReport_{owner.OwnerName ?? "Owner"}_{report.StartDate:yyyyMMdd}_{report.StopDate:yyyyMMdd}.xlsx",
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    stream.ToArray());

                var sendResult = await _emailSender.SendAsync(recipient, subject, body, new[] { attachment }, emailSettings.Headers);
                if (sendResult.Success)
                {
                    result.Sent++;
                }
                else
                {
                    result.Failed++;
                    result.Errors.Add(sendResult.Error);
                    _logger.LogWarning("OwnerReport: Failed to send report to {Email}: {Error}", recipient, sendResult.Error);
                }
            }

            return result;
        }

        public void ScheduleRecurringReport()
        {
            var settings = _scheduleSettings.Value;
            if (settings == null || !settings.Enabled)
            {
                _logger.LogInformation("OwnerReport schedule disabled.");
                return;
            }

            if (string.IsNullOrWhiteSpace(settings.Cron))
            {
                settings.Cron = "0 6 1 * *";
            }

            RecurringJob.AddOrUpdate<OwnerReportService>(
                "owner-report-recurring",
                svc => svc.RunScheduledReport(),
                settings.Cron);
            _logger.LogInformation("OwnerReport recurring job registered with CRON {Cron}", settings.Cron);
        }

        public async Task RunScheduledReport()
        {
            var settings = _scheduleSettings.Value ?? new OwnerReportScheduleSettings();
            DateTime now = DateTime.UtcNow;
            DateTime start;
            DateTime stop;
            if (settings.UsePreviousMonth)
            {
                var firstDayThisMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                stop = firstDayThisMonth.AddDays(-1);
                start = new DateTime(stop.Year, stop.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            }
            else
            {
                start = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                stop = start.AddMonths(1).AddDays(-1);
            }

            var result = await SendOwnerReportsAsync(start, stop, settings.SendTestTo);
            _logger.LogInformation("OwnerReport scheduled send completed. Sent={Sent}, Failed={Failed}", result.Sent, result.Failed);
        }
    }
}
