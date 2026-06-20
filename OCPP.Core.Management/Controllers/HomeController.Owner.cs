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
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OCPP.Core.Database;
using OCPP.Core.Management.Models;

namespace OCPP.Core.Management.Controllers
{
    public partial class HomeController : BaseController
    {
        [Authorize]
        public IActionResult Owner(string Id, OwnerViewModel ovm)
        {
            try
            {
                if (User != null && !User.IsInRole(Constants.AdminRoleName))
                {
                    Logger.LogWarning("Owner: Request by non-administrator: {0}", User?.Identity?.Name);
                    TempData["ErrMsgKey"] = "AccessDenied";
                    return RedirectToAction("Error", new { Id = "" });
                }

                ovm ??= new OwnerViewModel();
                ovm.CurrentOwnerId = Id;

                Logger.LogTrace("Owner: Loading owners...");
                List<Owner> dbOwners = DbContext.Owners.Include(o => o.ChargePoints).OrderBy(o => o.Name).ToList();
                Logger.LogInformation("Owner: Found {0} owners", dbOwners.Count);

                Owner currentOwner = null;
                if (!string.IsNullOrEmpty(Id) && Id != "@")
                {
                    if (int.TryParse(Id, out int ownerId))
                    {
                        currentOwner = dbOwners.FirstOrDefault(o => o.OwnerId == ownerId);
                        if (currentOwner != null)
                        {
                            Logger.LogTrace("Owner: Current owner: {0} / {1}", currentOwner.OwnerId, currentOwner.Name);
                        }
                    }
                }

                if (Request.Method == "POST")
                {
                    ovm.Name = ovm.Name?.Trim();
                    ovm.Email = string.IsNullOrWhiteSpace(ovm.Email) ? null : ovm.Email.Trim();
                    string errorMsg = null;

                    if (Id == "@")
                    {
                        Logger.LogTrace("Owner: Creating new owner...");

                        if (string.IsNullOrWhiteSpace(ovm.Name))
                        {
                            errorMsg = _localizer["OwnerNameRequired"].Value;
                        }

                        if (string.IsNullOrEmpty(errorMsg))
                        {
                            bool duplicate = dbOwners.Any(o =>
                                o.Name.Equals(ovm.Name, StringComparison.InvariantCultureIgnoreCase) &&
                                string.Equals(o.Email ?? string.Empty, ovm.Email ?? string.Empty, StringComparison.InvariantCultureIgnoreCase));
                            if (duplicate)
                            {
                                errorMsg = _localizer["OwnerDuplicate"].Value;
                                Logger.LogInformation("Owner: New => owner already exists: {0}", ovm.Name);
                            }
                        }

                        if (string.IsNullOrEmpty(errorMsg))
                        {
                            Owner newOwner = new Owner
                            {
                                Name = ovm.Name,
                                Email = ovm.Email
                            };
                            DbContext.Owners.Add(newOwner);
                            DbContext.SaveChanges();
                            Logger.LogInformation("Owner: New => owner saved: {0} / {1}", newOwner.OwnerId, ovm.Name);
                        }
                        else
                        {
                            ViewBag.ErrorMsg = errorMsg;
                            ovm.Owners = dbOwners;
                            return View("OwnerDetail", ovm);
                        }
                    }
                    else if (currentOwner != null)
                    {
                        if (Request.Form["action"] == "Delete")
                        {
                            Logger.LogDebug("Owner: Edit => Deleting owner {0} ...", currentOwner.OwnerId);
                            bool hasChargePoints = DbContext.ChargePoints.Any(cp => cp.OwnerId == currentOwner.OwnerId);
                            if (hasChargePoints)
                            {
                                errorMsg = _localizer["OwnerInUse"].Value;
                                Logger.LogInformation("Owner: Edit => owner '{0}' still assigned to charge points", currentOwner.OwnerId);
                            }
                            else
                            {
                                DbContext.Remove<Owner>(currentOwner);
                                DbContext.SaveChanges();
                                Logger.LogInformation("Owner: Edit => owner deleted: {0}", currentOwner.OwnerId);
                            }
                        }
                        else
                        {
                            if (string.IsNullOrWhiteSpace(ovm.Name))
                            {
                                errorMsg = _localizer["OwnerNameRequired"].Value;
                            }

                            if (string.IsNullOrEmpty(errorMsg))
                            {
                                bool duplicate = dbOwners.Any(o => o.OwnerId != currentOwner.OwnerId
                                    && o.Name.Equals(ovm.Name, StringComparison.InvariantCultureIgnoreCase)
                                    && string.Equals(o.Email ?? string.Empty, ovm.Email ?? string.Empty, StringComparison.InvariantCultureIgnoreCase));
                                if (duplicate)
                                {
                                    errorMsg = _localizer["OwnerDuplicate"].Value;
                                }
                            }

                            if (string.IsNullOrEmpty(errorMsg))
                            {
                                currentOwner.Name = ovm.Name;
                                currentOwner.Email = ovm.Email;
                                DbContext.SaveChanges();
                                Logger.LogInformation("Owner: Edit => owner saved: {0}", currentOwner.OwnerId);
                            }
                        }

                        if (!string.IsNullOrEmpty(errorMsg))
                        {
                            ovm.Owners = dbOwners;
                            ovm.OwnerId = currentOwner.OwnerId;
                            ViewBag.ErrorMsg = errorMsg;
                            return View("OwnerDetail", ovm);
                        }
                    }

                    return RedirectToAction("Owner", new { Id = "" });
                }
                else
                {
                    ovm = new OwnerViewModel();
                    ovm.Owners = dbOwners;
                    ovm.CurrentOwnerId = Id;

                    if (currentOwner != null)
                    {
                        ovm.OwnerId = currentOwner.OwnerId;
                        ovm.Name = currentOwner.Name;
                        ovm.Email = currentOwner.Email;
                    }

                    string viewName = (currentOwner != null || Id == "@") ? "OwnerDetail" : "OwnerList";
                    return View(viewName, ovm);
                }
            }
            catch (Exception exp)
            {
                Logger.LogError(exp, "Owner: Error loading oder saving owner(s) from database");
                TempData["ErrMessage"] = exp.Message;
                return RedirectToAction("Error", new { Id = "" });
            }
        }
    }
}
