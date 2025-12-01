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
        public IActionResult ChargePoint(string Id, ChargePointViewModel cpvm)
        {
            try
            {
                if (User != null && !User.IsInRole(Constants.AdminRoleName))
                {
                    Logger.LogWarning("ChargePoint: Request by non-administrator: {0}", User?.Identity?.Name);
                    TempData["ErrMsgKey"] = "AccessDenied";
                    return RedirectToAction("Error", new { Id = "" });
                }

                cpvm.CurrentId = Id;

                Logger.LogTrace("ChargePoint: Loading charge points...");
                List<ChargePoint> dbChargePoints = DbContext.ChargePoints.Include(cp => cp.Owner).OrderBy(x => x.Name).ToList<ChargePoint>();
                Logger.LogInformation("ChargePoint: Found {0} chargepoints", dbChargePoints.Count);

                List<Owner> owners = DbContext.Owners.OrderBy(x => x.Name).ToList();
                Logger.LogInformation("ChargePoint: Found {0} owners", owners.Count);

                int? NormalizeOwnerId(int? ownerId)
                {
                    if (!ownerId.HasValue)
                    {
                        return null;
                    }

                    return owners.Any(o => o.OwnerId == ownerId.Value) ? ownerId : null;
                }

                ChargePoint currentChargePoint = null;
                if (!string.IsNullOrEmpty(Id))
                {
                    foreach (ChargePoint cp in dbChargePoints)
                    {
                        if (cp.ChargePointId.Equals(Id, StringComparison.InvariantCultureIgnoreCase))
                        {
                            currentChargePoint = cp;
                            Logger.LogTrace("ChargePoint: Current chargepoint: {0} / {1}", cp.ChargePointId, cp.Name);
                            break;
                        }
                    }
                }

                if (Request.Method == "POST")
                {
                    cpvm.OwnerId = NormalizeOwnerId(cpvm.OwnerId);
                    string errorMsg = null;

                    if (Id == "@")
                    {
                        Logger.LogTrace("ChargePoint: Creating new chargepoint...");

                        // Create new tag
                        if (string.IsNullOrWhiteSpace(cpvm.ChargePointId))
                        {
                            errorMsg = _localizer["ChargePointIdRequired"].Value;
                            Logger.LogInformation("ChargePoint: New => no chargepoint ID entered");
                        }

                        if (string.IsNullOrEmpty(errorMsg))
                        {
                            // check if duplicate
                            foreach (ChargePoint cp in dbChargePoints)
                            {
                                if (cp.ChargePointId.Equals(cpvm.ChargePointId, StringComparison.InvariantCultureIgnoreCase))
                                {
                                    // id already exists
                                    errorMsg = _localizer["ChargePointIdExists"].Value;
                                    Logger.LogInformation("ChargePoint: New => chargepoint ID already exists: {0}", cpvm.ChargePointId);
                                    break;
                                }
                            }
                        }

                        if (string.IsNullOrEmpty(errorMsg))
                        {
                            errorMsg = ValidatePricing(cpvm);
                        }

                        if (string.IsNullOrEmpty(errorMsg))
                        {
                            // Save tag in DB
                            ChargePoint newChargePoint = new ChargePoint();
                            newChargePoint.ChargePointId = cpvm.ChargePointId;
                            newChargePoint.Name = cpvm.Name;
                            newChargePoint.Comment = cpvm.Comment;
                            newChargePoint.Description = cpvm.Description;
                            newChargePoint.Username = cpvm.Username;
                            newChargePoint.Password = cpvm.Password;
                            newChargePoint.ClientCertThumb = cpvm.ClientCertThumb;
                            newChargePoint.FreeChargingEnabled = cpvm.FreeChargingEnabled;
                            newChargePoint.PricePerKwh = cpvm.PricePerKwh ?? 0m;
                            newChargePoint.UserSessionFee = cpvm.UserSessionFee ?? 0m;
                            newChargePoint.OwnerSessionFee = cpvm.OwnerSessionFee ?? 0m;
                            newChargePoint.OwnerCommissionPercent = cpvm.OwnerCommissionPercent ?? 0m;
                            newChargePoint.OwnerCommissionFixedPerKwh = cpvm.OwnerCommissionFixedPerKwh ?? 0m;
                            newChargePoint.MaxSessionKwh = cpvm.MaxSessionKwh ?? 0d;
                            newChargePoint.StartUsageFeeAfterMinutes = cpvm.StartUsageFeeAfterMinutes ?? 0;
                            newChargePoint.MaxUsageFeeMinutes = cpvm.MaxUsageFeeMinutes ?? 0;
                            newChargePoint.ConnectorUsageFeePerMinute = cpvm.ConnectorUsageFeePerMinute ?? 0m;
                            newChargePoint.OwnerId = NormalizeOwnerId(cpvm.OwnerId);
                            DbContext.ChargePoints.Add(newChargePoint);
                            DbContext.SaveChanges();
                            Logger.LogInformation("ChargePoint: New => charge point saved: {0} / {1}", cpvm.ChargePointId, cpvm.Name);
                        }
                        else
                        {
                            ViewBag.ErrorMsg = errorMsg;
                            cpvm.Owners = owners;
                            cpvm.ChargePoints = dbChargePoints;
                            cpvm.CurrentId = Id;
                            return View("ChargePointDetail", cpvm);
                        }
                    }
                    else if (currentChargePoint.ChargePointId == Id)
                    {
                        if (Request.Form["action"] == "Delete")
                        {
                            // Delete existing tag
                            Logger.LogDebug("ChargeTag: Edit => Deleting tag {0} ...", currentChargePoint.ChargePointId);

                            using (var transaction = DbContext.Database.BeginTransaction())
                            {
                                try
                                {
                                    // Delete corresponding transactions
                                    var delTransactions = DbContext.Transactions.Where(t => t.ChargePointId == currentChargePoint.ChargePointId).ExecuteDelete();
                                    Logger.LogDebug("ChargeTag: Edit => Deleted {0} transactions", delTransactions);
                                    // Delete corresponding connectors
                                    var delConnectorStatuses = DbContext.ConnectorStatuses.Where(s => s.ChargePointId == currentChargePoint.ChargePointId).ExecuteDelete();
                                    Logger.LogDebug("ChargeTag: Edit => Deleted {0} connectors statuses", delConnectorStatuses);
                                    // And finally delete the chargeoint itself
                                    var delChargePoints = DbContext.ChargePoints.Where(c => c.ChargePointId == currentChargePoint.ChargePointId).ExecuteDelete();
                                    Logger.LogDebug("ChargeTag: Edit => Deleted {0} chargepoints", delChargePoints);

                                    if (delChargePoints == 1)
                                    {
                                        Logger.LogInformation("ChargeTag: Edit => Committing deletion of chargepoint '{0}'", currentChargePoint.ChargePointId);
                                        transaction.Commit();
                                    }
                                    else
                                    {
                                        Logger.LogWarning("ChargePoint: Deleting chargepoint '{0}' => no chargepoint with that ID deleted!?", currentChargePoint.ChargePointId);
                                        transaction.Rollback();
                                    }
                                }
                                catch (Exception exp)
                                {
                                    Logger.LogError(exp, "ChargePoint: Error deleting chargepoint '{0}' from database", currentChargePoint.ChargePointId);
                                    transaction.Rollback();
                                    throw;
                                }
                            }
                        }
                        else
                        {
                            errorMsg = ValidatePricing(cpvm);
                        }

                        if (!string.IsNullOrEmpty(errorMsg))
                        {
                            ViewBag.ErrorMsg = errorMsg;
                            cpvm.Owners = owners;
                            cpvm.ChargePoints = dbChargePoints;
                            cpvm.CurrentId = Id;
                            return View("ChargePointDetail", cpvm);
                        }

                        // Save existing charge point
                        Logger.LogTrace("ChargePoint: Saving charge point '{0}'", Id);
                        currentChargePoint.Name = cpvm.Name;
                        currentChargePoint.Comment = cpvm.Comment;
                        currentChargePoint.Description = cpvm.Description;
                        currentChargePoint.Username = cpvm.Username;
                        currentChargePoint.Password = cpvm.Password;
                        currentChargePoint.ClientCertThumb = cpvm.ClientCertThumb;
                        currentChargePoint.FreeChargingEnabled = cpvm.FreeChargingEnabled;
                        currentChargePoint.PricePerKwh = cpvm.PricePerKwh ?? 0m;
                        currentChargePoint.UserSessionFee = cpvm.UserSessionFee ?? 0m;
                        currentChargePoint.OwnerSessionFee = cpvm.OwnerSessionFee ?? 0m;
                        currentChargePoint.OwnerCommissionPercent = cpvm.OwnerCommissionPercent ?? 0m;
                        currentChargePoint.OwnerCommissionFixedPerKwh = cpvm.OwnerCommissionFixedPerKwh ?? 0m;
                        currentChargePoint.MaxSessionKwh = cpvm.MaxSessionKwh ?? 0d;
                        currentChargePoint.StartUsageFeeAfterMinutes = cpvm.StartUsageFeeAfterMinutes ?? 0;
                        currentChargePoint.MaxUsageFeeMinutes = cpvm.MaxUsageFeeMinutes ?? 0;
                        currentChargePoint.ConnectorUsageFeePerMinute = cpvm.ConnectorUsageFeePerMinute ?? 0m;
                        currentChargePoint.OwnerId = NormalizeOwnerId(cpvm.OwnerId);

                        DbContext.SaveChanges();
                        Logger.LogInformation("ChargePoint: Edit => chargepoint saved: {0} / {1}", cpvm.ChargePointId, cpvm.Name);
                    }

                    return RedirectToAction("ChargePoint", new { Id = "" });
                }
                else
                {
                    // Display charge point
                    cpvm = new ChargePointViewModel();
                    cpvm.ChargePoints = dbChargePoints;
                    cpvm.CurrentId = Id;
                    cpvm.Owners = owners;

                    if (currentChargePoint!= null)
                    {
                        cpvm = new ChargePointViewModel();
                        cpvm.ChargePointId = currentChargePoint.ChargePointId;
                        cpvm.Name = currentChargePoint.Name;
                        cpvm.Comment = currentChargePoint.Comment;
                        cpvm.Description = currentChargePoint.Description;
                        cpvm.Username = currentChargePoint.Username;
                        cpvm.Password = currentChargePoint.Password;
                        cpvm.ClientCertThumb = currentChargePoint.ClientCertThumb;
                        cpvm.FreeChargingEnabled = currentChargePoint.FreeChargingEnabled;
                        cpvm.PricePerKwh = currentChargePoint.PricePerKwh;
                        cpvm.UserSessionFee = currentChargePoint.UserSessionFee;
                        cpvm.OwnerSessionFee = currentChargePoint.OwnerSessionFee;
                        cpvm.OwnerCommissionPercent = currentChargePoint.OwnerCommissionPercent;
                        cpvm.OwnerCommissionFixedPerKwh = currentChargePoint.OwnerCommissionFixedPerKwh;
                        cpvm.MaxSessionKwh = currentChargePoint.MaxSessionKwh;
                        cpvm.StartUsageFeeAfterMinutes = currentChargePoint.StartUsageFeeAfterMinutes;
                        cpvm.MaxUsageFeeMinutes = currentChargePoint.MaxUsageFeeMinutes;
                        cpvm.ConnectorUsageFeePerMinute = currentChargePoint.ConnectorUsageFeePerMinute;
                        cpvm.OwnerId = NormalizeOwnerId(currentChargePoint.OwnerId);
                        cpvm.Owners = owners;
                        cpvm.CurrentId = Id;
                    }

                    string viewName = (!string.IsNullOrEmpty(cpvm.ChargePointId) || Id == "@") ? "ChargePointDetail" : "ChargePointList";
                    return View(viewName, cpvm);
                }
            }
            catch (Exception exp)
            {
                Logger.LogError(exp, "ChargePoint: Error loading/editing chargepoint(s)");
                TempData["ErrMessage"] = exp.Message;
                return RedirectToAction("Error", new { Id = "" });
            }
        }

        private string ValidatePricing(ChargePointViewModel cpvm)
        {
            if (cpvm == null) return "Invalid charge point data.";
            if (cpvm.FreeChargingEnabled) return null;

            decimal pricePerKwh = cpvm.PricePerKwh ?? 0m;
            decimal usageFeePerMinute = cpvm.ConnectorUsageFeePerMinute ?? 0m;
            decimal sessionFee = cpvm.UserSessionFee ?? 0m;

            bool hasEnergyPrice = pricePerKwh > 0m;
            bool hasUsageFee = usageFeePerMinute > 0m;
            bool hasSessionFee = sessionFee > 0m;

            if (!hasEnergyPrice && !hasUsageFee && !hasSessionFee)
            {
                return "Add a price per kWh, connector usage fee, or user session fee, or enable free charging. Owner commission fields are optional.";
            }

            double maxSessionKwh = cpvm.MaxSessionKwh ?? 0d;
            if (hasEnergyPrice && maxSessionKwh <= 0)
            {
                return "When Price per kWh is set, Max session kWh must be greater than zero to calculate the payment cap.";
            }

            return null;
        }
    }
}
