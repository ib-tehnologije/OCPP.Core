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
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using OCPP.Core.Database;

namespace OCPP.Core.Management.Controllers
{
    public partial class ApiController : BaseController
    {
        [Authorize]
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public async Task<IActionResult> StartTransaction(string Id, int connectorId, string param)
        {
            if (User != null && !User.IsInRole(Constants.AdminRoleName))
            {
                Logger.LogWarning("StartTransaction: Request by non-administrator: {0}", User?.Identity?.Name);
                return StatusCode((int)HttpStatusCode.Unauthorized);
            }

            int httpStatuscode = (int)HttpStatusCode.OK;
            object resultContent = new { status = "Error", message = _localizer["StartTransactionError"] };

            Logger.LogTrace("StartTransaction: Request to start charging transaction '{0}'/{1}/'{2}'", Id, connectorId, param);
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
                                    Uri createUri = new Uri(uri, "Payments/Create");
                                    httpClient.Timeout = new TimeSpan(0, 0, 30); // allow a bit more time for Stripe setup

                                    // API-Key authentication?
                                    if (!string.IsNullOrWhiteSpace(apiKeyConfig))
                                    {
                                        httpClient.DefaultRequestHeaders.Add("X-API-Key", apiKeyConfig);
                                    }
                                    else
                                    {
                                        Logger.LogWarning("StartTransaction: No API-Key configured!");
                                    }

                                    var payload = new
                                    {
                                        chargePointId = Id,
                                        connectorId = connectorId,
                                        chargeTagId = param
                                    };
                                    string jsonPayload = JsonConvert.SerializeObject(payload);
                                    using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                                    HttpResponseMessage response = await httpClient.PostAsync(createUri, content);
                                    if (response.StatusCode == HttpStatusCode.OK)
                                    {
                                        string jsonResult = await response.Content.ReadAsStringAsync();
                                        if (!string.IsNullOrEmpty(jsonResult))
                                        {
                                            Logger.LogInformation("StartTransaction: Payments/Create response '{0}'", jsonResult);
                                            return Content(jsonResult, "application/json");
                                        }
                                        else
                                        {
                                            Logger.LogError("StartTransaction: Result of API request is empty");
                                            httpStatuscode = (int)HttpStatusCode.OK;
                                            resultContent = new { status = "Error", message = _localizer["StartTransactionError"] };
                                        }
                                    }
                                    else if (response.StatusCode == HttpStatusCode.NotFound)
                                    {
                                        // Chargepoint offline
                                        httpStatuscode = (int)HttpStatusCode.NotFound;
                                        resultContent = new { status = "ChargerOffline", message = _localizer["ChargerOffline"] };
                                    }
                                    else
                                    {
                                        Logger.LogError("StartTransaction: Result of API  request => httpStatus={0}", response.StatusCode);
                                        httpStatuscode = (int)HttpStatusCode.OK;
                                        resultContent = new { status = "Error", message = _localizer["StartTransactionError"] };
                                    }
                                }
                            }
                            catch (Exception exp)
                            {
                                Logger.LogError(exp, "StartTransaction: Error in API request => {0}", exp.Message);
                                httpStatuscode = (int)HttpStatusCode.OK;
                                resultContent = new { status = "Error", message = _localizer["StartTransactionError"] };
                            }
                        }
                    }
                    else
                    {
                        Logger.LogWarning("StartTransaction: Error loading charge point '{0}' from database", Id);
                        httpStatuscode = (int)HttpStatusCode.OK;
                        resultContent = new { status = "Error", message = _localizer["UnknownChargepoint"] };
                    }
                }
                catch (Exception exp)
                {
                    Logger.LogError(exp, "StartTransaction: Error loading charge point from database");
                    httpStatuscode = (int)HttpStatusCode.OK;
                    resultContent = new { status = "Error", message = _localizer["StartTransactionError"] };
                }
            }

            return StatusCode(httpStatuscode, resultContent);
        }
    }
}
