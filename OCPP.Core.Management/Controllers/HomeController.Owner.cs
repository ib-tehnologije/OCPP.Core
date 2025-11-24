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
using System.Net.Mail;
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
        public IActionResult Owner(string Id, StationOwnerViewModel sovm)
        {
            try
            {
                if (User != null && !User.IsInRole(Constants.AdminRoleName))
                {
                    Logger.LogWarning("Owner: Request by non-administrator: {0}", User?.Identity?.Name);
                    TempData["ErrMsgKey"] = "AccessDenied";
                    return RedirectToAction("Error", new { Id = string.Empty });
                }

                Logger.LogTrace("Owner: Loading owners...");

                List<ChargeStationOwner> dbOwners = DbContext.ChargeStationOwners
                    .Include(o => o.ChargePoints)
                    .OrderBy(o => o.Name)
                    .ToList();

                ChargeStationOwner currentOwner = null;
                if (!string.IsNullOrEmpty(Id) && Id != "@")
                {
                    currentOwner = dbOwners.FirstOrDefault(o => o.OwnerId.ToString() == Id);
                    if (currentOwner == null && Request.Method != "POST")
                    {
                        Logger.LogWarning("Owner: Requested owner '{0}' not found", Id);
                        TempData["ErrMessage"] = _localizer["OwnerNotFound"].Value;
                        return RedirectToAction("Owner", new { Id = string.Empty });
                    }
                }

                if (Request.Method == "POST")
                {
                    string errorMsg = null;

                    if (Id == "@")
                    {
                        Logger.LogTrace("Owner: Creating new owner...");

                        if (string.IsNullOrWhiteSpace(sovm.Name))
                        {
                            errorMsg = _localizer["OwnerNameRequired"].Value;
                        }
                        else if (string.IsNullOrWhiteSpace(sovm.Email))
                        {
                            errorMsg = _localizer["OwnerEmailRequired"].Value;
                        }
                        else if (!IsValidEmail(sovm.Email))
                        {
                            errorMsg = _localizer["OwnerEmailInvalid"].Value;
                        }
                        else if (DbContext.ChargeStationOwners.Any(o => o.Email == sovm.Email))
                        {
                            errorMsg = _localizer["OwnerEmailExists"].Value;
                        }

                        if (string.IsNullOrEmpty(errorMsg))
                        {
                            ChargeStationOwner newOwner = new ChargeStationOwner
                            {
                                Name = sovm.Name,
                                Email = sovm.Email,
                                ProvisionPercentage = Math.Clamp(sovm.ProvisionPercentage, 0, 100)
                            };

                            DbContext.ChargeStationOwners.Add(newOwner);
                            DbContext.SaveChanges();
                            Logger.LogInformation("Owner: Created owner '{0}'", newOwner.Name);
                        }
                        else
                        {
                            sovm.Owners = dbOwners;
                            sovm.CurrentId = Id;
                            ViewBag.ErrorMsg = errorMsg;
                            return View("OwnerDetail", sovm);
                        }
                    }
                    else if (currentOwner != null && currentOwner.OwnerId.ToString() == Id)
                    {
                        if (Request.Form["action"] == "Delete")
                        {
                            Logger.LogTrace("Owner: Deleting owner '{0}'", Id);

                            if (DbContext.ChargePoints.Any(cp => cp.OwnerId == currentOwner.OwnerId))
                            {
                                TempData["ErrMessage"] = _localizer["OwnerDeleteWithStations"].Value;
                                return RedirectToAction("Owner", new { Id });
                            }

                            DbContext.ChargeStationOwners.Remove(currentOwner);
                            DbContext.SaveChanges();
                            Logger.LogInformation("Owner: Deleted owner '{0}'", currentOwner.Name);
                        }
                        else
                        {
                            if (string.IsNullOrWhiteSpace(sovm.Name))
                            {
                                errorMsg = _localizer["OwnerNameRequired"].Value;
                            }
                            else if (string.IsNullOrWhiteSpace(sovm.Email))
                            {
                                errorMsg = _localizer["OwnerEmailRequired"].Value;
                            }
                            else if (!IsValidEmail(sovm.Email))
                            {
                                errorMsg = _localizer["OwnerEmailInvalid"].Value;
                            }
                            else if (DbContext.ChargeStationOwners.Any(o => o.Email == sovm.Email && o.OwnerId != currentOwner.OwnerId))
                            {
                                errorMsg = _localizer["OwnerEmailExists"].Value;
                            }

                            if (string.IsNullOrEmpty(errorMsg))
                            {
                                currentOwner.Name = sovm.Name;
                                currentOwner.Email = sovm.Email;
                                currentOwner.ProvisionPercentage = Math.Clamp(sovm.ProvisionPercentage, 0, 100);

                                DbContext.SaveChanges();
                                Logger.LogInformation("Owner: Updated owner '{0}'", currentOwner.Name);
                            }
                            else
                            {
                                sovm.Owners = dbOwners;
                                sovm.CurrentId = Id;
                                ViewBag.ErrorMsg = errorMsg;
                                return View("OwnerDetail", sovm);
                            }
                        }
                    }

                    return RedirectToAction("Owner", new { Id = string.Empty });
                }
                else
                {
                    sovm = new StationOwnerViewModel();
                    sovm.Owners = dbOwners;
                    sovm.CurrentId = Id;

                    if (currentOwner != null)
                    {
                        sovm.OwnerId = currentOwner.OwnerId;
                        sovm.Name = currentOwner.Name;
                        sovm.Email = currentOwner.Email;
                        sovm.ProvisionPercentage = currentOwner.ProvisionPercentage;
                        sovm.LastReportYear = currentOwner.LastReportYear;
                        sovm.LastReportMonth = currentOwner.LastReportMonth;
                        sovm.LastReportSentAt = currentOwner.LastReportSentAt;
                        sovm.CurrentId = Id;
                    }

                    string viewName = (currentOwner != null || Id == "@") ? "OwnerDetail" : "OwnerList";
                    return View(viewName, sovm);
                }
            }
            catch (Exception exp)
            {
                Logger.LogError(exp, "Owner: Error processing owner view");
                TempData["ErrMessage"] = exp.Message;
                return RedirectToAction("Error", new { Id = string.Empty });
            }
        }

        private static bool IsValidEmail(string email)
        {
            try
            {
                var mailAddress = new MailAddress(email);
                return !string.IsNullOrWhiteSpace(mailAddress.Address);
            }
            catch
            {
                return false;
            }
        }
    }
}
