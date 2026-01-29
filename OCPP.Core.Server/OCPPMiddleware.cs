/*
 * OCPP.Core - https://github.com/dallmann-consulting/OCPP.Core
 * Copyright (C) 2020-2024 dallmann consulting GmbH.
 * All Rights Reserved.
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using OCPP.Core.Database;
using OCPP.Core.Server.Payments;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace OCPP.Core.Server
{
    public partial class OCPPMiddleware
    {
        // Supported OCPP protocols (in order)
        private const string Protocol_OCPP16 = "ocpp1.6";
        private const string Protocol_OCPP201 = "ocpp2.0.1";
        private const string Protocol_OCPP21 = "ocpp2.1";
        private static readonly string[] SupportedProtocols = { Protocol_OCPP16, Protocol_OCPP201, Protocol_OCPP21 };

        // RegExp for splitting ocpp message parts
        // ^\[\s*(\d)\s*,\s*\"([^"]*)\"\s*,(?:\s*\"(\w*)\"\s*,)?\s*(.*)\s*\]$
        // Third block is optional, because responses don't have an action
        private static string MessageRegExp = "^\\[\\s*(\\d+)\\s*,\\s*\"([^\"]+)\"\\s*,\\s*(?:\\s*\"(\\w+)\"\\s*,)?\\s*(\\{[^}]+\\}|\\{[\\s\\S]*?\\})\\s*\\]$";

        // Timeout in ms for waiting for responses from charger (for communication scheme server => charger)
        private int TimoutWaitForCharger = 60 * 1000;   // 60 seconds

        private static readonly TimeSpan PersistedStatusStaleAfter = TimeSpan.FromMinutes(10);

        private readonly RequestDelegate _next;
        private readonly ILoggerFactory _logFactory;
        private readonly ILogger _logger;
        private readonly IConfiguration _configuration;
        private readonly IPaymentCoordinator _paymentCoordinator;
        private readonly StartChargingMediator _startMediator;
        private readonly ReservationLinkService _reservationLinkService;
        private readonly Func<DateTime> _utcNow;
        private const string ConnectorBusyStatus = "ConnectorBusy";
        private bool ReservationProfileEnabled => _configuration.GetValue<bool>("Payments:EnableReservationProfile", false);

        // Dictionary with status objects for each charge point
        private static Dictionary<string, ChargePointStatus> _chargePointStatusDict = new Dictionary<string, ChargePointStatus>();

        // Dictionary for processing asynchronous API calls
        private Dictionary<string, OCPPMessage> _requestQueue = new Dictionary<string, OCPPMessage>();

        public OCPPMiddleware(RequestDelegate next, ILoggerFactory logFactory, IConfiguration configuration, IPaymentCoordinator paymentCoordinator, StartChargingMediator startMediator, ReservationLinkService reservationLinkService)
        {
            _next = next;
            _logFactory = logFactory;
            _configuration = configuration;
            _paymentCoordinator = paymentCoordinator;
            _startMediator = startMediator;
            _reservationLinkService = reservationLinkService;
            _utcNow = () => DateTime.UtcNow;

            _logger = logFactory.CreateLogger("OCPPMiddleware");

            LoadExtensions();
            if (_startMediator != null)
            {
                _startMediator.TryStartAsync = TryStartChargingAsync;
            }
        }

        public async Task Invoke(HttpContext context, OCPPCoreContext dbContext)
        {
            _logger.LogTrace("OCPPMiddleware => Websocket request: Path='{0}'", context.Request.Path);

            ChargePointStatus chargePointStatus = null;

            if (context.Request.Path.StartsWithSegments("/OCPP"))
            {
                string chargepointIdentifier;
                string[] parts = context.Request.Path.Value.Split('/');
                if (string.IsNullOrWhiteSpace(parts[parts.Length - 1]))
                {
                    // (Last part - 1) is chargepoint identifier
                    chargepointIdentifier = parts[parts.Length - 2];
                }
                else
                {
                    // Last part is chargepoint identifier
                    chargepointIdentifier = parts[parts.Length - 1];
                }
                _logger.LogInformation("OCPPMiddleware => Connection request with chargepoint identifier = '{0}'", chargepointIdentifier);

                // Known chargepoint?
                if (!string.IsNullOrWhiteSpace(chargepointIdentifier))
                {
                    ChargePoint chargePoint = dbContext.Find<ChargePoint>(chargepointIdentifier);
                    if (chargePoint != null)
                    {
                        _logger.LogInformation("OCPPMiddleware => SUCCESS: Found chargepoint with identifier={0}", chargePoint.ChargePointId);

                        // Check optional chargepoint authentication
                        if (!string.IsNullOrWhiteSpace(chargePoint.Username))
                        {
                            // Chargepoint MUST send basic authentication header

                            bool basicAuthSuccess = false;
                            string authHeader = context.Request.Headers["Authorization"];
                            if (!string.IsNullOrEmpty(authHeader))
                            {
                                string[] cred = System.Text.ASCIIEncoding.ASCII.GetString(Convert.FromBase64String(authHeader.Substring(6))).Split(':');
                                if (cred.Length == 2 && chargePoint.Username == cred[0] && chargePoint.Password == cred[1])
                                {
                                    // Authentication match => OK
                                    _logger.LogInformation("OCPPMiddleware => SUCCESS: Basic authentication for chargepoint '{0}' match", chargePoint.ChargePointId);
                                    basicAuthSuccess = true;
                                }
                                else
                                {
                                    // Authentication does NOT match => Failure
                                    _logger.LogWarning("OCPPMiddleware => FAILURE: Basic authentication for chargepoint '{0}' does NOT match", chargePoint.ChargePointId);
                                }
                            }
                            if (basicAuthSuccess == false)
                            {
                                context.Response.Headers.Append("WWW-Authenticate", "Basic realm=\"OCPP.Core\"");
                                context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                                return;
                            }

                        }
                        else if (!string.IsNullOrWhiteSpace(chargePoint.ClientCertThumb))
                        {
                            // Chargepoint MUST send basic authentication header

                            bool certAuthSuccess = false;
                            X509Certificate2 clientCert = context.Connection.ClientCertificate;
                            if (clientCert != null)
                            {
                                if (clientCert.Thumbprint.Equals(chargePoint.ClientCertThumb, StringComparison.InvariantCultureIgnoreCase))
                                {
                                    // Authentication match => OK
                                    _logger.LogInformation("OCPPMiddleware => SUCCESS: Certificate authentication for chargepoint '{0}' match", chargePoint.ChargePointId);
                                    certAuthSuccess = true;
                                }
                                else
                                {
                                    // Authentication does NOT match => Failure
                                    _logger.LogWarning("OCPPMiddleware => FAILURE: Certificate authentication for chargepoint '{0}' does NOT match", chargePoint.ChargePointId);
                                }
                            }
                            if (certAuthSuccess == false)
                            {
                                context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                                return;
                            }
                        }
                        else
                        {
                            _logger.LogInformation("OCPPMiddleware => No authentication for chargepoint '{0}' configured", chargePoint.ChargePointId);
                        }

                        // Store chargepoint data
                        chargePointStatus = new ChargePointStatus(chargePoint);
                    }
                    else
                    {
                        _logger.LogWarning("OCPPMiddleware => FAILURE: Found no chargepoint with identifier={0}", chargepointIdentifier);
                    }
                }

                if (chargePointStatus != null)
                {
                    if (context.WebSockets.IsWebSocketRequest)
                    {
                        // Match supported sub protocols
                        string subProtocol = null;
                        foreach (string supportedProtocol in SupportedProtocols)
                        {
                            if (context.WebSockets.WebSocketRequestedProtocols.Contains(supportedProtocol))
                            {
                                subProtocol = supportedProtocol;
                                break;
                            }
                        }
                        if (string.IsNullOrEmpty(subProtocol))
                        {
                            // Not matching protocol! => failure
                            string protocols = string.Empty;
                            foreach (string p in context.WebSockets.WebSocketRequestedProtocols)
                            {
                                if (string.IsNullOrEmpty(protocols)) protocols += ",";
                                protocols += p;
                            }
                            _logger.LogWarning("OCPPMiddleware => No supported sub-protocol in '{0}' from charge station '{1}'", protocols, chargepointIdentifier);
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        }
                        else
                        {
                            chargePointStatus.Protocol = subProtocol;

                            bool statusSuccess = false;
                            try
                            {
                                _logger.LogTrace("OCPPMiddleware => Store/Update status object");

                                lock (_chargePointStatusDict)
                                {
                                    // Replace any existing status entry to avoid duplicate-key errors on reconnects
                                    _chargePointStatusDict[chargepointIdentifier] = chargePointStatus;
                                    statusSuccess = true;
                                }
                            }
                            catch (Exception exp)
                            {
                                _logger.LogError(exp, "OCPPMiddleware => Error storing status object in dictionary => refuse connection");
                                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                            }

                            if (statusSuccess)
                            {
                                // Handle socket communication
                                _logger.LogTrace("OCPPMiddleware => Waiting for message...");

                                try
                                {
                                    using (WebSocket webSocket = await context.WebSockets.AcceptWebSocketAsync(subProtocol))
                                    {
                                        _logger.LogTrace("OCPPMiddleware => WebSocket connection with charge point '{0}'", chargepointIdentifier);
                                        chargePointStatus.WebSocket = webSocket;

                                        if (subProtocol == Protocol_OCPP21)
                                        {
                                            // OCPP V2.1
                                            await Receive21(chargePointStatus, context, dbContext);
                                        }
                                        else if (subProtocol == Protocol_OCPP201)
                                        {
                                            // OCPP V2.0
                                            await Receive20(chargePointStatus, context, dbContext);
                                        }
                                        else
                                        {
                                            // OCPP V1.6
                                            await Receive16(chargePointStatus, context, dbContext);
                                        }
                                    }
                                }
                                catch (Exception exp)
                                {
                                    if ((exp is WebSocketException || exp is TaskCanceledException) &&
                                        chargePointStatus?.WebSocket?.State != WebSocketState.Open)
                                    {
                                        _logger.LogInformation("OCPPMiddleware => WebSocket connection lost on charge point '{0}' with state '{1}' / close-status '{2}'", chargePointStatus.Id, chargePointStatus.WebSocket.State, chargePointStatus.WebSocket.CloseStatus);
                                    }
                                    else
                                    {
                                        _logger.LogTrace("OCPPMiddleware => Receive() unhandled exception '{0}'", exp.Message);
                                    }

                                    // Receive loop has ended anyway
                                    // => Close connection
                                    if (chargePointStatus?.WebSocket?.State == WebSocketState.Open)
                                    {
                                        try
                                        {
                                            await chargePointStatus?.WebSocket.CloseAsync(WebSocketCloseStatus.InternalServerError, null, CancellationToken.None);
                                        }
                                        catch { }
                                    }
                                    // Remove chargepoint status
                                    _chargePointStatusDict.Remove(chargePointStatus.Id);
                                }
                            }
                        }
                    }
                    else
                    {
                        // no websocket request => failure
                        _logger.LogWarning("OCPPMiddleware => Non-Websocket request");
                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    }
                }
                else
                {
                    // unknown chargepoint
                    _logger.LogTrace("OCPPMiddleware => no chargepoint: http 412");
                    context.Response.StatusCode = (int)HttpStatusCode.PreconditionFailed;
                }
            }
            else if (context.Request.Path.StartsWithSegments("/API"))
            {
                // format: /API/<command>[/chargepointId[/connectorId[/parameter]]]
                string[] urlParts = context.Request.Path.Value.Split('/');
                bool isStripeWebhook = urlParts.Length >= 4 &&
                                       string.Equals(urlParts[2], "Payments", StringComparison.OrdinalIgnoreCase) &&
                                       string.Equals(urlParts[3], "Webhook", StringComparison.OrdinalIgnoreCase);

                // Check authentication (X-API-Key)
                if (!isStripeWebhook)
                {
                    string apiKeyConfig = _configuration.GetValue<string>("ApiKey");
                    if (!string.IsNullOrWhiteSpace(apiKeyConfig))
                    {
                        // ApiKey specified => check request
                        string apiKeyCaller = context.Request.Headers["X-API-Key"].FirstOrDefault();
                        if (apiKeyConfig == apiKeyCaller)
                        {
                            // API-Key matches
                            _logger.LogInformation("OCPPMiddleware => Success: X-API-Key matches");
                        }
                        else
                        {
                            // API-Key does NOT matches => authentication failure!!!
                            _logger.LogWarning("OCPPMiddleware => Failure: Wrong X-API-Key! Caller='{0}'", apiKeyCaller);
                            context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                            return;
                        }
                    }
                    else
                    {
                        // No API-Key configured => no authentication
                        _logger.LogWarning("OCPPMiddleware => No X-API-Key configured!");
                    }
                }
                else
                {
                    _logger.LogDebug("OCPPMiddleware => Skipping API key check for Stripe webhook");
                }

                if (urlParts.Length >= 3)
                {
                    string cmd = urlParts[2];
                    string urlChargePointId = (urlParts.Length >= 4) ? urlParts[3] : null;
                    string urlConnectorId = (urlParts.Length >= 5) ? urlParts[4] : null;
                    string urlParam = (urlParts.Length >= 6) ? urlParts[5] : null;
                    _logger.LogTrace("OCPPMiddleware => cmd='{0}' / cpId='{1}' / conId='{2}' / param='{3}' / FullPath='{4}')", cmd, urlChargePointId, urlConnectorId, urlParam, context.Request.Path.Value);

                    if (cmd == "Status")
                    {
                        try
                        {
                            List<ChargePointStatus> statusList = new List<ChargePointStatus>();
                            foreach (ChargePointStatus status in _chargePointStatusDict.Values)
                            {
                                if (status.WebSocket != null && status.WebSocket.State == WebSocketState.Open)
                                {
                                    statusList.Add(status);
                                }
                            }
                            string jsonStatus = JsonConvert.SerializeObject(statusList);
                            context.Response.ContentType = "application/json";
                            await context.Response.WriteAsync(jsonStatus);
                        }
                        catch (Exception exp)
                        {
                            _logger.LogError(exp, "OCPPMiddleware => Error: {0}", exp.Message);
                            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                        }
                    }
                    else if (cmd == "Reset")
                    {
                        if (!string.IsNullOrEmpty(urlChargePointId))
                        {
                            try
                            {
                                ChargePointStatus status = null;
                                if (_chargePointStatusDict.TryGetValue(urlChargePointId, out status))
                                {
                                    // Send message to chargepoint
                                    if (status.Protocol == Protocol_OCPP21)
                                    {
                                        // OCPP V2.1
                                        await Reset21(status, context, dbContext);
                                    }
                                    else if (status.Protocol == Protocol_OCPP201)
                                    {
                                        // OCPP V2.0
                                        await Reset20(status, context, dbContext);
                                    }
                                    else
                                    {
                                        // OCPP V1.6
                                        await Reset16(status, context, dbContext);
                                    }
                                }
                                else
                                {
                                    // Chargepoint offline
                                    _logger.LogError("OCPPMiddleware SoftReset => Chargepoint offline: {0}", urlChargePointId);
                                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                                }
                            }
                            catch (Exception exp)
                            {
                                _logger.LogError(exp, "OCPPMiddleware SoftReset => Error: {0}", exp.Message);
                                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                            }
                        }
                        else
                        {
                            _logger.LogError("OCPPMiddleware SoftReset => Missing chargepoint ID");
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        }
                    }
                    else if (cmd == "UnlockConnector")
                    {
                        if (!string.IsNullOrEmpty(urlChargePointId))
                        {
                            try
                            {
                                ChargePointStatus status = null;
                                if (_chargePointStatusDict.TryGetValue(urlChargePointId, out status))
                                {
                                    // Send message to chargepoint
                                    if (status.Protocol == Protocol_OCPP21)
                                    {
                                        // OCPP V2.1
                                        await UnlockConnector21(status, context, dbContext, urlConnectorId);
                                    }
                                    else if (status.Protocol == Protocol_OCPP201)
                                    {
                                        // OCPP V2.0
                                        await UnlockConnector20(status, context, dbContext, urlConnectorId);
                                    }
                                    else
                                    {
                                        // OCPP V1.6
                                        await UnlockConnector16(status, context, dbContext, urlConnectorId);
                                    }
                                }
                                else
                                {
                                    // Chargepoint offline
                                    _logger.LogError("OCPPMiddleware UnlockConnector => Chargepoint offline: {0}", urlChargePointId);
                                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                                }
                            }
                            catch (Exception exp)
                            {
                                _logger.LogError(exp, "OCPPMiddleware UnlockConnector => Error: {0}", exp.Message);
                                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                            }
                        }
                        else
                        {
                            _logger.LogError("OCPPMiddleware UnlockConnector => Missing chargepoint ID");
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        }
                    }
                    else if (cmd == "SetChargingLimit")
                    {
                        if (!string.IsNullOrEmpty(urlChargePointId))
                        {
                            if (!string.IsNullOrEmpty(urlParam))
                            {
                                string pattern = @"^([0-9]+)([AWaw])$";
                                Regex regex = new Regex(pattern, RegexOptions.IgnoreCase);
                                Match match = regex.Match(urlParam);
                                if (match.Success && match.Groups.Count == 3)
                                {
                                    int power = int.Parse(match.Groups[1].Value);
                                    string unit = match.Groups[2].Value;

                                    try
                                    {
                                        ChargePointStatus status = null;
                                        if (_chargePointStatusDict.TryGetValue(urlChargePointId, out status))
                                        {
                                            // Send message to chargepoint
                                            if (status.Protocol == Protocol_OCPP21)
                                            {
                                                // OCPP V2.1
                                                await SetChargingProfile21(status, context, dbContext, urlConnectorId, power, unit);
                                            }
                                            else if (status.Protocol == Protocol_OCPP201)
                                            {
                                                // OCPP V2.0
                                                await SetChargingProfile20(status, context, dbContext, urlConnectorId, power, unit);
                                            }
                                            else
                                            {
                                                // OCPP V1.6
                                                await SetChargingProfile16(status, context, dbContext, urlConnectorId, power, unit);
                                            }
                                        }
                                        else
                                        {
                                            // Chargepoint offline
                                            _logger.LogError("OCPPMiddleware SetChargingProfile => Chargepoint offline: {0}", urlChargePointId);
                                            context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                                        }
                                    }
                                    catch (Exception exp)
                                    {
                                        _logger.LogError(exp, "OCPPMiddleware SetChargingProfile => Error: {0}", exp.Message);
                                        context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                                    }
                                }
                                else
                                {
                                    _logger.LogError("OCPPMiddleware SetChargingProfile => Bad parameter (power)");
                                    context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                                }
                            }
                            else
                            {
                                _logger.LogError("OCPPMiddleware SetChargingProfile => Missing parameter (power)");
                                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                            }
                        }
                        else
                        {
                            _logger.LogError("OCPPMiddleware SetChargingProfile => Missing chargepoint ID");
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        }
                    }
                    else if (cmd == "ClearChargingLimit")
                    {
                        if (!string.IsNullOrEmpty(urlChargePointId))
                        {
                            try
                            {
                                ChargePointStatus status = null;
                                if (_chargePointStatusDict.TryGetValue(urlChargePointId, out status))
                                {
                                    // Send message to chargepoint
                                    if (status.Protocol == Protocol_OCPP21)
                                    {
                                        // OCPP V2.1
                                        await ClearChargingProfile21(status, context, dbContext, urlConnectorId);
                                    }
                                    else if (status.Protocol == Protocol_OCPP201)
                                    {
                                        // OCPP V2.0
                                        await ClearChargingProfile20(status, context, dbContext, urlConnectorId);
                                    }
                                    else
                                    {
                                        // OCPP V1.6
                                        await ClearChargingProfile16(status, context, dbContext, urlConnectorId);
                                    }
                                }
                                else
                                {
                                    // Chargepoint offline
                                    _logger.LogError("OCPPMiddleware ClearChargingProfile => Chargepoint offline: {0}", urlChargePointId);
                                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                                }
                            }
                            catch (Exception exp)
                            {
                                _logger.LogError(exp, "OCPPMiddleware ClearChargingProfile => Error: {0}", exp.Message);
                                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                            }
                        }
                        else
                        {
                            _logger.LogError("OCPPMiddleware ClearChargingProfile => Missing chargepoint ID");
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        }
                    }
                    else if (cmd == "GetConfiguration")
                    {
                        if (!string.IsNullOrEmpty(urlChargePointId))
                        {
                            try
                            {
                                if (_chargePointStatusDict.TryGetValue(urlChargePointId, out var status))
                                {
                                    if (status.Protocol == Protocol_OCPP16)
                                    {
                                        await GetConfiguration16(status, context, dbContext, urlConnectorId);
                                    }
                                    else
                                    {
                                        context.Response.StatusCode = (int)HttpStatusCode.NotImplemented;
                                        context.Response.ContentType = "application/json";
                                        await context.Response.WriteAsync("{\"status\":\"NotSupported\"}");
                                    }
                                }
                                else
                                {
                                    _logger.LogError("OCPPMiddleware GetConfiguration => Chargepoint offline: {0}", urlChargePointId);
                                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                                }
                            }
                            catch (Exception exp)
                            {
                                _logger.LogError(exp, "OCPPMiddleware GetConfiguration => Error: {0}", exp.Message);
                                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                            }
                        }
                        else
                        {
                            _logger.LogError("OCPPMiddleware GetConfiguration => Missing chargepoint ID");
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        }
                    }
                    else if (cmd == "ChangeConfiguration")
                    {
                        if (!string.IsNullOrEmpty(urlChargePointId) && !string.IsNullOrEmpty(urlConnectorId) && urlParam != null)
                        {
                            try
                            {
                                if (_chargePointStatusDict.TryGetValue(urlChargePointId, out var status))
                                {
                                    if (status.Protocol == Protocol_OCPP16)
                                    {
                                        await ChangeConfiguration16(status, context, dbContext, urlConnectorId, urlParam);
                                    }
                                    else
                                    {
                                        context.Response.StatusCode = (int)HttpStatusCode.NotImplemented;
                                        context.Response.ContentType = "application/json";
                                        await context.Response.WriteAsync("{\"status\":\"NotSupported\"}");
                                    }
                                }
                                else
                                {
                                    _logger.LogError("OCPPMiddleware ChangeConfiguration => Chargepoint offline: {0}", urlChargePointId);
                                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                                }
                            }
                            catch (Exception exp)
                            {
                                _logger.LogError(exp, "OCPPMiddleware ChangeConfiguration => Error: {0}", exp.Message);
                                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                            }
                        }
                        else
                        {
                            _logger.LogError("OCPPMiddleware ChangeConfiguration => Missing chargepoint ID, key or value");
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        }
                    }
                    else if (cmd == "StartTransaction")
                    {
                        if (!string.IsNullOrEmpty(urlChargePointId))
                        {
                            // Parse connector id (int value)
                            if (!string.IsNullOrEmpty(urlConnectorId) && int.TryParse(urlConnectorId, out int connectorId))
                            {
                                if (!string.IsNullOrEmpty(urlParam))
                                {
                                    try
                                    {
                                        ChargePointStatus status = null;
                                        if (_chargePointStatusDict.TryGetValue(urlChargePointId, out status))
                                        {
                                            // Send message to chargepoint
                                            if (status.Protocol == Protocol_OCPP21)
                                            {
                                                // OCPP V2.1
                                                await RequestStartTransaction21(status, context, dbContext, urlConnectorId, urlParam);
                                            }
                                            else if (status.Protocol == Protocol_OCPP201)
                                            {
                                                // OCPP V2.0
                                                await RequestStartTransaction20(status, context, dbContext, urlConnectorId, urlParam);
                                            }
                                            else
                                            {
                                                // OCPP V1.6
                                                await RemoteStartTransaction16(status, context, dbContext, urlConnectorId, urlParam);
                                            }
                                        }
                                        else
                                        {
                                            // Chargepoint offline
                                            _logger.LogError("OCPPMiddleware StartTransaction => Chargepoint offline: {0}", urlChargePointId);
                                            context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                                        }
                                    }
                                    catch (Exception exp)
                                    {
                                        _logger.LogError(exp, "OCPPMiddleware StartTransaction => Error: {0}", exp.Message);
                                        context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                                    }
                                }
                                else
                                {
                                    _logger.LogError("OCPPMiddleware StartTransaction => Missing tokenId");
                                    context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                                }
                            }
                            else
                            {
                                _logger.LogError($"OCPPMiddleware StartTransaction => Bad connector ID: '{0}'", urlConnectorId);
                                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                            }
                        }
                        else
                        {
                            _logger.LogError("OCPPMiddleware StartTransaction => Missing chargepoint ID");
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        }
                    }
                    else if (cmd == "StopTransaction")
                    {
                        if (!string.IsNullOrEmpty(urlChargePointId))
                        {
                            try
                            {
                                ChargePointStatus status = null;
                                if (_chargePointStatusDict.TryGetValue(urlChargePointId, out status))
                                {
                                    if (int.TryParse(urlConnectorId, out int connectorId))
                                    {
                                        // Check last (open) transaction
                                        Transaction transaction = dbContext.Transactions
                                            .Where(t => t.ChargePointId == urlChargePointId && t.ConnectorId == connectorId)
                                            .OrderByDescending(t => t.TransactionId)
                                            .FirstOrDefault();

                                        if (transaction != null && !transaction.StopTime.HasValue)
                                        {
                                            // Send message to chargepoint
                                            if (status.Protocol == Protocol_OCPP21)
                                            {
                                                // OCPP V2.1
                                                await RequestStopTransaction21(status, context, dbContext, urlConnectorId, transaction.Uid);
                                            }
                                            else if (status.Protocol == Protocol_OCPP201)
                                            {
                                                // OCPP V2.0
                                                await RequestStopTransaction20(status, context, dbContext, urlConnectorId, transaction.Uid);
                                            }
                                            else
                                            {
                                                // OCPP V1.6
                                                await RemoteStopTransaction16(status, context, dbContext, urlConnectorId, transaction.TransactionId);
                                            }
                                        }
                                        else
                                        {
                                            _logger.LogError("OCPPMiddleware StopTransaction => connector '{0}' has no open transaction", urlConnectorId);
                                            context.Response.StatusCode = (int)HttpStatusCode.FailedDependency;
                                        }
                                    }
                                    else
                                    {
                                        _logger.LogError("OCPPMiddleware StopTransaction => invalid connector ID: '{0}'", urlConnectorId);
                                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                                    }
                                }
                                else
                                {
                                    // Chargepoint offline
                                    _logger.LogError("OCPPMiddleware StopTransaction => Chargepoint offline: {0}", urlChargePointId);
                                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                                }
                            }
                            catch (Exception exp)
                            {
                                _logger.LogError(exp, "OCPPMiddleware StopTransaction => Error: {0}", exp.Message);
                                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                            }
                        }
                        else
                        {
                            _logger.LogError("OCPPMiddleware StopTransaction => Missing chargepoint ID");
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        }
                    }
                    else if (string.Equals(cmd, "Payments", StringComparison.OrdinalIgnoreCase))
                    {
                        await HandlePaymentsAsync(context, dbContext, urlParts);
                    }
                    else
                    {
                        // Unknown action/function
                        _logger.LogWarning("OCPPMiddleware => action/function: {0}", cmd);
                        context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    }
                }
            }
            else if (context.Request.Path.StartsWithSegments("/"))
            {
                try
                {
                    bool showIndexInfo = _configuration.GetValue<bool>("ShowIndexInfo");
                    if (showIndexInfo)
                    {
                        _logger.LogTrace("OCPPMiddleware => Index status page");

                        context.Response.ContentType = "text/plain";
                        await context.Response.WriteAsync(string.Format("Running...\r\n\r\n{0} chargepoints connected", _chargePointStatusDict.Values.Count));
                    }
                    else
                    {
                        _logger.LogInformation("OCPPMiddleware => Root path with deactivated index page");
                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    }
                }
                catch (Exception exp)
                {
                    _logger.LogError(exp, "OCPPMiddleware => Error: {0}", exp.Message);
                    context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                }
            }
            else
            {
                _logger.LogWarning("OCPPMiddleware => Bad path request");
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
            }
        }

        private async Task HandlePaymentsAsync(HttpContext context, OCPPCoreContext dbContext, string[] urlParts)
        {
            string action = (urlParts.Length >= 4) ? urlParts[3] : null;

            if (string.Equals(action, "Webhook", StringComparison.OrdinalIgnoreCase))
            {
                await HandlePaymentWebhookAsync(context, dbContext);
                return;
            }

            if (_paymentCoordinator == null || !_paymentCoordinator.IsEnabled)
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("{\"status\":\"Disabled\"}");
                return;
            }

            if (string.Equals(action, "Create", StringComparison.OrdinalIgnoreCase))
            {
                await HandlePaymentCreateAsync(context, dbContext);
            }
            else if (string.Equals(action, "Confirm", StringComparison.OrdinalIgnoreCase))
            {
                await HandlePaymentConfirmAsync(context, dbContext);
            }
            else if (string.Equals(action, "Cancel", StringComparison.OrdinalIgnoreCase))
            {
                await HandlePaymentCancelAsync(context, dbContext);
            }
            else if (string.Equals(action, "Status", StringComparison.OrdinalIgnoreCase))
            {
                await HandlePaymentStatusAsync(context, dbContext);
            }
            else
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
            }
        }

        private async Task HandlePaymentCreateAsync(HttpContext context, OCPPCoreContext dbContext)
        {
            if (!HttpMethods.IsPost(context.Request.Method))
            {
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            PaymentSessionRequest request = null;
            try
            {
                string body = await ReadRequestBodyAsync(context);
                request = JsonConvert.DeserializeObject<PaymentSessionRequest>(body);
            }
            catch (Exception exp)
            {
                _logger.LogError(exp, "Payments/Create => Invalid request payload");
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                return;
            }

            if (request == null ||
                string.IsNullOrWhiteSpace(request.ChargePointId) ||
                string.IsNullOrWhiteSpace(request.ChargeTagId))
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                return;
            }

            _logger.LogInformation("Payments/Create => Incoming request cp={ChargePointId} connector={ConnectorId} tag={ChargeTagId} origin={Origin} ip={RemoteIp} ua={UserAgent}",
                request.ChargePointId,
                request.ConnectorId,
                request.ChargeTagId,
                request.Origin ?? "(none)",
                context.Connection.RemoteIpAddress?.ToString() ?? "(unknown)",
                context.Request.Headers["User-Agent"].FirstOrDefault() ?? "(none)");

            if (!_chargePointStatusDict.TryGetValue(request.ChargePointId, out var status) ||
                status.WebSocket == null ||
                status.WebSocket.State != WebSocketState.Open)
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                await context.Response.WriteAsync("{\"status\":\"ChargerOffline\"}");
                return;
            }

            if (IsConnectorBusy(dbContext, request.ChargePointId, request.ConnectorId, status, null, out var busyReason))
            {
                _logger.LogWarning("Payments/Create => Connector busy: {Reason} (ChargePoint={ChargePointId}, Connector={ConnectorId})",
                    busyReason ?? "Unknown",
                    request.ChargePointId,
                    request.ConnectorId);
                context.Response.StatusCode = (int)HttpStatusCode.Conflict;
                await context.Response.WriteAsync("{\"status\":\"ConnectorBusy\"}");
                return;
            }

            var chargePoint = dbContext.ChargePoints.Find(request.ChargePointId);
            bool isFreeChargePoint = chargePoint != null &&
                                     (chargePoint.FreeChargingEnabled ||
                                      ((chargePoint.PricePerKwh <= 0m) && (chargePoint.ConnectorUsageFeePerMinute <= 0m) && (chargePoint.UserSessionFee <= 0m)));
            bool isFreeChargeTag = HasFreeTagAccess(dbContext, chargePoint, request.ChargeTagId);

            if (isFreeChargePoint || isFreeChargeTag)
            {
                EnsureChargeTagExists(dbContext, request.ChargeTagId);
                string apiResult = await ExecuteRemoteStartAsync(status, dbContext, request.ConnectorId.ToString(), request.ChargeTagId);
                string remoteStatus = ExtractStatusFromApiResult(apiResult);
                if (string.IsNullOrWhiteSpace(apiResult))
                {
                    apiResult = "{\"status\":\"Error\"}";
                }
                // Do not mutate persisted status on remote-start acceptance; rely on charger reports.
                context.Response.StatusCode = (int)HttpStatusCode.OK;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(apiResult);
                return;
            }

            try
            {
                var result = _paymentCoordinator.CreateCheckoutSession(dbContext, request);
                var payload = new
                {
                    status = "Redirect",
                    checkoutUrl = result.CheckoutUrl,
                    reservationId = result.Reservation.ReservationId,
                    currency = result.Reservation.Currency,
                    maxAmountCents = result.Reservation.MaxAmountCents,
                    maxEnergyKwh = result.Reservation.MaxEnergyKwh
                };

                context.Response.StatusCode = (int)HttpStatusCode.OK;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonConvert.SerializeObject(payload));
            }
            catch (InvalidOperationException ioe) when (string.Equals(ioe.Message, ConnectorBusyStatus, StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = (int)HttpStatusCode.Conflict;
                await context.Response.WriteAsync("{\"status\":\"ConnectorBusy\"}");
            }
            catch (Exception exp)
            {
                _logger.LogError(exp, "Payments/Create => Exception: {0}", exp.Message);
                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                await context.Response.WriteAsync("{\"status\":\"Error\"}");
            }
        }

        private async Task HandlePaymentConfirmAsync(HttpContext context, OCPPCoreContext dbContext)
        {
            if (!HttpMethods.IsPost(context.Request.Method))
            {
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            PaymentConfirmRequest request = null;
            try
            {
                string body = await ReadRequestBodyAsync(context);
                request = JsonConvert.DeserializeObject<PaymentConfirmRequest>(body);
            }
            catch (Exception exp)
            {
                _logger.LogError(exp, "Payments/Confirm => Invalid request payload");
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                return;
            }

            if (request == null || request.ReservationId == Guid.Empty || string.IsNullOrWhiteSpace(request.CheckoutSessionId))
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                return;
            }

            _logger.LogInformation("Payments/Confirm => Incoming request reservation={ReservationId} checkoutSession={CheckoutSessionId} ip={RemoteIp}",
                request.ReservationId,
                request.CheckoutSessionId,
                context.Connection.RemoteIpAddress?.ToString() ?? "(unknown)");

            var confirmation = _paymentCoordinator.ConfirmReservation(dbContext, request.ReservationId, request.CheckoutSessionId);
            if (!confirmation.Success)
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonConvert.SerializeObject(new { status = confirmation.Status, error = confirmation.Error }));
                return;
            }

            var reservation = confirmation.Reservation;
            var tryStart = await TryStartChargingAsync(dbContext, reservation, "Confirm");

            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonConvert.SerializeObject(new { status = tryStart.Status, reason = tryStart.Reason }));
        }

        private async Task HandlePaymentCancelAsync(HttpContext context, OCPPCoreContext dbContext)
        {
            if (!HttpMethods.IsPost(context.Request.Method))
            {
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            PaymentCancelRequest request = null;
            try
            {
                string body = await ReadRequestBodyAsync(context);
                request = JsonConvert.DeserializeObject<PaymentCancelRequest>(body);
            }
            catch (Exception exp)
            {
                _logger.LogError(exp, "Payments/Cancel => Invalid request payload");
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                return;
            }

            if (request == null || request.ReservationId == Guid.Empty)
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                return;
            }

            _logger.LogInformation("Payments/Cancel => Incoming cancel reservation={ReservationId} reason={Reason}", request.ReservationId, request.Reason ?? "(none)");

            _paymentCoordinator.CancelReservation(dbContext, request.ReservationId, request.Reason);
            try
            {
                var reservation = dbContext.ChargePaymentReservations.Find(request.ReservationId);
                if (reservation != null && _chargePointStatusDict.TryGetValue(reservation.ChargePointId, out var cpStatus))
                {
                    await BestEffortCancelReservationAsync(reservation, cpStatus, dbContext);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Payments/Cancel => Best-effort CancelReservation failed");
            }
            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"status\":\"Cancelled\"}");
        }

        private async Task HandlePaymentStatusAsync(HttpContext context, OCPPCoreContext dbContext)
        {
            // Accept GET for easy polling
            if (!HttpMethods.IsGet(context.Request.Method))
            {
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            if (!Guid.TryParse(context.Request.Query["reservationId"], out var reservationId) || reservationId == Guid.Empty)
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                await context.Response.WriteAsync("{\"status\":\"Error\",\"message\":\"reservationId required\"}");
                return;
            }

            var reservation = await dbContext.ChargePaymentReservations.FindAsync(reservationId);
            if (reservation == null)
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                await context.Response.WriteAsync("{\"status\":\"NotFound\"}");
                return;
            }

            string liveStatus = null;
            string persistedStatus = null;
            DateTime? persistedStatusTime = null;
            bool activeTx = false;
            bool activeReservation = false;
            StartabilityResult startability = null;

            _chargePointStatusDict.TryGetValue(reservation.ChargePointId, out var cpStatus);
            if (cpStatus?.OnlineConnectors != null &&
                cpStatus.OnlineConnectors.TryGetValue(reservation.ConnectorId, out var online))
            {
                liveStatus = online.Status.ToString();
            }

            var persisted = dbContext.ConnectorStatuses.Find(reservation.ChargePointId, reservation.ConnectorId);
            if (persisted != null)
            {
                persistedStatus = persisted.LastStatus;
                persistedStatusTime = persisted.LastStatusTime;
            }

            activeTx = dbContext.Transactions.Any(t =>
                t.ChargePointId == reservation.ChargePointId &&
                t.ConnectorId == reservation.ConnectorId &&
                t.StopTime == null);

            activeReservation = dbContext.ChargePaymentReservations.Any(r =>
                r.ChargePointId == reservation.ChargePointId &&
                r.ConnectorId == reservation.ConnectorId &&
                PaymentReservationStatus.IsActive(r.Status));

            startability = GetConnectorStartability(dbContext, reservation.ChargePointId, reservation.ConnectorId, cpStatus, reservation.ReservationId);

            var payload = new
            {
                status = reservation.Status,
                reservationId = reservation.ReservationId,
                chargePointId = reservation.ChargePointId,
                connectorId = reservation.ConnectorId,
                transactionId = reservation.TransactionId,
                startTransactionId = reservation.StartTransactionId,
                lastError = reservation.LastError,
                failureCode = reservation.FailureCode,
                failureMessage = reservation.FailureMessage,
                ocppIdTag = reservation.OcppIdTag,
                createdAtUtc = reservation.CreatedAtUtc,
                updatedAtUtc = reservation.UpdatedAtUtc,
                authorizedAtUtc = reservation.AuthorizedAtUtc,
                capturedAtUtc = reservation.CapturedAtUtc,
                startDeadlineAtUtc = reservation.StartDeadlineAtUtc,
                remoteStartSentAtUtc = reservation.RemoteStartSentAtUtc,
                remoteStartAcceptedAtUtc = reservation.RemoteStartAcceptedAtUtc,
                startTransactionAtUtc = reservation.StartTransactionAtUtc,
                stopTransactionAtUtc = reservation.StopTransactionAtUtc,
                lastOcppEventAtUtc = reservation.LastOcppEventAtUtc,
                maxAmountCents = reservation.MaxAmountCents,
                capturedAmountCents = reservation.CapturedAmountCents,
                liveStatus,
                persistedStatus,
                persistedStatusTime,
                activeTransaction = activeTx,
                activeReservation = activeReservation,
                currency = reservation.Currency,
                startable = startability?.Startable ?? false,
                startableReason = startability?.Reason,
                startableReasons = startability?.Reasons,
                startableLiveStatus = startability?.LiveStatus,
                startablePersistedStatus = startability?.PersistedStatus,
                startablePersistedAgeMinutes = startability?.PersistedStatusAgeMinutes
            };

            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonConvert.SerializeObject(payload));
        }

        private bool IsConnectorBusy(OCPPCoreContext dbContext, string chargePointId, int connectorId, ChargePointStatus chargePointStatus, Guid? reservationToIgnore, out string busyReason)
        {
            busyReason = null;

            var status = chargePointStatus;
            if (status == null && _chargePointStatusDict.TryGetValue(chargePointId, out var existingStatus))
            {
                status = existingStatus;
            }

            OnlineConnectorStatus liveConnectorStatus = null;
            if (status != null &&
                status.OnlineConnectors != null &&
                status.OnlineConnectors.TryGetValue(connectorId, out liveConnectorStatus))
            {
                busyReason = $"LiveStatus:{liveConnectorStatus.Status}";
            }

            bool busy = liveConnectorStatus != null &&
                        liveConnectorStatus.Status != ConnectorStatusEnum.Available &&
                        liveConnectorStatus.Status != ConnectorStatusEnum.Preparing;

            int activeTransactionCount = 0;
            try
            {
                activeTransactionCount = dbContext.Transactions.Count(t =>
                    t.ChargePointId == chargePointId &&
                    t.ConnectorId == connectorId &&
                    t.StopTime == null);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "IsConnectorBusy => Unable to count transactions for cp={ChargePointId} connector={ConnectorId}", chargePointId, connectorId);
            }

            if (!busy && activeTransactionCount > 0)
            {
                busy = true;
                busyReason = "OpenTransaction";
            }

            int activeReservationCount = 0;
            try
            {
                activeReservationCount = dbContext.ChargePaymentReservations.Count(r =>
                    r.ChargePointId == chargePointId &&
                    r.ConnectorId == connectorId &&
                    (!reservationToIgnore.HasValue || r.ReservationId != reservationToIgnore.Value) &&
                    PaymentReservationStatus.IsActive(r.Status));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "IsConnectorBusy => Unable to count reservations for cp={ChargePointId} connector={ConnectorId}", chargePointId, connectorId);
            }

            if (!busy && activeReservationCount > 0)
            {
                busy = true;
                busyReason = "ActiveReservation";
            }

            string persistedStatusValue = null;
            double? persistedStatusAgeMinutes = null;
            if (liveConnectorStatus == null)
            {
                var persistedStatus = dbContext.ConnectorStatuses
                    .Where(cs => cs.ChargePointId == chargePointId && cs.ConnectorId == connectorId)
                    .Select(cs => new { cs.LastStatus, cs.LastStatusTime })
                    .FirstOrDefault();

                if (persistedStatus != null)
                {
                    persistedStatusValue = persistedStatus.LastStatus;
                    if (persistedStatus.LastStatusTime.HasValue)
                    {
                        persistedStatusAgeMinutes = (_utcNow() - persistedStatus.LastStatusTime.Value).TotalMinutes;
                    }

                    if (!string.Equals(persistedStatus.LastStatus, "Available", StringComparison.InvariantCultureIgnoreCase) &&
                        !string.Equals(persistedStatus.LastStatus, "Preparing", StringComparison.InvariantCultureIgnoreCase))
                    {
                        bool isStale = persistedStatusAgeMinutes.HasValue &&
                                       TimeSpan.FromMinutes(persistedStatusAgeMinutes.Value) > PersistedStatusStaleAfter;
                        if (!isStale && !busy)
                        {
                            busy = true;
                            busyReason = $"PersistedStatus:{persistedStatus.LastStatus}";
                        }
                        else if (!busy)
                        {
                            busyReason = $"StalePersistedStatus:{persistedStatus.LastStatus}";
                        }
                    }
                }
            }

            busyReason ??= "Available";

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "IsConnectorBusy trace cp={ChargePointId} connector={ConnectorId} decision={Decision} reason={Reason} wsState={WebSocketState} liveStatus={LiveStatus} meterTs={MeterTs} activeTx={ActiveTxCount} activeRes={ActiveResCount} persistedStatus={PersistedStatus} persistedAgeMin={PersistedAge} ignoreReservation={IgnoreReservation}",
                    chargePointId,
                    connectorId,
                    busy ? "Busy" : "Available",
                    busyReason,
                    status?.WebSocket?.State.ToString() ?? "(none)",
                    liveConnectorStatus?.Status.ToString() ?? "(none)",
                    liveConnectorStatus?.MeterValueDate,
                    activeTransactionCount,
                    activeReservationCount,
                    persistedStatusValue ?? "(none)",
                    persistedStatusAgeMinutes?.ToString("0.#") ?? "(n/a)",
                    reservationToIgnore?.ToString() ?? "(none)");
            }

            return busy;
        }

        private bool IsConnectorBusy(OCPPCoreContext dbContext, string chargePointId, int connectorId, ChargePointStatus chargePointStatus = null, Guid? reservationToIgnore = null)
        {
            return IsConnectorBusy(dbContext, chargePointId, connectorId, chargePointStatus, reservationToIgnore, out _);
        }

        private class StartabilityResult
        {
            public bool Startable { get; set; }
            public string Reason { get; set; }
            public List<string> Reasons { get; set; } = new List<string>();
            public string LiveStatus { get; set; }
            public string PersistedStatus { get; set; }
            public double? PersistedStatusAgeMinutes { get; set; }
            public bool ActiveReservation { get; set; }
            public bool ActiveTransaction { get; set; }
            public bool Offline { get; set; }
        }

        private StartabilityResult GetConnectorStartability(OCPPCoreContext dbContext, string chargePointId, int connectorId, ChargePointStatus chargePointStatus, Guid? reservationToIgnore)
        {
            var result = new StartabilityResult { Startable = false, Reason = "Unknown" };

            var status = chargePointStatus;
            if (status == null && _chargePointStatusDict.TryGetValue(chargePointId, out var existingStatus))
            {
                status = existingStatus;
            }

            if (status == null || status.WebSocket == null || status.WebSocket.State != WebSocketState.Open)
            {
                result.Offline = true;
                result.Reason = "Offline";
                result.Reasons.Add(result.Reason);
                return result;
            }

            OnlineConnectorStatus liveConnectorStatus = null;
            if (status.OnlineConnectors != null &&
                status.OnlineConnectors.TryGetValue(connectorId, out liveConnectorStatus))
            {
                result.LiveStatus = liveConnectorStatus.Status.ToString();
                if (liveConnectorStatus.Status == ConnectorStatusEnum.Unavailable || liveConnectorStatus.Status == ConnectorStatusEnum.Faulted)
                {
                    result.Reason = $"Status:{liveConnectorStatus.Status}";
                    result.Reasons.Add(result.Reason);
                    return result;
                }
                if (liveConnectorStatus.Status == ConnectorStatusEnum.Occupied)
                {
                    result.Reason = "Status:Occupied";
                    result.Reasons.Add(result.Reason);
                    return result;
                }
                // Treat Preparing as startable (spec requirement)
                if (liveConnectorStatus.Status == ConnectorStatusEnum.Preparing)
                {
                    result.Reasons.Add("Status:Preparing");
                }
            }

            // If offline but we have a very recent last message, allow start if no other locks
            // This prevents rejection when WS hiccups briefly.
            result.Offline = false;

            result.ActiveTransaction = dbContext.Transactions.Any(t =>
                t.ChargePointId == chargePointId &&
                t.ConnectorId == connectorId &&
                t.StopTime == null);
            if (result.ActiveTransaction)
            {
                result.Reason = "OpenTransaction";
                result.Reasons.Add(result.Reason);
                return result;
            }

            result.ActiveReservation = dbContext.ChargePaymentReservations.Any(r =>
                r.ChargePointId == chargePointId &&
                r.ConnectorId == connectorId &&
                (!reservationToIgnore.HasValue || r.ReservationId != reservationToIgnore.Value) &&
                (r.Status == PaymentReservationStatus.Pending ||
                 r.Status == PaymentReservationStatus.Authorized ||
                 r.Status == PaymentReservationStatus.StartRequested ||
                 r.Status == PaymentReservationStatus.Charging));
            if (result.ActiveReservation)
            {
                result.Reason = "ActiveReservation";
                result.Reasons.Add(result.Reason);
                return result;
            }

            if (liveConnectorStatus == null)
            {
                var persistedStatus = dbContext.ConnectorStatuses
                    .Where(cs => cs.ChargePointId == chargePointId && cs.ConnectorId == connectorId)
                    .Select(cs => new { cs.LastStatus, cs.LastStatusTime })
                    .FirstOrDefault();

                if (persistedStatus != null)
                {
                    result.PersistedStatus = persistedStatus.LastStatus;
                    if (persistedStatus.LastStatusTime.HasValue)
                    {
                        result.PersistedStatusAgeMinutes = (_utcNow() - persistedStatus.LastStatusTime.Value).TotalMinutes;
                    }

                    if (!string.Equals(persistedStatus.LastStatus, "Available", StringComparison.InvariantCultureIgnoreCase) &&
                        !string.Equals(persistedStatus.LastStatus, "Preparing", StringComparison.InvariantCultureIgnoreCase))
                    {
                        bool isStale = result.PersistedStatusAgeMinutes.HasValue &&
                                       result.PersistedStatusAgeMinutes.Value > PersistedStatusStaleAfter.TotalMinutes;
                        if (!isStale)
                        {
                            result.Reason = $"PersistedStatus:{persistedStatus.LastStatus}";
                            result.Reasons.Add(result.Reason);
                            return result;
                        }
                    }
                }
            }

            result.Startable = true;
            result.Reason = "Startable";
            result.Reasons.Add(result.Reason);
            return result;
        }

        private void SetConnectorStatus(OCPPCoreContext dbContext, string chargePointId, int connectorId, string status)
        {
            if (string.IsNullOrWhiteSpace(chargePointId) || connectorId <= 0) return;

            var connector = dbContext.ConnectorStatuses.Find(chargePointId, connectorId);
            string previousStatus = connector?.LastStatus;
            DateTime? previousTime = connector?.LastStatusTime;
            if (connector == null)
            {
                connector = new ConnectorStatus
                {
                    ChargePointId = chargePointId,
                    ConnectorId = connectorId
                };
                dbContext.ConnectorStatuses.Add(connector);
            }

            connector.LastStatus = status;
            connector.LastStatusTime = _utcNow();
            dbContext.SaveChanges();

            _logger.LogInformation("SetConnectorStatus => cp={ChargePointId} connector={ConnectorId} {PreviousStatus}@{PreviousTime:u} -> {NewStatus}@{NewTime:u}",
                chargePointId,
                connectorId,
                previousStatus ?? "(none)",
                previousTime,
                connector.LastStatus,
                connector.LastStatusTime);
        }

        private async Task<(string Status, string Reason)> TryStartChargingAsync(OCPPCoreContext dbContext, ChargePaymentReservation reservation, string caller)
        {
            if (reservation == null) return ("Error", "NoReservation");

            _chargePointStatusDict.TryGetValue(reservation.ChargePointId, out var status);
            var startability = GetConnectorStartability(dbContext, reservation.ChargePointId, reservation.ConnectorId, status, reservation.ReservationId);
            if (!startability.Startable)
            {
                _logger.LogWarning("TryStartCharging => Not startable reason={Reason} cp={Cp} connector={Conn} reservation={Res}", startability.Reason, reservation.ChargePointId, reservation.ConnectorId, reservation.ReservationId);
                return ("ConnectorBusy", startability.Reason);
            }

            // Optional charger-side reservation to lock connector
            await BestEffortReserveNowAsync(reservation, status, dbContext);

            // Idempotency: if already StartRequested, consider success
            if (reservation.Status == PaymentReservationStatus.StartRequested || reservation.Status == PaymentReservationStatus.Charging)
            {
                return ("StartRequested", "AlreadyRequested");
            }

            // Ensure idTag exists for authorization flow
            if (!string.IsNullOrWhiteSpace(reservation.OcppIdTag))
            {
                EnsureChargeTagExists(dbContext, reservation.OcppIdTag);
            }

            string connectorId = reservation.ConnectorId.ToString();
            string apiResult = await ExecuteRemoteStartAsync(status, dbContext, connectorId, reservation.OcppIdTag ?? reservation.ChargeTagId);
            string remoteStatus = ExtractStatusFromApiResult(apiResult);

            reservation.RemoteStartSentAtUtc = _utcNow();
            reservation.RemoteStartResult = remoteStatus;
            reservation.UpdatedAtUtc = reservation.RemoteStartSentAtUtc.Value;

            if (string.Equals(remoteStatus, "Accepted", StringComparison.OrdinalIgnoreCase))
            {
                reservation.Status = PaymentReservationStatus.StartRequested;
                reservation.RemoteStartAcceptedAtUtc = _utcNow();
                dbContext.SaveChanges();
                _logger.LogInformation("TryStartCharging => Remote start accepted reservation={ReservationId} caller={Caller}", reservation.ReservationId, caller);
                return ("StartRequested", "Accepted");
            }

            reservation.Status = PaymentReservationStatus.StartRejected;
            reservation.LastError = $"Remote start result: {remoteStatus}";
            reservation.FailureCode = "RemoteStartRejected";
            reservation.FailureMessage = reservation.LastError;
            dbContext.SaveChanges();
            _paymentCoordinator.CancelPaymentIntentIfCancelable(dbContext, reservation, reservation.LastError);
            await BestEffortCancelReservationAsync(reservation, status, dbContext);
            _logger.LogWarning("TryStartCharging => Remote start rejected reservation={ReservationId} status={RemoteStatus}", reservation.ReservationId, remoteStatus);
            return ("StartRejected", remoteStatus ?? "RemoteStartFailed");
        }

        private async Task HandlePaymentWebhookAsync(HttpContext context, OCPPCoreContext dbContext)
        {
            if (!HttpMethods.IsPost(context.Request.Method))
            {
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            string payload = await ReadRequestBodyAsync(context);
            string signature = context.Request.Headers["Stripe-Signature"].FirstOrDefault();

            _logger.LogInformation("Payments/Webhook => Incoming webhook ip={RemoteIp} payloadLength={PayloadLength} hasSignature={HasSignature}",
                context.Connection.RemoteIpAddress?.ToString() ?? "(unknown)",
                payload?.Length ?? 0,
                string.IsNullOrWhiteSpace(signature) ? "no" : "yes");

            _paymentCoordinator?.HandleWebhookEvent(dbContext, payload, signature);

            // Best-effort: if this is a checkout completion, kick TryStartCharging to avoid relying on browser redirect.
            try
            {
                var jobj = JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JObject>(payload);
                string type = jobj?["type"]?.ToString();
                if (string.Equals(type, "checkout.session.completed", StringComparison.OrdinalIgnoreCase))
                {
                    var metadataToken = jobj?["data"]?["object"]?["metadata"];
                    string resIdString = null;
                    if (metadataToken is Newtonsoft.Json.Linq.JObject metaObj && metaObj.TryGetValue("reservation_id", out var resToken))
                    {
                        resIdString = resToken?.ToString();
                    }

                    if (Guid.TryParse(resIdString, out var resId))
                    {
                        var reservation = dbContext.ChargePaymentReservations.Find(resId);
                        if (reservation != null)
                        {
                            await TryStartChargingAsync(dbContext, reservation, "StripeWebhook");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Payments/Webhook => Unable to parse payload for auto-start");
            }

            context.Response.StatusCode = (int)HttpStatusCode.OK;
        }

        private async Task<string> ExecuteRemoteStartAsync(ChargePointStatus chargePointStatus, OCPPCoreContext dbContext, string connectorId, string idTag)
        {
            if (chargePointStatus.Protocol == Protocol_OCPP21)
            {
                return await ExecuteRequestStartTransaction21(chargePointStatus, dbContext, connectorId, idTag);
            }
            else if (chargePointStatus.Protocol == Protocol_OCPP201)
            {
                return await ExecuteRequestStartTransaction20(chargePointStatus, dbContext, connectorId, idTag);
            }
            else
            {
                return await ExecuteRemoteStartTransaction16(chargePointStatus, dbContext, connectorId, idTag);
            }
        }

        private static string ExtractStatusFromApiResult(string apiResult)
        {
            if (string.IsNullOrWhiteSpace(apiResult)) return null;
            try
            {
                var payload = JsonConvert.DeserializeObject<Dictionary<string, object>>(apiResult);
                if (payload != null && payload.TryGetValue("status", out var statusValue))
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

        private static async Task<string> ReadRequestBodyAsync(HttpContext context)
        {
            context.Request.EnableBuffering();
            context.Request.Body.Position = 0;
            using (var reader = new StreamReader(context.Request.Body, leaveOpen: true))
            {
                string body = await reader.ReadToEndAsync();
                context.Request.Body.Position = 0;
                return body;
            }
        }

        private int GetChargerReservationId(ChargePaymentReservation reservation)
        {
            if (reservation == null) return 0;
            // Derive a stable positive int from the Guid for OCPP 1.6 reservation id expectations.
            var bytes = reservation.ReservationId.ToByteArray();
            int value = BitConverter.ToInt32(bytes, 0);
            return Math.Abs(value == int.MinValue ? int.MaxValue : value);
        }

        private bool SupportsReservationProfile(ChargePointStatus status)
        {
            if (status == null) return false;

            if (status.SupportsReservationProfile.HasValue)
            {
                return status.SupportsReservationProfile.Value;
            }

            var proto = status.Protocol?.ToLowerInvariant();
            if (proto == "ocpp1.6" || proto == "ocpp201" || proto == "ocpp2.1")
            {
                return true;
            }

            return false;
        }

        private async Task BestEffortReserveNowAsync(ChargePaymentReservation reservation, ChargePointStatus chargePointStatus, OCPPCoreContext dbContext)
        {
            if (!ReservationProfileEnabled || reservation == null || chargePointStatus == null) return;
            if (chargePointStatus.WebSocket == null || chargePointStatus.WebSocket.State != WebSocketState.Open) return;
            if (!SupportsReservationProfile(chargePointStatus)) return;

            var expiry = reservation.StartDeadlineAtUtc ?? _utcNow().AddMinutes(Math.Max(1, _configuration.GetValue<int?>("Payments:StartWindowMinutes") ?? 7));
            var idTag = reservation.OcppIdTag ?? reservation.ChargeTagId;
            int reservationId = GetChargerReservationId(reservation);
            try
            {
                if (string.Equals(chargePointStatus.Protocol, Protocol_OCPP16, StringComparison.OrdinalIgnoreCase))
                {
                    await ExecuteReserveNow16(chargePointStatus, dbContext, reservation.ConnectorId, idTag, reservationId, expiry);
                }
                else if (string.Equals(chargePointStatus.Protocol, Protocol_OCPP201, StringComparison.OrdinalIgnoreCase))
                {
                    await ExecuteReserveNow20(chargePointStatus, dbContext, reservation.ConnectorId, idTag, reservationId, expiry);
                }
                else if (string.Equals(chargePointStatus.Protocol, Protocol_OCPP21, StringComparison.OrdinalIgnoreCase))
                {
                    await ExecuteReserveNow21(chargePointStatus, dbContext, reservation.ConnectorId, idTag, reservationId, expiry);
                }
                else
                {
                    _logger.LogDebug("ReserveNow skipped: protocol {Protocol} not supported", chargePointStatus.Protocol);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ReserveNow best-effort failed for reservation {ReservationId}", reservation.ReservationId);
            }
        }

        private async Task BestEffortCancelReservationAsync(ChargePaymentReservation reservation, ChargePointStatus chargePointStatus, OCPPCoreContext dbContext)
        {
            if (!ReservationProfileEnabled || reservation == null || chargePointStatus == null) return;
            if (chargePointStatus.WebSocket == null || chargePointStatus.WebSocket.State != WebSocketState.Open) return;
            if (!SupportsReservationProfile(chargePointStatus)) return;

            int reservationId = GetChargerReservationId(reservation);
            try
            {
                if (string.Equals(chargePointStatus.Protocol, Protocol_OCPP16, StringComparison.OrdinalIgnoreCase))
                {
                    await ExecuteCancelReservation16(chargePointStatus, dbContext, reservationId);
                }
                else if (string.Equals(chargePointStatus.Protocol, Protocol_OCPP201, StringComparison.OrdinalIgnoreCase))
                {
                    await ExecuteCancelReservation20(chargePointStatus, dbContext, reservationId);
                }
                else if (string.Equals(chargePointStatus.Protocol, Protocol_OCPP21, StringComparison.OrdinalIgnoreCase))
                {
                    await ExecuteCancelReservation21(chargePointStatus, dbContext, reservationId);
                }
                else
                {
                    _logger.LogDebug("CancelReservation skipped: protocol {Protocol} not supported", chargePointStatus.Protocol);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "CancelReservation best-effort failed for reservation {ReservationId}", reservation.ReservationId);
            }
        }

        private static void EnsureChargeTagExists(OCPPCoreContext dbContext, string tagId)
        {
            if (dbContext == null || string.IsNullOrWhiteSpace(tagId)) return;

            var existing = dbContext.ChargeTags.Find(tagId);
            if (existing != null) return;

            var newTag = new ChargeTag
            {
                TagId = tagId,
                TagName = $"Web session {tagId}",
                Blocked = false
            };

            dbContext.ChargeTags.Add(newTag);
            dbContext.SaveChanges();
        }

        private static bool HasFreeTagAccess(OCPPCoreContext dbContext, ChargePoint chargePoint, string chargeTagId)
        {
            if (dbContext == null || string.IsNullOrWhiteSpace(chargeTagId)) return false;

            var tag = dbContext.ChargeTags.Find(chargeTagId);
            if (tag == null || (tag.Blocked ?? false)) return false;

            var privileges = dbContext.ChargeTagPrivileges
                .Where(p => p.FreeChargingEnabled && p.TagId == tag.TagId);

            if (chargePoint != null &&
                privileges.Any(p => p.ChargePointId == chargePoint.ChargePointId))
            {
                return true;
            }

            return privileges.Any(p => p.ChargePointId == null);
        }

        public void NotifyTransactionStarted(OCPPCoreContext dbContext, ChargePointStatus chargePointStatus, int connectorId, string chargeTagId, int transactionId)
        {
            if (chargePointStatus == null) return;
            _paymentCoordinator?.MarkTransactionStarted(dbContext, chargePointStatus.Id, connectorId, chargeTagId, transactionId);
            LinkReservationToTransaction(dbContext, chargePointStatus.Id, connectorId, chargeTagId, transactionId, _utcNow());
        }

        public void LinkReservationToTransaction(OCPPCoreContext dbContext, string chargePointId, int connectorId, string idTag, int transactionId, DateTime startTime)
        {
            _reservationLinkService?.LinkReservation(dbContext, chargePointId, connectorId, idTag, transactionId, startTime);
        }

        public void NotifyTransactionCompleted(OCPPCoreContext dbContext, Transaction transaction)
        {
            _paymentCoordinator?.CompleteReservation(dbContext, transaction);

            // Mark connector available locally and in the database so future starts are not blocked
            if (transaction != null)
            {
                if (_chargePointStatusDict.TryGetValue(transaction.ChargePointId, out var status))
                {
                    if (status.OnlineConnectors.TryGetValue(transaction.ConnectorId, out var connector))
                    {
                        connector.Status = ConnectorStatusEnum.Available;
                    }
                    else
                    {
                        status.OnlineConnectors[transaction.ConnectorId] = new OnlineConnectorStatus
                        {
                            Status = ConnectorStatusEnum.Available
                        };
                    }
                }

                SetConnectorStatus(dbContext, transaction.ChargePointId, transaction.ConnectorId, "Available");
            }
        }

        /// <summary>
        /// Dumps an OCPP message in the dump dir
        /// </summary>
        private void DumpMessage(string nameSuffix, string message)
        {
            string dumpDir = _configuration.GetValue<string>("MessageDumpDir");
            if (!string.IsNullOrWhiteSpace(dumpDir))
            {
                var fullDumpDir = Path.GetFullPath(dumpDir);
                Directory.CreateDirectory(fullDumpDir);
                string path = Path.Combine(fullDumpDir, string.Format("{0}_{1}.txt", DateTime.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss-ffff"), nameSuffix));
                try
                {
                    // Write incoming message into dump directory
                    File.WriteAllText(path, message);
                }
                catch (Exception exp)
                {
                    _logger.LogError(exp, "OCPPMiddleware.DumpMessage => Error dumping message '{0}' to path: '{1}'", nameSuffix, path);
                }
            }
        }
    }

    public static class OCPPMiddlewareExtensions
    {
        public static IApplicationBuilder UseOCPPMiddleware(this IApplicationBuilder builder)
        {
            return builder.UseMiddleware<OCPPMiddleware>();
        }
    }
}
