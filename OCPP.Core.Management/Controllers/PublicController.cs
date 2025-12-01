using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OCPP.Core.Database;
using OCPP.Core.Management.Models;

namespace OCPP.Core.Management.Controllers
{
    [AllowAnonymous]
    public class PublicController : BaseController
    {
        public PublicController(
            IUserManager userManager,
            ILoggerFactory loggerFactory,
            IConfiguration config,
            OCPPCoreContext dbContext) : base(userManager, loggerFactory, config, dbContext)
        {
            Logger = loggerFactory.CreateLogger<PublicController>();
        }

        [HttpGet]
        public IActionResult Start(string cp, int conn = 1)
        {
            var model = BuildViewModel(cp, conn);
            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Start(PublicStartViewModel request)
        {
            var model = BuildViewModel(request?.ChargePointId, request?.ConnectorId ?? 1);
            model.ChargeTagId = request?.ChargeTagId;

            if (string.IsNullOrWhiteSpace(model.ChargePointId))
            {
                model.ErrorMessage = "Charge point is missing.";
                return View(model);
            }

            var chargeTagId = model.ChargeTagId;
            if (string.IsNullOrWhiteSpace(chargeTagId))
            {
                chargeTagId = $"WEB-{Guid.NewGuid():N}";
                model.ChargeTagId = chargeTagId;
            }

            var apiResult = await PostAsync("Payments/Create", new
            {
                chargePointId = model.ChargePointId,
                connectorId = model.ConnectorId,
                chargeTagId = chargeTagId,
                origin = "public",
                returnBaseUrl = $"{Request.Scheme}://{Request.Host}"
            });

            string checkoutUrl = ExtractCheckoutUrl(apiResult.Payload);
            string status = ExtractStatus(apiResult.Payload);

            if (!apiResult.Success)
            {
                model.ErrorMessage = ExtractMessage(apiResult.Payload) ?? apiResult.ErrorMessage ?? "Unable to start the transaction.";
                return View(model);
            }

            if (!string.IsNullOrWhiteSpace(checkoutUrl) &&
                string.Equals(status, "Redirect", StringComparison.OrdinalIgnoreCase))
            {
                return Redirect(checkoutUrl);
            }

            if (string.Equals(status, "Accepted", StringComparison.OrdinalIgnoreCase))
            {
                var resultModel = new PaymentResultViewModel
                {
                    Status = "Accepted",
                    Success = true,
                    Message = "Payment authorized. Charging session will start shortly.",
                    Origin = "public",
                    ReturnUrl = Url.Action("Start", "Public")
                };
                return View("~/Views/Payments/PublicResult.cshtml", resultModel);
            }

            model.ErrorMessage = ExtractMessage(apiResult.Payload) ?? "Unable to start the transaction.";
            return View(model);
        }

        [HttpGet]
        public IActionResult Map()
        {
            var vm = BuildMapViewModel();
            return View(vm);
        }

        private PublicMapViewModel BuildMapViewModel()
        {
            var vm = new PublicMapViewModel();

            var chargePoints = DbContext.ChargePoints.ToList<ChargePoint>();
            var connectorStatuses = DbContext.ConnectorStatuses.ToList<ConnectorStatus>();

            foreach (var cp in chargePoints)
            {
                var statuses = connectorStatuses.Where(c => c.ChargePointId == cp.ChargePointId).ToList();
                string aggregatedStatus = "Unknown";
                DateTime? statusTime = null;

                if (statuses.Any())
                {
                    if (statuses.Any(s => string.Equals(s.LastStatus, "Faulted", StringComparison.InvariantCultureIgnoreCase)))
                    {
                        aggregatedStatus = "Faulted";
                    }
                    else if (statuses.Any(s => string.Equals(s.LastStatus, "Occupied", StringComparison.InvariantCultureIgnoreCase)))
                    {
                        aggregatedStatus = "Occupied";
                    }
                    else if (statuses.All(s => string.Equals(s.LastStatus, "Available", StringComparison.InvariantCultureIgnoreCase)))
                    {
                        aggregatedStatus = "Available";
                    }
                    else
                    {
                        aggregatedStatus = statuses.First().LastStatus;
                    }

                    statusTime = statuses.Where(s => s.LastStatusTime.HasValue).OrderByDescending(s => s.LastStatusTime).FirstOrDefault()?.LastStatusTime;
                }

                vm.ChargePoints.Add(new PublicMapChargePoint
                {
                    ChargePointId = cp.ChargePointId,
                    Name = string.IsNullOrWhiteSpace(cp.Name) ? cp.ChargePointId : cp.Name,
                    ConnectorCount = statuses.Count(),
                    Status = aggregatedStatus,
                    StatusTime = statusTime,
                    Latitude = cp.Latitude,
                    Longitude = cp.Longitude,
                    LocationDescription = cp.LocationDescription,
                    PricePerKwh = cp.PricePerKwh,
                    UserSessionFee = cp.UserSessionFee,
                    ConnectorUsageFeePerMinute = cp.ConnectorUsageFeePerMinute,
                    StartUsageFeeAfterMinutes = cp.StartUsageFeeAfterMinutes,
                    UsageFeeAfterChargingEnds = cp.UsageFeeAfterChargingEnds
                });
            }

            return vm;
        }

        private PublicStartViewModel BuildViewModel(string chargePointId, int connectorId)
        {
            var model = new PublicStartViewModel
            {
                ChargePointId = chargePointId,
                ConnectorId = connectorId > 0 ? connectorId : 1
            };

            if (string.IsNullOrWhiteSpace(chargePointId))
            {
                model.ErrorMessage = "Charge point is missing.";
                return model;
            }

            var chargePoint = DbContext.ChargePoints.Find(chargePointId);
            if (chargePoint == null)
            {
                model.ErrorMessage = "Charge point not found.";
                return model;
            }

            model.ChargePointName = string.IsNullOrWhiteSpace(chargePoint.Name)
                ? chargePoint.ChargePointId
                : chargePoint.Name;
            model.PricePerKwh = chargePoint.PricePerKwh;
            model.UserSessionFee = chargePoint.UserSessionFee;
            model.MaxSessionKwh = chargePoint.MaxSessionKwh;
            model.StartUsageFeeAfterMinutes = chargePoint.StartUsageFeeAfterMinutes;
            model.MaxUsageFeeMinutes = chargePoint.MaxUsageFeeMinutes;
            model.ConnectorUsageFeePerMinute = chargePoint.ConnectorUsageFeePerMinute;
            model.UsageFeeAfterChargingEnds = chargePoint.UsageFeeAfterChargingEnds;
            model.FreeChargingEnabled = chargePoint.FreeChargingEnabled;
            model.MaxUsageFeeBillableMinutes = Math.Max(0, model.MaxUsageFeeMinutes - model.StartUsageFeeAfterMinutes);

            // Approximate max preauthorization similar to backend: energy cap + idle cap + session fee
            decimal energyCap = (decimal)model.MaxSessionKwh * model.PricePerKwh;
            decimal idleCap = model.ConnectorUsageFeePerMinute * model.MaxUsageFeeBillableMinutes;
            model.EstimatedMaxHold = Math.Max(0, energyCap) + Math.Max(0, idleCap) + Math.Max(0, model.UserSessionFee);

            var connector = DbContext.ConnectorStatuses.Find(chargePointId, model.ConnectorId);
            if (connector != null)
            {
                model.ConnectorName = string.IsNullOrWhiteSpace(connector.ConnectorName)
                    ? $"Connector {connector.ConnectorId}"
                    : connector.ConnectorName;
                model.LastStatus = connector.LastStatus;
                model.LastStatusTime = connector.LastStatusTime;
            }
            else
            {
                model.ConnectorName = $"Connector {model.ConnectorId}";
            }

            return model;
        }

        private async Task<(bool Success, string Payload, string ErrorMessage)> PostAsync(string relativePath, object payload)
        {
            string serverApiUrl = Config.GetValue<string>("ServerApiUrl");
            string apiKeyConfig = Config.GetValue<string>("ApiKey");

            if (string.IsNullOrWhiteSpace(serverApiUrl))
            {
                return (false, null, "Server API URL is not configured.");
            }

            if (!serverApiUrl.EndsWith('/'))
            {
                serverApiUrl += "/";
            }

            var targetUri = new Uri(new Uri(serverApiUrl), relativePath);

            try
            {
                using var httpClient = new HttpClient();
                httpClient.Timeout = TimeSpan.FromSeconds(30);

                if (!string.IsNullOrWhiteSpace(apiKeyConfig))
                {
                    httpClient.DefaultRequestHeaders.Add("X-API-Key", apiKeyConfig);
                }

                string jsonPayload = JsonConvert.SerializeObject(payload);
                using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                HttpResponseMessage response = await httpClient.PostAsync(targetUri, content);
                string responsePayload = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    return (true, responsePayload, null);
                }

                Logger.LogWarning("PublicController => POST {Path} failed: {StatusCode} / {Payload}", relativePath, response.StatusCode, responsePayload);
                return (false, responsePayload, response.StatusCode.ToString());
            }
            catch (Exception exp)
            {
                Logger.LogError(exp, "PublicController => POST {Path} failed: {Message}", relativePath, exp.Message);
                return (false, null, exp.Message);
            }
        }

        private static string ExtractStatus(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload)) return null;
            try
            {
                var json = JsonConvert.DeserializeObject<Dictionary<string, object>>(payload);
                if (json != null && json.TryGetValue("status", out var statusValue))
                {
                    return statusValue?.ToString();
                }
            }
            catch
            {
                // ignore parse errors
            }

            return null;
        }

        private static string ExtractCheckoutUrl(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload)) return null;
            try
            {
                var json = JObject.Parse(payload);
                return json["checkoutUrl"]?.Value<string>();
            }
            catch
            {
                // ignore parse errors
            }

            return null;
        }

        private static string ExtractMessage(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                return null;
            }

            try
            {
                var json = JObject.Parse(payload);
                var message = json["message"]?.Value<string>();
                if (!string.IsNullOrWhiteSpace(message))
                {
                    return message;
                }

                var error = json["error"]?.Value<string>();
                if (!string.IsNullOrWhiteSpace(error))
                {
                    return error;
                }
            }
            catch
            {
                // ignore parse errors
            }

            return null;
        }
    }
}
