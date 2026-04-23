using System;
using System.Collections.Generic;
using System.Globalization;
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

            var model = await BuildViewModelAsync(cp, conn);
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
            var model = await BuildViewModelAsync(request?.ChargePointId, requestedConnectorId);
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
        public async Task<IActionResult> Map()
        {
            var vm = await BuildMapViewModelAsync();
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

        private async Task<PublicMapViewModel> BuildMapViewModelAsync()
        {
            var vm = new PublicMapViewModel();
            var nowUtc = DateTime.UtcNow;
            var onlineStatuses = await LoadOnlineChargePointStatusesAsync();

            var chargePoints = DbContext.ChargePoints.ToList<ChargePoint>();
            var connectorStatuses = DbContext.ConnectorStatuses.ToList<ConnectorStatus>();
            var openTransactions = DbContext.Transactions
                .Where(t => t.StopTime == null)
                .Select(t => new { t.ChargePointId, t.ConnectorId })
                .Distinct()
                .ToList();
            var activeReservations = DbContext.ChargePaymentReservations
                .Where(r => ChargePaymentReservationState.ConnectorLockStatuses.Contains(r.Status))
                .Select(r => new { r.ChargePointId, r.ConnectorId, r.UpdatedAtUtc })
                .ToList();

            foreach (var cp in chargePoints)
            {
                var chargePointStatuses = connectorStatuses
                    .Where(c => string.Equals(c.ChargePointId, cp.ChargePointId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var chargePointOpenTransactions = openTransactions
                    .Where(t => string.Equals(t.ChargePointId, cp.ChargePointId, StringComparison.OrdinalIgnoreCase))
                    .Select(t => t.ConnectorId)
                    .ToHashSet();
                var chargePointActiveReservations = activeReservations
                    .Where(r => string.Equals(r.ChargePointId, cp.ChargePointId, StringComparison.OrdinalIgnoreCase))
                    .GroupBy(r => r.ConnectorId)
                    .ToDictionary(g => g.Key, g => g.Max(r => r.UpdatedAtUtc));
                var connectorStates = BuildPublicConnectorStates(
                    chargePointStatuses,
                    chargePointOpenTransactions,
                    chargePointActiveReservations,
                    onlineStatuses.TryGetValue(cp.ChargePointId, out var liveChargePointStatus)
                        ? liveChargePointStatus
                        : null,
                    NormalizePublicDisplayCode(cp.PublicDisplayCode),
                    nowUtc);

                int connectorCount = connectorStates.Count;
                int availableConnectorCount = connectorStates.Count(s => IsAvailableStatus(s.EffectiveStatus));
                int occupiedConnectorCount = connectorStates.Count(s => string.Equals(s.EffectiveStatus, "Occupied", StringComparison.OrdinalIgnoreCase));
                int offlineConnectorCount = connectorCount - availableConnectorCount - occupiedConnectorCount;
                string aggregatedStatus = BuildAggregateConnectorStatus(availableConnectorCount, occupiedConnectorCount);
                DateTime? statusTime = connectorStates
                    .Where(s => s.EffectiveStatusTime.HasValue)
                    .OrderByDescending(s => s.EffectiveStatusTime)
                    .FirstOrDefault()
                    ?.EffectiveStatusTime;

                vm.ChargePoints.Add(new PublicMapChargePoint
                {
                    ChargePointId = cp.ChargePointId,
                    Name = string.IsNullOrWhiteSpace(cp.Name) ? cp.ChargePointId : cp.Name,
                    ConnectorCount = connectorCount,
                    AvailableConnectorCount = availableConnectorCount,
                    OccupiedConnectorCount = occupiedConnectorCount,
                    OfflineConnectorCount = offlineConnectorCount,
                    Status = aggregatedStatus,
                    StatusTime = statusTime,
                    PublicDisplayCode = NormalizePublicDisplayCode(cp.PublicDisplayCode),
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

        private async Task<PublicStartViewModel> BuildViewModelAsync(string chargePointId, int? requestedConnectorId)
        {
            var model = new PublicStartViewModel
            {
                ChargePointId = chargePointId,
                ConnectorId = requestedConnectorId.GetValueOrDefault() > 0 ? requestedConnectorId.Value : 1
            };
            var nowUtc = DateTime.UtcNow;
            var onlineStatuses = await LoadOnlineChargePointStatusesAsync();

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
            model.PublicDisplayCode = NormalizePublicDisplayCode(chargePoint.PublicDisplayCode);
            model.LocationDescription = chargePoint.LocationDescription;
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

            var connectorStates = BuildPublicConnectorStates(
                connectors,
                openTransactions,
                activeReservations,
                onlineStatuses.TryGetValue(chargePointId, out var liveChargePointStatus)
                    ? liveChargePointStatus
                    : null,
                model.PublicDisplayCode,
                nowUtc);

            if (connectorStates.Count > 0)
            {
                int selectedConnectorId = requestedConnectorId.HasValue && requestedConnectorId.Value > 0 &&
                    connectorStates.Any(c => c.ConnectorId == requestedConnectorId.Value)
                    ? requestedConnectorId.Value
                    : connectorStates.FirstOrDefault(c => IsAvailableStatus(c.EffectiveStatus))?.ConnectorId ?? connectorStates.First().ConnectorId;

                model.ConnectorId = selectedConnectorId;
                model.Connectors = connectorStates
                    .Select(c => BuildPublicStartConnectorOption(c, c.ConnectorId == selectedConnectorId))
                    .ToList();

                var selectedConnector = connectorStates.First(c => c.ConnectorId == selectedConnectorId);
                model.ConnectorName = ResolvePublicConnectorPrimaryLabel(
                    selectedConnector.DisplayName,
                    selectedConnector.PublicConnectorShortCode,
                    selectedConnector.ConnectorId);
                model.PublicConnectorCode = selectedConnector.PublicConnectorCode;
                model.PublicConnectorShortCode = selectedConnector.PublicConnectorShortCode;
                model.LastStatus = selectedConnector.EffectiveStatus;
                model.LastStatusTime = selectedConnector.EffectiveStatusTime;
                model.AvailabilityMessage = selectedConnector.AvailabilityMessage;
            }
            else
            {
                string displayName = $"Connector {model.ConnectorId}";
                string publicConnectorCode = BuildPublicConnectorCode(model.PublicDisplayCode, model.ConnectorId);
                string publicConnectorShortCode = BuildPublicConnectorShortCode(model.PublicDisplayCode, model.ConnectorId);
                model.Connectors.Add(new PublicStartConnectorOption
                {
                    ConnectorId = model.ConnectorId,
                    Label = ResolvePublicConnectorPrimaryLabel(displayName, publicConnectorShortCode, model.ConnectorId),
                    DisplayName = displayName,
                    PublicConnectorCode = publicConnectorCode,
                    PublicConnectorShortCode = publicConnectorShortCode,
                    LastStatus = "Offline",
                    AvailabilityMessage = BuildAvailabilityMessage("Offline", null),
                    IsSelected = true
                });
                model.ConnectorName = ResolvePublicConnectorPrimaryLabel(displayName, publicConnectorShortCode, model.ConnectorId);
                model.PublicConnectorCode = publicConnectorCode;
                model.PublicConnectorShortCode = publicConnectorShortCode;
                model.LastStatus = "Offline";
                model.AvailabilityMessage = BuildAvailabilityMessage("Offline", null);
            }

            return model;
        }

        private List<PublicConnectorState> BuildPublicConnectorStates(
            IReadOnlyCollection<ConnectorStatus> connectorStatuses,
            ISet<int> openTransactions,
            IReadOnlyDictionary<int, DateTime> activeReservations,
            ChargePointStatus liveChargePointStatus,
            string publicDisplayCode,
            DateTime nowUtc)
        {
            connectorStatuses ??= Array.Empty<ConnectorStatus>();
            openTransactions ??= new HashSet<int>();
            activeReservations ??= new Dictionary<int, DateTime>();

            var connectorIds = connectorStatuses
                .Select(c => c.ConnectorId)
                .Concat(openTransactions)
                .Concat(activeReservations.Keys)
                .Distinct()
                .OrderBy(id => id)
                .ToList();

            var states = new List<PublicConnectorState>();
            foreach (int connectorId in connectorIds)
            {
                ConnectorStatus connector = connectorStatuses.FirstOrDefault(c => c.ConnectorId == connectorId);
                bool hasOpenTransaction = openTransactions.Contains(connectorId);
                bool hasActiveReservation = activeReservations.ContainsKey(connectorId);
                string liveRawStatus = GetLiveConnectorRawStatus(liveChargePointStatus, connectorId);
                DateTime? statusTime = connector?.LastStatusTime;
                if (hasActiveReservation && activeReservations.TryGetValue(connectorId, out var reservationUpdatedAt))
                {
                    statusTime = statusTime.HasValue && statusTime.Value > reservationUpdatedAt
                        ? statusTime
                        : reservationUpdatedAt;
                }

                string effectiveStatus = GetEffectiveConnectorStatus(
                    connector?.LastStatus,
                    liveRawStatus,
                    statusTime,
                    hasOpenTransaction,
                    hasActiveReservation,
                    liveChargePointStatus != null,
                    nowUtc);

                states.Add(new PublicConnectorState
                {
                    ConnectorId = connectorId,
                    DisplayName = BuildConnectorDisplayName(connector, connectorId),
                    PublicConnectorCode = BuildPublicConnectorCode(publicDisplayCode, connectorId),
                    PublicConnectorShortCode = BuildPublicConnectorShortCode(publicDisplayCode, connectorId),
                    EffectiveStatus = effectiveStatus,
                    EffectiveStatusTime = statusTime,
                    OccupancyReason = hasOpenTransaction ? "OpenTransaction" : (hasActiveReservation ? "ActiveReservation" : null),
                    AvailabilityMessage = BuildAvailabilityMessage(effectiveStatus, hasOpenTransaction ? "OpenTransaction" : (hasActiveReservation ? "ActiveReservation" : null))
                });
            }

            return states;
        }

        private static PublicStartConnectorOption BuildPublicStartConnectorOption(PublicConnectorState connectorState, bool isSelected)
        {
            return new PublicStartConnectorOption
            {
                ConnectorId = connectorState.ConnectorId,
                Label = ResolvePublicConnectorPrimaryLabel(
                    connectorState.DisplayName,
                    connectorState.PublicConnectorShortCode,
                    connectorState.ConnectorId),
                DisplayName = connectorState.DisplayName,
                LastStatus = connectorState.EffectiveStatus,
                LastStatusTime = connectorState.EffectiveStatusTime,
                OccupancyReason = connectorState.OccupancyReason,
                AvailabilityMessage = connectorState.AvailabilityMessage,
                PublicConnectorCode = connectorState.PublicConnectorCode,
                PublicConnectorShortCode = connectorState.PublicConnectorShortCode,
                IsSelected = isSelected
            };
        }

        private async Task<Dictionary<string, ChargePointStatus>> LoadOnlineChargePointStatusesAsync()
        {
            var dictOnlineStatus = new Dictionary<string, ChargePointStatus>(StringComparer.OrdinalIgnoreCase);

            string serverApiUrl = Config?.GetValue<string>("ServerApiUrl");
            if (string.IsNullOrWhiteSpace(serverApiUrl))
            {
                return dictOnlineStatus;
            }

            string apiKeyConfig = Config?.GetValue<string>("ApiKey");

            try
            {
                using var httpClient = new HttpClient
                {
                    Timeout = TimeSpan.FromSeconds(4)
                };

                if (!serverApiUrl.EndsWith('/'))
                {
                    serverApiUrl += "/";
                }

                if (!string.IsNullOrWhiteSpace(apiKeyConfig))
                {
                    httpClient.DefaultRequestHeaders.Add("X-API-Key", apiKeyConfig);
                }

                Uri uri = new Uri(new Uri(serverApiUrl), "Status");
                HttpResponseMessage response = await httpClient.GetAsync(uri);
                if (response.StatusCode != System.Net.HttpStatusCode.OK)
                {
                    Logger.LogWarning("PublicController => Online status request returned {StatusCode}", response.StatusCode);
                    return dictOnlineStatus;
                }

                string jsonData = await response.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(jsonData))
                {
                    return dictOnlineStatus;
                }

                var onlineStatusList = JsonConvert.DeserializeObject<ChargePointStatus[]>(jsonData);
                if (onlineStatusList == null)
                {
                    return dictOnlineStatus;
                }

                foreach (var status in onlineStatusList)
                {
                    if (status?.Id == null)
                    {
                        continue;
                    }

                    dictOnlineStatus[status.Id] = status;
                }
            }
            catch (Exception exp)
            {
                Logger.LogWarning(exp, "PublicController => Error loading online status feed: {Message}", exp.Message);
            }

            return dictOnlineStatus;
        }

        private static string BuildConnectorDisplayName(ConnectorStatus connector, int connectorId)
        {
            if (connector == null)
            {
                return $"Connector {connectorId}";
            }

            return string.IsNullOrWhiteSpace(connector.ConnectorName)
                ? $"Connector {connector.ConnectorId}"
                : connector.ConnectorName;
        }

        private static string ResolvePublicConnectorPrimaryLabel(string displayName, string publicConnectorShortCode, int connectorId)
        {
            if (!string.IsNullOrWhiteSpace(displayName) &&
                !string.Equals(displayName, $"Connector {connectorId}", StringComparison.OrdinalIgnoreCase))
            {
                return displayName;
            }

            if (!string.IsNullOrWhiteSpace(publicConnectorShortCode))
            {
                return publicConnectorShortCode;
            }

            return string.IsNullOrWhiteSpace(displayName)
                ? $"Connector {connectorId}"
                : displayName;
        }

        private static string NormalizePublicDisplayCode(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? null
                : value.Trim();
        }

        private static string BuildPublicConnectorCode(string publicDisplayCode, int connectorId)
        {
            string normalized = NormalizePublicDisplayCode(publicDisplayCode);
            if (string.IsNullOrWhiteSpace(normalized) || connectorId <= 0)
            {
                return null;
            }

            return $"{normalized}*{connectorId.ToString(CultureInfo.InvariantCulture)}";
        }

        private static string BuildPublicConnectorShortCode(string publicDisplayCode, int connectorId)
        {
            string normalized = NormalizePublicDisplayCode(publicDisplayCode);
            if (string.IsNullOrWhiteSpace(normalized) || connectorId <= 0)
            {
                return null;
            }

            string lastSegment = normalized
                .Split(new[] { '*', '-' }, StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault();

            if (string.IsNullOrWhiteSpace(lastSegment))
            {
                return null;
            }

            return $"{lastSegment}*{connectorId.ToString(CultureInfo.InvariantCulture)}";
        }

        private static string GetLiveConnectorRawStatus(ChargePointStatus liveChargePointStatus, int connectorId)
        {
            if (liveChargePointStatus?.OnlineConnectors != null &&
                liveChargePointStatus.OnlineConnectors.TryGetValue(connectorId, out var liveConnectorStatus))
            {
                string liveOcppStatus = NormalizeConnectorStatus(liveConnectorStatus.OcppStatus);
                if (!string.IsNullOrWhiteSpace(liveOcppStatus))
                {
                    return IsUndefinedConnectorStatus(liveOcppStatus) ? null : liveOcppStatus;
                }

                return liveConnectorStatus.Status == ConnectorStatusEnum.Undefined ? null : liveConnectorStatus.Status.ToString();
            }

            return null;
        }

        private string GetEffectiveConnectorStatus(
            string lastStatus,
            string liveRawStatus,
            DateTime? lastStatusTime,
            bool hasOpenTransaction,
            bool hasActiveReservation,
            bool isChargePointOnline,
            DateTime nowUtc)
        {
            if (hasOpenTransaction || hasActiveReservation)
            {
                return "Occupied";
            }

            string effectiveRawStatus = !string.IsNullOrWhiteSpace(liveRawStatus)
                ? liveRawStatus
                : lastStatus;

            if (!isChargePointOnline && IsConnectorStatusStale(lastStatusTime, nowUtc))
            {
                return "Offline";
            }

            if (string.IsNullOrWhiteSpace(effectiveRawStatus))
            {
                return "Offline";
            }

            if (IsAvailableStatus(effectiveRawStatus))
            {
                return "Available";
            }

            if (IsOfflineLikeStatus(effectiveRawStatus))
            {
                return "Offline";
            }

            return "Occupied";
        }

        private static string BuildAvailabilityMessage(string effectiveStatus, string occupancyReason)
        {
            if (string.Equals(occupancyReason, "ActiveReservation", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(effectiveStatus, "Occupied", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(occupancyReason, "ActiveReservation", StringComparison.OrdinalIgnoreCase)
                    ? "This connector is temporarily reserved during checkout. If it is your session, continue in the same browser or choose another connector."
                    : "This connector is currently in use. Please stop the active session first or choose another connector.";
            }

            if (string.Equals(effectiveStatus, "Offline", StringComparison.OrdinalIgnoreCase))
            {
                return "This connector is currently offline. Please try again later or choose another connector.";
            }

            return null;
        }

        private static string BuildAggregateConnectorStatus(int availableConnectorCount, int occupiedConnectorCount)
        {
            if (availableConnectorCount > 0)
            {
                return "Available";
            }

            if (occupiedConnectorCount > 0)
            {
                return "Occupied";
            }

            return "Offline";
        }

        private int GetPublicStatusFreshnessMinutes()
        {
            int heartbeatIntervalSeconds = Config?.GetValue<int?>("HeartBeatInterval") ?? 300;
            int derivedThreshold = (int)Math.Ceiling(Math.Max(heartbeatIntervalSeconds, 300) * 3 / 60d);
            return Math.Max(15, derivedThreshold);
        }

        private bool IsConnectorStatusStale(DateTime? lastStatusTime, DateTime nowUtc)
        {
            if (!lastStatusTime.HasValue)
            {
                return true;
            }

            return lastStatusTime.Value < nowUtc.AddMinutes(-GetPublicStatusFreshnessMinutes());
        }

        private static bool IsAvailableStatus(string status)
        {
            return string.Equals(status, "Available", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, "Preparing", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsOfflineLikeStatus(string status)
        {
            string normalizedStatus = NormalizeConnectorStatus(status);
            return string.Equals(normalizedStatus, "Faulted", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalizedStatus, "Unavailable", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalizedStatus, "Unknown", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalizedStatus, "Undefined", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalizedStatus, "Offline", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsUndefinedConnectorStatus(string status)
        {
            string normalizedStatus = NormalizeConnectorStatus(status);
            return string.Equals(normalizedStatus, "Undefined", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalizedStatus, "0", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeConnectorStatus(string status)
        {
            return string.IsNullOrWhiteSpace(status) ? null : status.Trim();
        }

        private sealed class PublicConnectorState
        {
            public int ConnectorId { get; set; }
            public string DisplayName { get; set; }
            public string PublicConnectorCode { get; set; }
            public string PublicConnectorShortCode { get; set; }
            public string EffectiveStatus { get; set; }
            public DateTime? EffectiveStatusTime { get; set; }
            public string OccupancyReason { get; set; }
            public string AvailabilityMessage { get; set; }
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
                string.Equals(reservationStatus, ChargePaymentReservationState.WaitingForDisconnect, StringComparison.OrdinalIgnoreCase) ||
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
