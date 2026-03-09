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
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OCPP.Core.Database;

namespace OCPP.Core.Management.Controllers
{
    public partial class ApiController : BaseController
    {
        [Authorize]
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public async Task<IActionResult> CancelReservation(Guid reservationId)
        {
            if (reservationId == Guid.Empty &&
                RouteData?.Values != null &&
                RouteData.Values.TryGetValue("id", out object routeIdValue) &&
                Guid.TryParse(Convert.ToString(routeIdValue), out Guid parsedReservationId))
            {
                reservationId = parsedReservationId;
            }

            if (User != null && !User.IsInRole(Constants.AdminRoleName))
            {
                Logger.LogWarning("CancelReservation: Request by non-administrator: {0}", User?.Identity?.Name);
                return StatusCode((int)HttpStatusCode.Unauthorized);
            }

            int httpStatuscode = (int)HttpStatusCode.OK;
            string resultContent = _localizer["CancelReservationError"];

            Logger.LogTrace("CancelReservation: Request to cancel reservation '{0}'", reservationId);
            if (reservationId != Guid.Empty)
            {
                try
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

                                Uri uri = new Uri(new Uri(serverApiUrl), "Payments/Cancel");
                                httpClient.Timeout = new TimeSpan(0, 0, 15);

                                if (!string.IsNullOrWhiteSpace(apiKeyConfig))
                                {
                                    httpClient.DefaultRequestHeaders.Add("X-API-Key", apiKeyConfig);
                                }
                                else
                                {
                                    Logger.LogWarning("CancelReservation: No API-Key configured!");
                                }

                                var payload = new
                                {
                                    reservationId,
                                    reason = "Cancelled from management UI"
                                };
                                string jsonPayload = JsonConvert.SerializeObject(payload);
                                using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                                HttpResponseMessage response = await httpClient.PostAsync(uri, content);
                                if (response.StatusCode == HttpStatusCode.OK)
                                {
                                    string jsonResult = await response.Content.ReadAsStringAsync();
                                    if (!string.IsNullOrEmpty(jsonResult))
                                    {
                                        try
                                        {
                                            JObject jsonObject = JsonConvert.DeserializeObject<JObject>(jsonResult);
                                            Logger.LogInformation("CancelReservation: Result of API request is '{0}'", jsonResult);
                                            bool cancellationApplied = jsonObject?.Value<bool?>("cancellationApplied") ?? false;
                                            string status = jsonObject?.Value<string>("status");
                                            resultContent = cancellationApplied
                                                ? _localizer["CancelReservationApplied"]
                                                : string.Format(_localizer["CancelReservationStatus"], status ?? "Unknown");
                                        }
                                        catch (Exception exp)
                                        {
                                            Logger.LogError(exp, "CancelReservation: Error in JSON result => {0}", exp.Message);
                                            resultContent = _localizer["CancelReservationError"];
                                        }
                                    }
                                    else
                                    {
                                        Logger.LogError("CancelReservation: Result of API request is empty");
                                        resultContent = _localizer["CancelReservationError"];
                                    }
                                }
                                else if (response.StatusCode == HttpStatusCode.NotFound)
                                {
                                    resultContent = _localizer["UnknownChargepoint"];
                                }
                                else
                                {
                                    Logger.LogError("CancelReservation: Result of API request => httpStatus={0}", response.StatusCode);
                                    resultContent = _localizer["CancelReservationError"];
                                }
                            }
                        }
                        catch (Exception exp)
                        {
                            Logger.LogError(exp, "CancelReservation: Error in API request => {0}", exp.Message);
                            resultContent = _localizer["CancelReservationError"];
                        }
                    }
                }
                catch (Exception exp)
                {
                    Logger.LogError(exp, "CancelReservation: Error cancelling reservation");
                    resultContent = _localizer["CancelReservationError"];
                }
            }

            return StatusCode(httpStatuscode, resultContent);
        }
    }
}
