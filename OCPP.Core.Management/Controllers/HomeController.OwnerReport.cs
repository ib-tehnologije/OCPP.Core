using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OCPP.Core.Management.Models;
using OCPP.Core.Management.Services;

namespace OCPP.Core.Management.Controllers
{
    public partial class HomeController : BaseController
    {
        [Authorize]
        public IActionResult OwnerReport(DateTime? startDate, DateTime? stopDate)
        {
            var report = _ownerReportService.BuildOwnerReport(startDate, stopDate);
            return View(report);
        }

        [Authorize]
        public IActionResult OwnerReportCsv(DateTime? startDate, DateTime? stopDate)
        {
            var report = _ownerReportService.BuildOwnerReport(startDate, stopDate);
            var csv = new StringBuilder();

            csv.AppendLine(string.Join(DefaultCSVSeparator, new[]
            {
                _localizer["OwnerName"].Value,
                _localizer["OwnerEmail"].Value,
                _localizer["EnergyKwh"].Value,
                _localizer["EnergyRevenue"].Value,
                _localizer["UsageFees"].Value,
                _localizer["UserSessionFees"].Value,
                _localizer["OwnerSessionFees"].Value,
                _localizer["GrossTotal"].Value,
                _localizer["OperatorCommission"].Value,
                _localizer["OperatorRevenue"].Value,
                _localizer["OwnerPayout"].Value,
                _localizer["SessionCount"].Value
            }));

            foreach (var owner in report.Owners)
            {
                csv.Append(EscapeCsvValue(owner.OwnerName, DefaultCSVSeparator));
                csv.Append(DefaultCSVSeparator);
                csv.Append(EscapeCsvValue(owner.OwnerEmail, DefaultCSVSeparator));
                csv.Append(DefaultCSVSeparator);
                csv.Append(Math.Round(owner.EnergyKwh, 3).ToString(CultureInfo.InvariantCulture));
                csv.Append(DefaultCSVSeparator);
                csv.Append(owner.EnergyRevenue.ToString("0.0000", CultureInfo.InvariantCulture));
                csv.Append(DefaultCSVSeparator);
                csv.Append(owner.UsageFeeTotal.ToString("0.0000", CultureInfo.InvariantCulture));
                csv.Append(DefaultCSVSeparator);
                csv.Append(owner.UserSessionFeeTotal.ToString("0.0000", CultureInfo.InvariantCulture));
                csv.Append(DefaultCSVSeparator);
                csv.Append(owner.OwnerSessionFeeTotal.ToString("0.0000", CultureInfo.InvariantCulture));
                csv.Append(DefaultCSVSeparator);
                csv.Append(owner.GrossTotal.ToString("0.0000", CultureInfo.InvariantCulture));
                csv.Append(DefaultCSVSeparator);
                csv.Append(owner.OperatorCommissionTotal.ToString("0.0000", CultureInfo.InvariantCulture));
                csv.Append(DefaultCSVSeparator);
                csv.Append(owner.OperatorRevenueTotal.ToString("0.0000", CultureInfo.InvariantCulture));
                csv.Append(DefaultCSVSeparator);
                csv.Append(owner.OwnerPayoutTotal.ToString("0.0000", CultureInfo.InvariantCulture));
                csv.Append(DefaultCSVSeparator);
                csv.Append(owner.SessionCount.ToString(CultureInfo.InvariantCulture));
                csv.AppendLine();
            }

            var fileName = $"OwnerReport_{report.StartDate:yyyyMMdd}_{report.StopDate:yyyyMMdd}.csv";
            return File(Encoding.GetEncoding("ISO-8859-1").GetBytes(csv.ToString()), "text/csv", SanitizeFileName(fileName));
        }

        [Authorize]
        public IActionResult OwnerReportXlsx(DateTime? startDate, DateTime? stopDate)
        {
            var report = _ownerReportService.BuildOwnerReport(startDate, stopDate);

            using var workbook = _ownerReportService.CreateOwnerReportWorkbook(report);
            using var stream = new MemoryStream();
            workbook.SaveAs(stream);
            var fileName = $"OwnerReport_{report.StartDate:yyyyMMdd}_{report.StopDate:yyyyMMdd}.xlsx";
            return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", SanitizeFileName(fileName));
        }

        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SendOwnerReportEmails(DateTime? startDate, DateTime? stopDate)
        {
            var result = await _ownerReportService.SendOwnerReportsAsync(startDate, stopDate);
            if (result.Failed == 0)
            {
                TempData["InfoMsg"] = string.Format(_localizer["EmailSendSuccess"].Value, result.Sent);
            }
            else
            {
                TempData["ErrMessage"] = string.Format(_localizer["EmailSendPartial"].Value, result.Sent, result.Failed);
            }

            return RedirectToAction("OwnerReport", new { startDate, stopDate });
        }
    }
}
