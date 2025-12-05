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
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using OCPP.Core.Database;
using OCPP.Core.Management.Models;

namespace OCPP.Core.Management.Controllers
{
    public partial class ApiController
    {
        [Authorize]
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public async Task<IActionResult> GetConfiguration(string Id, string key)
        {
            if (User != null && !User.IsInRole(Constants.AdminRoleName))
            {
                Logger.LogWarning("GetConfiguration: Request by non-administrator: {0}", User?.Identity?.Name);
                return StatusCode((int)HttpStatusCode.Unauthorized);
            }

            int httpStatuscode = (int)HttpStatusCode.OK;
            string resultContent = string.Empty;

            Logger.LogTrace("GetConfiguration: Request for chargepoint '{0}' / key '{1}'", Id, key);
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
                                    string path = string.IsNullOrWhiteSpace(key)
                                        ? $"GetConfiguration/{Uri.EscapeDataString(Id)}"
                                        : $"GetConfiguration/{Uri.EscapeDataString(Id)}/{Uri.EscapeDataString(key)}";
                                    uri = new Uri(uri, path);
                                    httpClient.Timeout = new TimeSpan(0, 0, 15); // use short timeout

                                    if (!string.IsNullOrWhiteSpace(apiKeyConfig))
                                    {
                                        httpClient.DefaultRequestHeaders.Add("X-API-Key", apiKeyConfig);
                                    }
                                    else
                                    {
                                        Logger.LogWarning("GetConfiguration: No API-Key configured!");
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
                                    else if (response.StatusCode == HttpStatusCode.NotImplemented)
                                    {
                                        httpStatuscode = (int)HttpStatusCode.OK;
                                        resultContent = _localizer["NotSupported"];
                                    }
                                    else
                                    {
                                        Logger.LogError("GetConfiguration: Result of API request => httpStatus={0}", response.StatusCode);
                                        httpStatuscode = (int)HttpStatusCode.OK;
                                        resultContent = _localizer["ResetError"];
                                    }
                                }
                            }
                            catch (Exception exp)
                            {
                                Logger.LogError(exp, "GetConfiguration: Error in API request => {0}", exp.Message);
                                httpStatuscode = (int)HttpStatusCode.OK;
                                resultContent = _localizer["ResetError"];
                            }
                        }
                    }
                    else
                    {
                        Logger.LogWarning("GetConfiguration: Error loading charge point '{0}' from database", Id);
                        httpStatuscode = (int)HttpStatusCode.OK;
                        resultContent = _localizer["UnknownChargepoint"];
                    }
                }
                catch (Exception exp)
                {
                    Logger.LogError(exp, "GetConfiguration: Error loading charge point from database");
                    httpStatuscode = (int)HttpStatusCode.OK;
                    resultContent = _localizer["ResetError"];
                }
            }

            return StatusCode(httpStatuscode, resultContent);
        }

        [Authorize]
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public async Task<IActionResult> ChangeConfiguration(string Id, string key, string value)
        {
            if (User != null && !User.IsInRole(Constants.AdminRoleName))
            {
                Logger.LogWarning("ChangeConfiguration: Request by non-administrator: {0}", User?.Identity?.Name);
                return StatusCode((int)HttpStatusCode.Unauthorized);
            }

            int httpStatuscode = (int)HttpStatusCode.OK;
            string resultContent = string.Empty;

            Logger.LogTrace("ChangeConfiguration: Request for chargepoint '{0}' / key '{1}'", Id, key);
            if (!string.IsNullOrEmpty(Id) && !string.IsNullOrEmpty(key) && value != null)
            {
                try
                {
                    if (!ValidateConfigurationValue(key, value, out var validationError))
                    {
                        return StatusCode((int)HttpStatusCode.BadRequest, validationError);
                    }

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
                                    string path = $"ChangeConfiguration/{Uri.EscapeDataString(Id)}/{Uri.EscapeDataString(key)}/{Uri.EscapeDataString(value)}";
                                    uri = new Uri(uri, path);
                                    httpClient.Timeout = new TimeSpan(0, 0, 15); // use short timeout

                                    if (!string.IsNullOrWhiteSpace(apiKeyConfig))
                                    {
                                        httpClient.DefaultRequestHeaders.Add("X-API-Key", apiKeyConfig);
                                    }
                                    else
                                    {
                                        Logger.LogWarning("ChangeConfiguration: No API-Key configured!");
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
                                    else if (response.StatusCode == HttpStatusCode.NotImplemented)
                                    {
                                        httpStatuscode = (int)HttpStatusCode.OK;
                                        resultContent = _localizer["NotSupported"];
                                    }
                                    else
                                    {
                                        Logger.LogError("ChangeConfiguration: Result of API  request => httpStatus={0}", response.StatusCode);
                                        httpStatuscode = (int)HttpStatusCode.OK;
                                        resultContent = _localizer["ResetError"];
                                    }
                                }
                            }
                            catch (Exception exp)
                            {
                                Logger.LogError(exp, "ChangeConfiguration: Error in API request => {0}", exp.Message);
                                httpStatuscode = (int)HttpStatusCode.OK;
                                resultContent = _localizer["ResetError"];
                            }
                        }
                    }
                    else
                    {
                        Logger.LogWarning("ChangeConfiguration: Error loading charge point '{0}' from database", Id);
                        httpStatuscode = (int)HttpStatusCode.OK;
                        resultContent = _localizer["UnknownChargepoint"];
                    }
                }
                catch (Exception exp)
                {
                    Logger.LogError(exp, "ChangeConfiguration: Error loading charge point from database");
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

        private bool ValidateConfigurationValue(string key, string value, out string error)
        {
            error = null;
            var option = AlfenConfigCatalog.All.Find(o => o.Key.Equals(key, StringComparison.InvariantCultureIgnoreCase));
            if (option == null)
            {
                // Unknown key: allow but warn
                Logger.LogWarning("ChangeConfiguration: Unknown config key '{0}' passed validation", key);
                return true;
            }

            if (string.Equals(option.Access, "RO", StringComparison.InvariantCultureIgnoreCase))
            {
                error = $"Key '{key}' is read-only.";
                return false;
            }

            switch (option.Type)
            {
                case "bool":
                    if (!string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
                    {
                        error = $"Key '{key}' expects a boolean (true/false).";
                        return false;
                    }
                    break;
                case "int":
                    if (!long.TryParse(value, out var iv))
                    {
                        error = $"Key '{key}' expects an integer.";
                        return false;
                    }
                    if (option.Min.HasValue && iv < option.Min.Value)
                    {
                        error = $"Key '{key}' minimum is {option.Min}.";
                        return false;
                    }
                    if (option.Max.HasValue && iv > option.Max.Value)
                    {
                        error = $"Key '{key}' maximum is {option.Max}.";
                        return false;
                    }
                    break;
                case "list":
                    if (option.AllowedValues != null && option.AllowedValues.Count > 0)
                    {
                        if (!option.AllowedValues.Contains(value))
                        {
                            error = $"Key '{key}' must be one of: {string.Join(", ", option.AllowedValues)}.";
                            return false;
                        }
                    }
                    break;
                default:
                    // string: always ok
                    break;
            }

            return true;
        }
    }
}
