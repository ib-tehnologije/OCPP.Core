/*
 * OCPP.Core - https://github.com/dallmann-consulting/OCPP.Core
 * Copyright (C) 2020-2025 dallmann consulting GmbH.
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

using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OCPP.Core.Database;

namespace OCPP.Core.Management.Controllers
{
    public partial class ApiController
    {
        [Authorize]
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public async Task<IActionResult> SetChargingLimit(string Id, int connectorId, string limit)
        {
            if (User != null && !User.IsInRole(Constants.AdminRoleName))
            {
                Logger.LogWarning("SetChargingLimit: Request by non-administrator: {0}", User?.Identity?.Name);
                return StatusCode((int)HttpStatusCode.Unauthorized);
            }

            int httpStatuscode = (int)HttpStatusCode.OK;
            string resultContent = string.Empty;

            if (!string.IsNullOrEmpty(Id) && !string.IsNullOrEmpty(limit))
            {
                try
                {
                    ChargePoint chargePoint = DbContext.ChargePoints.Find(Id);
                    if (chargePoint != null)
                    {
                        string serverApiUrl = base.Config.GetValue<string>("ServerApiUrl");
                        string apiKeyConfig = base.Config.GetValue<string>("ApiKey");
                        if (!string.IsNullOrEmpty(serverApiUrl))
                        {
                            try
                            {
                                using (var httpClient = new HttpClient())
                                {
                                    if (!serverApiUrl.EndsWith('/'))
                                    {
                                        serverApiUrl += "/";
                                    }
                                    Uri uri = new Uri(serverApiUrl);
                                    string path = $"SetChargingLimit/{Uri.EscapeDataString(Id)}/{connectorId}/{Uri.EscapeDataString(limit)}";
                                    uri = new Uri(uri, path);
                                    httpClient.Timeout = new TimeSpan(0, 0, 15);

                                    if (!string.IsNullOrWhiteSpace(apiKeyConfig))
                                    {
                                        httpClient.DefaultRequestHeaders.Add("X-API-Key", apiKeyConfig);
                                    }
                                    else
                                    {
                                        Logger.LogWarning("SetChargingLimit: No API-Key configured!");
                                    }

                                    HttpResponseMessage response = await httpClient.GetAsync(uri);
                                    if (response.StatusCode == HttpStatusCode.OK)
                                    {
                                        resultContent = await response.Content.ReadAsStringAsync();
                                    }
                                    else if (response.StatusCode == HttpStatusCode.NotFound)
                                    {
                                        httpStatuscode = (int)HttpStatusCode.OK;
                                        resultContent = _localizer["ChargerOffline"];
                                    }
                                    else
                                    {
                                        httpStatuscode = (int)HttpStatusCode.OK;
                                        resultContent = _localizer["ResetError"];
                                    }
                                }
                            }
                            catch (Exception exp)
                            {
                                Logger.LogError(exp, "SetChargingLimit: Error in API request => {0}", exp.Message);
                                httpStatuscode = (int)HttpStatusCode.OK;
                                resultContent = _localizer["ResetError"];
                            }
                        }
                    }
                    else
                    {
                        httpStatuscode = (int)HttpStatusCode.OK;
                        resultContent = _localizer["UnknownChargepoint"];
                    }
                }
                catch (Exception exp)
                {
                    Logger.LogError(exp, "SetChargingLimit: Error loading charge point from database");
                    httpStatuscode = (int)HttpStatusCode.OK;
                    resultContent = _localizer["ResetError"];
                }
            }
            else
            {
                httpStatuscode = (int)HttpStatusCode.BadRequest;
            }

            return StatusCode(httpStatuscode, resultContent);
        }

        [Authorize]
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public async Task<IActionResult> ClearChargingLimit(string Id, int connectorId)
        {
            if (User != null && !User.IsInRole(Constants.AdminRoleName))
            {
                Logger.LogWarning("ClearChargingLimit: Request by non-administrator: {0}", User?.Identity?.Name);
                return StatusCode((int)HttpStatusCode.Unauthorized);
            }

            int httpStatuscode = (int)HttpStatusCode.OK;
            string resultContent = string.Empty;

            if (!string.IsNullOrEmpty(Id))
            {
                try
                {
                    ChargePoint chargePoint = DbContext.ChargePoints.Find(Id);
                    if (chargePoint != null)
                    {
                        string serverApiUrl = base.Config.GetValue<string>("ServerApiUrl");
                        string apiKeyConfig = base.Config.GetValue<string>("ApiKey");
                        if (!string.IsNullOrEmpty(serverApiUrl))
                        {
                            try
                            {
                                using (var httpClient = new HttpClient())
                                {
                                    if (!serverApiUrl.EndsWith('/'))
                                    {
                                        serverApiUrl += "/";
                                    }
                                    Uri uri = new Uri(serverApiUrl);
                                    string path = $"ClearChargingLimit/{Uri.EscapeDataString(Id)}/{connectorId}";
                                    uri = new Uri(uri, path);
                                    httpClient.Timeout = new TimeSpan(0, 0, 15);

                                    if (!string.IsNullOrWhiteSpace(apiKeyConfig))
                                    {
                                        httpClient.DefaultRequestHeaders.Add("X-API-Key", apiKeyConfig);
                                    }
                                    else
                                    {
                                        Logger.LogWarning("ClearChargingLimit: No API-Key configured!");
                                    }

                                    HttpResponseMessage response = await httpClient.GetAsync(uri);
                                    if (response.StatusCode == HttpStatusCode.OK)
                                    {
                                        resultContent = await response.Content.ReadAsStringAsync();
                                    }
                                    else if (response.StatusCode == HttpStatusCode.NotFound)
                                    {
                                        httpStatuscode = (int)HttpStatusCode.OK;
                                        resultContent = _localizer["ChargerOffline"];
                                    }
                                    else
                                    {
                                        httpStatuscode = (int)HttpStatusCode.OK;
                                        resultContent = _localizer["ResetError"];
                                    }
                                }
                            }
                            catch (Exception exp)
                            {
                                Logger.LogError(exp, "ClearChargingLimit: Error in API request => {0}", exp.Message);
                                httpStatuscode = (int)HttpStatusCode.OK;
                                resultContent = _localizer["ResetError"];
                            }
                        }
                    }
                    else
                    {
                        httpStatuscode = (int)HttpStatusCode.OK;
                        resultContent = _localizer["UnknownChargepoint"];
                    }
                }
                catch (Exception exp)
                {
                    Logger.LogError(exp, "ClearChargingLimit: Error loading charge point from database");
                    httpStatuscode = (int)HttpStatusCode.OK;
                    resultContent = _localizer["ResetError"];
                }
            }
            else
            {
                httpStatuscode = (int)HttpStatusCode.BadRequest;
            }

            return StatusCode(httpStatuscode, resultContent);
        }
    }
}
