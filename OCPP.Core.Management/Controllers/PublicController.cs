using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OCPP.Core.Database;
using OCPP.Core.Management.Helpers;
using OCPP.Core.Management.Models;

namespace OCPP.Core.Management.Controllers
{
    [AllowAnonymous]
    public class PublicController : BaseController
    {
        private readonly IDataProtector _recoveryProtector;

        public PublicController(
            IUserManager userManager,
            ILoggerFactory loggerFactory,
            IConfiguration config,
            IDataProtectionProvider dataProtectionProvider,
            OCPPCoreContext dbContext) : base(userManager, loggerFactory, config, dbContext)
        {
            Logger = loggerFactory.CreateLogger<PublicController>();
            _recoveryProtector = dataProtectionProvider.CreateProtector("OCPP.Core.Management.PublicPaymentRecovery.v1");
        }

        [HttpGet]
        public IActionResult Index()
        {
            return RedirectToAction(nameof(Map));
        }

        [HttpGet]
        public async Task<IActionResult> Start(string cp, int? conn = null)
        {
            var recovery = ReadRecoveryCookie();
            if (recovery != null &&
                string.Equals(recovery.ChargePointId, cp, StringComparison.OrdinalIgnoreCase) &&
                !conn.HasValue)
            {
                conn = recovery.ConnectorId;
            }

            var model = BuildViewModel(cp, conn);
            var redirect = await ApplyRecoveryAsync(model, recovery);
            if (redirect != null)
            {
                return redirect;
            }

            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Start(PublicStartViewModel request)
        {
            int requestedConnectorId = request?.ConnectorId ?? 1;
            var model = BuildViewModel(request?.ChargePointId, requestedConnectorId);
            model.RequestR1Invoice = request?.RequestR1Invoice ?? false;
            model.BuyerCompanyName = request?.BuyerCompanyName;
            model.BuyerOib = request?.BuyerOib;

            if (string.IsNullOrWhiteSpace(model.ChargePointId))
            {
                model.ErrorMessage = "Charge point is missing.";
                return View(model);
            }

            if (model.Connectors.Count > 0 &&
                model.Connectors.All(c => c.ConnectorId != requestedConnectorId))
            {
                model.ErrorMessage = "Selected connector is not available for this charge point. Please choose another connector.";
                return View(model);
            }

            if (model.RequestR1Invoice)
            {
                var normalizedOib = NormalizeOib(model.BuyerOib);
                if (string.IsNullOrWhiteSpace(normalizedOib))
                {
                    model.ErrorMessage = "For an R1 (company) invoice, please enter your OIB (11 digits).";
                    return View(model);
                }

                if (!IsValidOib(normalizedOib))
                {
                    model.ErrorMessage = "The OIB you entered is not valid. Please check the 11 digits and try again.";
                    return View(model);
                }

                model.BuyerOib = normalizedOib;
                model.BuyerCompanyName = (model.BuyerCompanyName ?? string.Empty).Trim();
            }
            else
            {
                // Avoid leaking stale hidden form values when R1 is not requested.
                model.BuyerOib = null;
                model.BuyerCompanyName = null;
            }

            var chargeTagId = $"WEB-{Guid.NewGuid():N}";
            model.ChargeTagId = chargeTagId;

            var apiResult = await PostAsync("Payments/Create", new
            {
                chargePointId = model.ChargePointId,
                connectorId = model.ConnectorId,
                chargeTagId = chargeTagId,
                requestR1Invoice = model.RequestR1Invoice,
                buyerCompanyName = model.BuyerCompanyName,
                buyerOib = model.BuyerOib,
                origin = "public",
                returnBaseUrl = $"{Request.Scheme}://{Request.Host}"
            });

            string checkoutUrl = ExtractCheckoutUrl(apiResult.Payload);
            string status = ExtractStatus(apiResult.Payload);
            string reason = ExtractReason(apiResult.Payload);
            Guid? reservationId = ExtractReservationId(apiResult.Payload);

            bool isBusy = string.Equals(status, "ConnectorBusy", StringComparison.OrdinalIgnoreCase);
            bool isOffline = string.Equals(status, "ChargerOffline", StringComparison.OrdinalIgnoreCase);

            if (!apiResult.Success || isBusy || isOffline)
            {
                if (isBusy)
                {
                    model.ErrorMessage = BuildBusyMessage(reason);
                }
                else if (isOffline)
                {
                    model.ErrorMessage = "This connector is offline. Please try again when it is available.";
                }
                else
                {
                    model.ErrorMessage = ExtractMessage(apiResult.Payload) ?? apiResult.ErrorMessage ?? "Unable to start the transaction.";
                }

                var redirect = await ApplyRecoveryAsync(model, ReadRecoveryCookie());
                if (redirect != null)
                {
                    return redirect;
                }

                return View(model);
            }

            if (!string.IsNullOrWhiteSpace(checkoutUrl) &&
                string.Equals(status, "Redirect", StringComparison.OrdinalIgnoreCase))
            {
                if (reservationId.HasValue)
                {
                    WriteRecoveryCookie(new PublicPaymentRecoveryPayload
                    {
                        ReservationId = reservationId.Value,
                        ChargePointId = model.ChargePointId,
                        ConnectorId = model.ConnectorId,
                        CreatedAtUtc = DateTime.UtcNow
                    });
                }
                return Redirect(checkoutUrl);
            }

            if (string.Equals(status, "Accepted", StringComparison.OrdinalIgnoreCase))
            {
                ClearRecoveryCookie();
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

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CancelRecovery(string chargePointId, int connectorId, Guid reservationId)
        {
            if (reservationId != Guid.Empty)
            {
                await PostAsync("Payments/Cancel", new
                {
                    reservationId,
                    reason = "public_recovery_cancelled"
                });
            }

            ClearRecoveryCookie();
            return RedirectToAction(nameof(Start), new { cp = chargePointId, conn = connectorId });
        }

        private PublicMapViewModel BuildMapViewModel()
        {
            var vm = new PublicMapViewModel();

            var chargePoints = DbContext.ChargePoints.ToList<ChargePoint>();
            var connectorStatuses = DbContext.ConnectorStatuses.ToList<ConnectorStatus>();
            var openTransactions = DbContext.Transactions
                .Where(t => t.StopTime == null)
                .Select(t => new { t.ChargePointId, t.ConnectorId })
                .Distinct()
                .ToList();
            var activeReservations = DbContext.ChargePaymentReservations
                .Where(r => ChargePaymentReservationState.ConnectorLockStatuses.Contains(r.Status))
                .Select(r => new { r.ChargePointId, r.ConnectorId })
                .Distinct()
                .ToList();

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
                    else if (openTransactions.Any(t => string.Equals(t.ChargePointId, cp.ChargePointId, StringComparison.OrdinalIgnoreCase)))
                    {
                        aggregatedStatus = "Occupied";
                    }
                    else if (activeReservations.Any(r => string.Equals(r.ChargePointId, cp.ChargePointId, StringComparison.OrdinalIgnoreCase)))
                    {
                        aggregatedStatus = "Reserved";
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

        private PublicStartViewModel BuildViewModel(string chargePointId, int? requestedConnectorId)
        {
            var model = new PublicStartViewModel
            {
                ChargePointId = chargePointId,
                ConnectorId = requestedConnectorId.GetValueOrDefault() > 0 ? requestedConnectorId.Value : 1
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

            var openTransactions = DbContext.Transactions
                .Where(t => t.ChargePointId == chargePointId && t.StopTime == null)
                .Select(t => t.ConnectorId)
                .Distinct()
                .ToHashSet();
            var activeReservations = DbContext.ChargePaymentReservations
                .Where(r => r.ChargePointId == chargePointId && ChargePaymentReservationState.ConnectorLockStatuses.Contains(r.Status))
                .GroupBy(r => r.ConnectorId)
                .ToDictionary(g => g.Key, g => g.Max(r => r.UpdatedAtUtc));
            var connectors = DbContext.ConnectorStatuses
                .Where(c => c.ChargePointId == chargePointId)
                .OrderBy(c => c.ConnectorId)
                .ToList();

            if (connectors.Count > 0)
            {
                var connectorStates = connectors
                    .Select(c =>
                    {
                        bool hasOpenTransaction = openTransactions.Contains(c.ConnectorId);
                        bool hasActiveReservation = activeReservations.ContainsKey(c.ConnectorId);
                        var effectiveStatus = GetEffectiveConnectorStatus(c.LastStatus, hasOpenTransaction, hasActiveReservation);
                        var statusTime = c.LastStatusTime;
                        if (hasActiveReservation && activeReservations.TryGetValue(c.ConnectorId, out var reservationUpdatedAt))
                        {
                            statusTime = statusTime.HasValue && statusTime.Value > reservationUpdatedAt
                                ? statusTime
                                : reservationUpdatedAt;
                        }

                        return new
                        {
                            Connector = c,
                            EffectiveStatus = effectiveStatus,
                            EffectiveStatusTime = statusTime,
                            OccupancyReason = hasOpenTransaction ? "OpenTransaction" : (hasActiveReservation ? "ActiveReservation" : null)
                        };
                    })
                    .ToList();

                int selectedConnectorId = requestedConnectorId.HasValue && requestedConnectorId.Value > 0 &&
                    connectorStates.Any(c => c.Connector.ConnectorId == requestedConnectorId.Value)
                    ? requestedConnectorId.Value
                    : connectorStates.FirstOrDefault(c => IsAvailableStatus(c.EffectiveStatus))?.Connector.ConnectorId ?? connectorStates.First().Connector.ConnectorId;

                model.ConnectorId = selectedConnectorId;
                model.Connectors = connectorStates
                    .Select(c => new PublicStartConnectorOption
                    {
                        ConnectorId = c.Connector.ConnectorId,
                        Label = BuildConnectorLabel(c.Connector),
                        LastStatus = c.EffectiveStatus,
                        LastStatusTime = c.EffectiveStatusTime,
                        OccupancyReason = c.OccupancyReason,
                        IsSelected = c.Connector.ConnectorId == selectedConnectorId
                    })
                    .ToList();

                var selectedConnector = connectorStates.First(c => c.Connector.ConnectorId == selectedConnectorId);
                model.ConnectorName = BuildConnectorLabel(selectedConnector.Connector);
                model.LastStatus = selectedConnector.EffectiveStatus;
                model.LastStatusTime = selectedConnector.EffectiveStatusTime;
                model.AvailabilityMessage = BuildAvailabilityMessage(selectedConnector.EffectiveStatus, selectedConnector.OccupancyReason);
            }
            else
            {
                model.Connectors.Add(new PublicStartConnectorOption
                {
                    ConnectorId = model.ConnectorId,
                    Label = $"Connector {model.ConnectorId}",
                    IsSelected = true
                });
                model.ConnectorName = $"Connector {model.ConnectorId}";
            }

            return model;
        }

        private static string BuildConnectorLabel(ConnectorStatus connector)
        {
            if (connector == null)
            {
                return null;
            }

            return string.IsNullOrWhiteSpace(connector.ConnectorName)
                ? $"Connector {connector.ConnectorId}"
                : connector.ConnectorName;
        }

        private static string GetEffectiveConnectorStatus(string lastStatus, bool hasOpenTransaction, bool hasActiveReservation)
        {
            if (string.Equals(lastStatus, "Faulted", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(lastStatus, "Unavailable", StringComparison.OrdinalIgnoreCase))
            {
                return lastStatus;
            }

            if (hasOpenTransaction)
            {
                return string.IsNullOrWhiteSpace(lastStatus) ? "Occupied" : lastStatus;
            }

            if (hasActiveReservation)
            {
                return "Reserved";
            }

            return string.IsNullOrWhiteSpace(lastStatus) ? "Unknown" : lastStatus;
        }

        private static string BuildAvailabilityMessage(string effectiveStatus, string occupancyReason)
        {
            if (string.Equals(occupancyReason, "ActiveReservation", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(effectiveStatus, "Reserved", StringComparison.OrdinalIgnoreCase))
            {
                return "This connector is temporarily reserved during checkout. If it is your session, continue in the same browser or choose another connector.";
            }

            if (string.Equals(occupancyReason, "OpenTransaction", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(effectiveStatus, "Occupied", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(effectiveStatus, "Charging", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(effectiveStatus, "SuspendedEV", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(effectiveStatus, "SuspendedEVSE", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(effectiveStatus, "Finishing", StringComparison.OrdinalIgnoreCase))
            {
                return "This connector is currently in use. Please stop the active session first or choose another connector.";
            }

            if (string.Equals(effectiveStatus, "Unavailable", StringComparison.OrdinalIgnoreCase))
            {
                return "This connector is unavailable right now. Please choose another connector.";
            }

            if (string.Equals(effectiveStatus, "Faulted", StringComparison.OrdinalIgnoreCase))
            {
                return "This connector is currently faulted. Please choose another connector.";
            }

            return null;
        }

        private static bool IsAvailableStatus(string status)
        {
            return string.Equals(status, "Available", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, "Preparing", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<IActionResult> ApplyRecoveryAsync(PublicStartViewModel model, PublicPaymentRecoveryPayload recovery)
        {
            if (model == null || recovery == null)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(model.ChargePointId) ||
                !string.Equals(recovery.ChargePointId, model.ChargePointId, StringComparison.OrdinalIgnoreCase) ||
                recovery.ConnectorId != model.ConnectorId)
            {
                return null;
            }

            var resumeResult = await PostAsync("Payments/Resume", new
            {
                reservationId = recovery.ReservationId
            });

            string status = ExtractStatus(resumeResult.Payload);
            string reservationStatus = ExtractReservationStatus(resumeResult.Payload);
            string checkoutUrl = ExtractCheckoutUrl(resumeResult.Payload);
            string error = ExtractMessage(resumeResult.Payload) ?? resumeResult.ErrorMessage;

            if (string.Equals(status, "Status", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, ChargePaymentReservationState.Completed, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(reservationStatus, ChargePaymentReservationState.Authorized, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(reservationStatus, ChargePaymentReservationState.StartRequested, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(reservationStatus, ChargePaymentReservationState.Charging, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(reservationStatus, ChargePaymentReservationState.Completed, StringComparison.OrdinalIgnoreCase))
            {
                return RedirectToAction("Status", "Payments", new { reservationId = recovery.ReservationId, origin = "public" });
            }

            if (string.Equals(status, "Redirect", StringComparison.OrdinalIgnoreCase))
            {
                model.RecoveryReservationId = recovery.ReservationId;
                model.RecoveryReservationStatus = reservationStatus ?? ChargePaymentReservationState.Pending;
                model.RecoveryCheckoutUrl = checkoutUrl;
                model.RecoveryMessage = "You already have an unfinished checkout for this connector. Continue payment or cancel it before starting a new session.";
                model.ErrorMessage = null;
                return null;
            }

            if (string.Equals(status, "ResumeUnavailable", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "MissingCheckoutSession", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "StripeError", StringComparison.OrdinalIgnoreCase))
            {
                model.RecoveryReservationId = recovery.ReservationId;
                model.RecoveryReservationStatus = reservationStatus ?? ChargePaymentReservationState.Pending;
                model.RecoveryCheckoutUrl = null;
                model.RecoveryMessage = !string.IsNullOrWhiteSpace(error)
                    ? $"{error} Cancel the previous attempt to unlock this connector."
                    : "This checkout can no longer be resumed. Cancel the previous attempt to unlock this connector.";
                model.ErrorMessage = null;
                return null;
            }

            if (string.Equals(status, ChargePaymentReservationState.Cancelled, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, ChargePaymentReservationState.Failed, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, ChargePaymentReservationState.StartRejected, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, ChargePaymentReservationState.StartTimeout, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, ChargePaymentReservationState.Abandoned, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "NotFound", StringComparison.OrdinalIgnoreCase))
            {
                ClearRecoveryCookie();
            }

            return null;
        }

        private PublicPaymentRecoveryPayload ReadRecoveryCookie()
        {
            if (PublicPaymentRecoveryCookie.TryRead(Request.Cookies, _recoveryProtector, out var payload))
            {
                return payload;
            }

            return null;
        }

        private void WriteRecoveryCookie(PublicPaymentRecoveryPayload payload)
        {
            var protectedValue = PublicPaymentRecoveryCookie.Protect(_recoveryProtector, payload);
            Response.Cookies.Append(
                PublicPaymentRecoveryCookie.CookieName,
                protectedValue,
                PublicPaymentRecoveryCookie.BuildCookieOptions(Request.IsHttps));
        }

        private void ClearRecoveryCookie()
        {
            Response.Cookies.Delete(
                PublicPaymentRecoveryCookie.CookieName,
                new CookieOptions
                {
                    Path = "/",
                    Secure = Request.IsHttps,
                    SameSite = SameSiteMode.Lax
                });
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

        private static string ExtractReservationStatus(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload)) return null;
            try
            {
                var json = JObject.Parse(payload);
                return json["reservationStatus"]?.Value<string>();
            }
            catch
            {
                return null;
            }
        }

        private static Guid? ExtractReservationId(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload)) return null;
            try
            {
                var json = JObject.Parse(payload);
                var raw = json["reservationId"]?.Value<string>();
                return Guid.TryParse(raw, out var reservationId) ? reservationId : null;
            }
            catch
            {
                return null;
            }
        }

        private static string ExtractReason(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload)) return null;
            try
            {
                var json = JObject.Parse(payload);
                return json["reason"]?.Value<string>();
            }
            catch
            {
                return null;
            }
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

        private static string BuildBusyMessage(string reason)
        {
            if (string.Equals(reason, "ActiveReservation", StringComparison.OrdinalIgnoreCase))
            {
                return "This connector is temporarily reserved during checkout. If it is your session, continue in the same browser or choose another connector.";
            }

            if (string.Equals(reason, "OpenTransaction", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(reason, "LiveStatus:Occupied", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(reason, "LiveStatus:Charging", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(reason, "LiveStatus:SuspendedEV", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(reason, "LiveStatus:SuspendedEVSE", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(reason, "LiveStatus:Finishing", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(reason, "PersistedStatus:Occupied", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(reason, "PersistedStatus:Charging", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(reason, "PersistedStatus:SuspendedEV", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(reason, "PersistedStatus:SuspendedEVSE", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(reason, "PersistedStatus:Finishing", StringComparison.OrdinalIgnoreCase))
            {
                return "This connector is currently in use. Please stop the active session first or choose another connector.";
            }

            if (string.Equals(reason, "LiveStatus:Unavailable", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(reason, "LiveStatus:Faulted", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(reason, "PersistedStatus:Unavailable", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(reason, "PersistedStatus:Faulted", StringComparison.OrdinalIgnoreCase))
            {
                return "This connector is not ready right now. Please choose another connector.";
            }

            return "This connector is currently unavailable. Please choose another connector or try again in a moment.";
        }

        private static string NormalizeOib(string oib)
        {
            if (string.IsNullOrWhiteSpace(oib))
            {
                return null;
            }

            var digits = new string(oib.Where(char.IsDigit).ToArray());
            return digits.Length == 0 ? null : digits;
        }

        /// <summary>
        /// Croatian OIB validation (ISO 7064 MOD 11,10).
        /// </summary>
        private static bool IsValidOib(string oibDigits)
        {
            if (string.IsNullOrWhiteSpace(oibDigits) || oibDigits.Length != 11 || oibDigits.Any(c => c < '0' || c > '9'))
            {
                return false;
            }

            int a = 10;
            for (int i = 0; i < 10; i++)
            {
                a = a + (oibDigits[i] - '0');
                a = a % 10;
                if (a == 0) a = 10;
                a = (a * 2) % 11;
            }

            int control = 11 - a;
            if (control == 10) control = 0;
            return control == (oibDigits[10] - '0');
        }
    }
}
