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
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using OCPP.Core.Database;
using OCPP.Core.Management.Models;

namespace OCPP.Core.Management.Controllers
{
    public partial class HomeController : BaseController
    {
        [Authorize]
        public IActionResult ManageChargePoint(string id)
        {
            try
            {
                if (User != null && !User.IsInRole(Constants.AdminRoleName))
                {
                    Logger.LogWarning("ManageChargePoint: Request by non-administrator: {0}", User?.Identity?.Name);
                    TempData["ErrMsgKey"] = "AccessDenied";
                    return RedirectToAction("Error", new { Id = string.Empty });
                }

                if (string.IsNullOrWhiteSpace(id))
                {
                    return RedirectToAction("ChargePoint", new { Id = string.Empty });
                }

                ChargePoint chargePoint = DbContext.ChargePoints.Include(cp => cp.Owner).FirstOrDefault(cp => cp.ChargePointId == id);
                if (chargePoint == null)
                {
                    TempData["ErrMsgKey"] = "UnknownChargepoint";
                    return RedirectToAction("ChargePoint", new { Id = string.Empty });
                }

                List<ConnectorStatus> connectors = DbContext.ConnectorStatuses
                    .Where(c => c.ChargePointId == id)
                    .OrderBy(c => c.ConnectorId)
                    .ToList();

                List<ChargeTag> tags = DbContext.ChargeTags.AsNoTracking().OrderBy(t => t.TagName).ToList();

                var vm = new ChargePointManageViewModel
                {
                    ChargePoint = chargePoint,
                    ConnectorStatuses = connectors,
                    ChargeTags = tags,
                    ConfigOptions = AlfenConfigCatalog.All
                };

                LoadOnlineStatus(vm);

                return View("ChargePointManage", vm);
            }
            catch (Exception exp)
            {
                Logger.LogError(exp, "ManageChargePoint: Error building view");
                TempData["ErrMessage"] = exp.Message;
                return RedirectToAction("Error", new { Id = string.Empty });
            }
        }

        private void LoadOnlineStatus(ChargePointManageViewModel vm)
        {
            Dictionary<string, ChargePointStatus> dictOnlineStatus = new Dictionary<string, ChargePointStatus>();
            string serverApiUrl = base.Config.GetValue<string>("ServerApiUrl");
            string apiKeyConfig = base.Config.GetValue<string>("ApiKey");
            if (string.IsNullOrEmpty(serverApiUrl))
            {
                return;
            }

            try
            {
                using (var httpClient = new HttpClient())
                {
                    if (!serverApiUrl.EndsWith('/'))
                    {
                        serverApiUrl += "/";
                    }

                    Uri uri = new Uri(new Uri(serverApiUrl), "Status");
                    httpClient.Timeout = new TimeSpan(0, 0, 4);

                    if (!string.IsNullOrWhiteSpace(apiKeyConfig))
                    {
                        httpClient.DefaultRequestHeaders.Add("X-API-Key", apiKeyConfig);
                    }
                    else
                    {
                        Logger.LogWarning("ManageChargePoint: No API-Key configured!");
                    }

                    HttpResponseMessage response = httpClient.GetAsync(uri).Result;
                    if (response.StatusCode == System.Net.HttpStatusCode.OK)
                    {
                        string jsonData = response.Content.ReadAsStringAsync().Result;
                        if (!string.IsNullOrEmpty(jsonData))
                        {
                            ChargePointStatus[] onlineStatusList = JsonConvert.DeserializeObject<ChargePointStatus[]>(jsonData);
                            if (onlineStatusList != null)
                            {
                                foreach (ChargePointStatus cps in onlineStatusList)
                                {
                                    if (!dictOnlineStatus.TryAdd(cps.Id, cps))
                                    {
                                        Logger.LogError("ManageChargePoint: Online charge point status (ID={0}) could not be added to dictionary", cps.Id);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception exp)
            {
                Logger.LogError(exp, "ManageChargePoint: Error in status web request => {0}", exp.Message);
            }

            vm.OnlineConnectorStatuses = dictOnlineStatus;
        }

    }
}
