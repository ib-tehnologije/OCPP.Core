/*
 * OCPP.Core - https://github.com/dallmann-consulting/OCPP.Core
 * Copyright (C) 2020-2021 dallmann consulting GmbH.
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

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Schema;
using OCPP.Core.Database;
using OCPP.Core.Server.Payments;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace OCPP.Core.Server
{
    public partial class ControllerBase
    {
        /// <summary>
        /// Internal string for OCPP protocol version
        /// </summary>
        protected virtual string ProtocolVersion { get;  }

        /// <summary>
        /// Configuration context for reading app settings
        /// </summary>
        protected IConfiguration Configuration { get; set; }

        /// <summary>
        /// Chargepoint status
        /// </summary>
        protected ChargePointStatus ChargePointStatus { get; set; }

        /// <summary>
        /// ILogger object
        /// </summary>
        protected ILogger Logger { get; set; }

        /// <summary>
        /// DbContext object
        /// </summary>
        protected OCPPCoreContext DbContext { get; set; }

        /// <summary>
        /// Constructor
        /// </summary>
        public ControllerBase(IConfiguration config, ILoggerFactory loggerFactory, ChargePointStatus chargePointStatus, OCPPCoreContext dbContext)
        {
            Configuration = config;

            if (chargePointStatus != null)
            {
                ChargePointStatus = chargePointStatus;
            }
            else
            {
                Logger.LogError("New ControllerBase => empty chargepoint status");
            }
            DbContext = dbContext;
        }

        /// <summary>
        /// Deserialize and validate JSON message (if schema file exists)
        /// </summary>
        protected T DeserializeMessage<T>(OCPPMessage msg)
        {
            string path = Assembly.GetExecutingAssembly().Location;
            string codeBase = Path.GetDirectoryName(path);

            bool validateMessages = Configuration.GetValue<bool>("ValidateMessages", false);

            string schemaJson = null;
            if (validateMessages && 
                !string.IsNullOrEmpty(codeBase) && 
                Directory.Exists(codeBase))
            {
                string msgTypeName = typeof(T).Name;
                string filename = Path.Combine(codeBase, $"Schema{ProtocolVersion}", $"{msgTypeName}.json");
                if (File.Exists(filename))
                {
                    Logger.LogTrace("DeserializeMessage => Using schema file: {0}", filename);
                    schemaJson = File.ReadAllText(filename);
                }
            }

            JsonTextReader reader = new JsonTextReader(new StringReader(msg.JsonPayload));
            JsonSerializer serializer = new JsonSerializer();

            if (!string.IsNullOrEmpty(schemaJson))
            {
                JSchemaValidatingReader validatingReader = new JSchemaValidatingReader(reader);
                validatingReader.Schema = JSchema.Parse(schemaJson);

                IList<string> messages = new List<string>();
                validatingReader.ValidationEventHandler += (o, a) => messages.Add(a.Message);
                T obj = serializer.Deserialize<T>(validatingReader);
                if (messages.Count > 0)
                {
                    foreach (string err in messages)
                    {
                        Logger.LogWarning("DeserializeMessage {0} => Validation error: {1}", msg.Action, err);
                    }
                    throw new FormatException("Message validation failed");
                }
                return obj;
            }
            else
            {
                // Deserialization WITHOUT schema validation
                Logger.LogTrace("DeserializeMessage => Deserialization without schema validation");
                return serializer.Deserialize<T>(reader);
            }
        }


        /// <summary>
        /// Helper function for creating and updating the ConnectorStatus in then database
        /// </summary>
        protected bool UpdateConnectorStatus(int connectorId, string status, DateTimeOffset? statusTime, double? meter, DateTimeOffset? meterTime)
        {
            try
            {
                ConnectorStatus connectorStatus = DbContext.Find<ConnectorStatus>(ChargePointStatus.Id, connectorId);
                if (connectorStatus == null)
                {
                    // no matching entry => create connector status
                    connectorStatus = new ConnectorStatus();
                    connectorStatus.ChargePointId = ChargePointStatus.Id;
                    connectorStatus.ConnectorId = connectorId;
                    Logger.LogTrace("UpdateConnectorStatus => Creating new DB-ConnectorStatus: ID={0} / Connector={1}", connectorStatus.ChargePointId, connectorStatus.ConnectorId);
                    DbContext.Add<ConnectorStatus>(connectorStatus);
                    DbContext.SaveChanges();
                }

                if (!string.IsNullOrEmpty(status))
                {
                    DateTime dbTime = ((statusTime.HasValue) ? statusTime.Value : DateTimeOffset.UtcNow).DateTime;
                    DbContext.ConnectorStatuses.Where(cs => cs.ChargePointId == ChargePointStatus.Id && cs.ConnectorId == connectorId)
                        .ExecuteUpdate(s => s
                            .SetProperty(cs => cs.LastStatus, status)
                            .SetProperty(cs => cs.LastStatusTime, dbTime)
                            );
                }

                if (meter.HasValue)
                {
                    DateTime dbTime = ((meterTime.HasValue) ? meterTime.Value : DateTimeOffset.UtcNow).DateTime;
                    DbContext.ConnectorStatuses.Where(cs => cs.ChargePointId == ChargePointStatus.Id && cs.ConnectorId == connectorId)
                        .ExecuteUpdate(s => s
                            .SetProperty(cs => cs.LastMeter, meter.Value)
                            .SetProperty(cs => cs.LastMeterTime, dbTime)
                            );
                }
                Logger.LogInformation("UpdateConnectorStatus => Save ConnectorStatus: ID={0} / Connector={1} / Status={2} / Meter={3}", connectorStatus.ChargePointId, connectorId, status, meter);
                return true;
            }
            catch (Exception exp)
            {
                Logger.LogError(exp, "UpdateConnectorStatus => Exception writing connector status (ID={0} / Connector={1}): {2}", ChargePointStatus?.Id, connectorId, exp.Message);
            }

            return false;
        }

        /// <summary>
        /// Set/Update in memory connector status with meter (and more) values
        /// </summary>
        protected void UpdateMemoryConnectorStatus(int connectorId, double meterKWH, DateTimeOffset meterTime, double? currentChargeKW, double? currentImportA, double? stateOfCharge)
        {
            // Values <1 have no meaning => null
            if (currentChargeKW.HasValue && currentChargeKW < 0) currentChargeKW = null;
            if (currentImportA.HasValue && currentImportA < 0) currentImportA = null;
            if (stateOfCharge.HasValue && stateOfCharge < 0) stateOfCharge = null;

            OnlineConnectorStatus ocs = GetOrCreateOnlineConnectorStatus(connectorId, out bool isNew);
            ocs.ChargeRateKW = currentChargeKW;
            if (meterKWH >= 0 && !currentChargeKW.HasValue &&
                ocs.MeterKWH.HasValue && ocs.MeterKWH <= meterKWH &&
                ocs.MeterValueDate < meterTime)
            {
                try
                {
                    // Chargepoint sends no power (kW) => calculate from meter and time (from last sample)
                    double diffMeter = meterKWH - ocs.MeterKWH.Value;
                    ocs.ChargeRateKW = diffMeter / ((meterTime.Subtract(ocs.MeterValueDate).TotalSeconds) / (60 * 60));
                    Logger.LogDebug("MeterValues => Calculated power for ChargePoint={0} / Connector={1} / Power: {2}kW", ChargePointStatus?.Id, connectorId, ocs.ChargeRateKW);
                }
                catch (Exception exp)
                {
                    Logger.LogWarning("MeterValues => Error calculating power for ChargePoint={0} / Connector={1}: {2}", ChargePointStatus?.Id, connectorId, exp.ToString());
                }
            }
            ocs.MeterKWH = meterKWH;
            ocs.MeterValueDate = meterTime;
            ocs.CurrentImportA = currentImportA;
            ocs.SoC = stateOfCharge;

            if (isNew)
            {
                TryAddOnlineConnectorStatus(connectorId, ocs, "MeterValues");
            }
        }

        protected void UpdateMemoryConnectorStatus(int connectorId, string rawStatus, DateTimeOffset? statusTime)
        {
            OnlineConnectorStatus ocs = GetOrCreateOnlineConnectorStatus(connectorId, out bool isNew);

            string normalizedStatus = OcppConnectorStatus.Normalize(rawStatus);
            ocs.Status = OcppConnectorStatus.ToConnectorStatusEnum(normalizedStatus);
            ocs.OcppStatus = normalizedStatus;
            ocs.OcppStatusAtUtc = statusTime ?? DateTimeOffset.UtcNow;

            if (isNew)
            {
                TryAddOnlineConnectorStatus(connectorId, ocs, "StatusNotification");
            }
        }

        protected void ApplyConnectorStatusTransition(int connectorId, string rawStatus, DateTimeOffset? statusTime)
        {
            string previousRawStatus = GetConnectorRawStatus(connectorId);
            string normalizedStatus = OcppConnectorStatus.Normalize(rawStatus);

            UpdateMemoryConnectorStatus(connectorId, normalizedStatus, statusTime);
            UpdateIdleTrackingForStatusTransition(connectorId, previousRawStatus, normalizedStatus, statusTime);
        }

        /// <summary>
        /// Clean charge tag Id from possible suffix ("..._abc")
        /// </summary>
        protected static string CleanChargeTagId(string rawChargeTagId, ILogger logger)
        {
            string idTag = rawChargeTagId;

            // KEBA adds the serial to the idTag ("<idTag>_<serial>") => cut off suffix
            if (!string.IsNullOrWhiteSpace(rawChargeTagId))
            {
                int sep = rawChargeTagId.IndexOf('_');
                if (sep >= 0)
                {
                    idTag = rawChargeTagId.Substring(0, sep);
                    logger.LogTrace("CleanChargeTagId => Charge tag '{0}' => '{1}'", rawChargeTagId, idTag);
                }
            }

            return idTag;
        }

        /// <summary>
        /// Return UtcNow + 1 year
        /// </summary>
        protected static DateTimeOffset MaxExpiryDate
        {
            get
            {
                return DateTime.UtcNow.Date.AddYears(1);
            }
        }

        protected void MarkChargingEnded(int connectorId, DateTimeOffset? timestamp)
        {
            try
            {
                var tx = DbContext.Transactions
                    .Where(t => t.ChargePointId == ChargePointStatus.Id
                        && t.ConnectorId == connectorId
                        && !t.StopTime.HasValue
                        && !t.ChargingEndedAtUtc.HasValue)
                    .OrderByDescending(t => t.TransactionId)
                    .FirstOrDefault();

                if (tx != null)
                {
                    tx.ChargingEndedAtUtc = (timestamp ?? DateTimeOffset.UtcNow).UtcDateTime;
                    DbContext.SaveChanges();
                    Logger.LogInformation("MarkChargingEnded => ChargePoint={0} Connector={1} Tx={2} Time={3}",
                        ChargePointStatus?.Id, connectorId, tx.TransactionId, tx.ChargingEndedAtUtc);
                }
            }
            catch (Exception exp)
            {
                Logger.LogError(exp, "MarkChargingEnded => Error setting charging end for ChargePoint={0} Connector={1}", ChargePointStatus?.Id, connectorId);
            }
        }

        protected void FinalizeIdleTracking(Transaction transaction, DateTime asOfUtc)
        {
            if (transaction == null || !transaction.ChargingEndedAtUtc.HasValue)
            {
                return;
            }

            try
            {
                var reservation = FindReservationForTransaction(transaction);
                if (reservation != null && IdleFeeCalculator.IsIdleFeeEnabled(reservation))
                {
                    var snapshot = IdleFeeCalculator.CalculateSnapshot(
                        transaction,
                        reservation,
                        GetPaymentFlowOptions(),
                        asOfUtc,
                        Logger);

                    transaction.IdleUsageFeeMinutes = snapshot.TotalMinutes;
                    transaction.IdleUsageFeeAmount = snapshot.TotalAmount;
                }

                Logger.LogInformation(
                    "FinalizeIdleTracking => ChargePoint={ChargePointId} Connector={ConnectorId} Tx={TransactionId} IdleMinutes={IdleMinutes} IdleAmount={IdleAmount}",
                    transaction.ChargePointId,
                    transaction.ConnectorId,
                    transaction.TransactionId,
                    transaction.IdleUsageFeeMinutes,
                    transaction.IdleUsageFeeAmount);

                transaction.ChargingEndedAtUtc = null;
            }
            catch (Exception exp)
            {
                Logger.LogError(exp, "FinalizeIdleTracking => Error finalizing idle totals for tx={TransactionId}", transaction?.TransactionId);
            }
        }

        protected ChargePaymentReservation FindReservationForTransaction(Transaction transaction)
        {
            if (transaction == null)
            {
                return null;
            }

            try
            {
                IQueryable<ChargePaymentReservation> baseQuery = DbContext.ChargePaymentReservations
                    .Where(r => r.ChargePointId == transaction.ChargePointId && r.ConnectorId == transaction.ConnectorId);

                if (transaction.TransactionId > 0)
                {
                    var byTransactionId = baseQuery
                        .Where(r => r.TransactionId == transaction.TransactionId || r.StartTransactionId == transaction.TransactionId)
                        .OrderByDescending(r => r.CreatedAtUtc)
                        .FirstOrDefault();
                    if (byTransactionId != null)
                    {
                        return byTransactionId;
                    }
                }

                string normalizedTag = CleanChargeTagId(transaction.StartTagId, Logger);
                if (!string.IsNullOrWhiteSpace(normalizedTag))
                {
                    var byTag = baseQuery
                        .Where(r => r.OcppIdTag == normalizedTag || r.ChargeTagId == normalizedTag)
                        .OrderByDescending(r => r.CreatedAtUtc)
                        .FirstOrDefault();
                    if (byTag != null)
                    {
                        return byTag;
                    }
                }

                DateTime txTime = transaction.StopTime ?? transaction.StartTime;
                return baseQuery
                    .Where(r => r.CreatedAtUtc <= txTime.AddHours(6))
                    .OrderByDescending(r => r.CreatedAtUtc)
                    .FirstOrDefault();
            }
            catch (Exception exp)
            {
                Logger.LogError(exp, "FindReservationForTransaction => Error resolving reservation for tx={TransactionId}", transaction.TransactionId);
                return null;
            }
        }

        private OnlineConnectorStatus GetOrCreateOnlineConnectorStatus(int connectorId, out bool isNew)
        {
            if (ChargePointStatus.OnlineConnectors.TryGetValue(connectorId, out OnlineConnectorStatus existingStatus))
            {
                isNew = false;
                return existingStatus;
            }

            isNew = true;
            return new OnlineConnectorStatus();
        }

        private void TryAddOnlineConnectorStatus(int connectorId, OnlineConnectorStatus connectorStatus, string source)
        {
            if (ChargePointStatus.OnlineConnectors.TryAdd(connectorId, connectorStatus))
            {
                Logger.LogTrace("{Source} => Set OnlineConnectorStatus for ChargePoint={ChargePointId} / Connector={ConnectorId}",
                    source,
                    ChargePointStatus?.Id,
                    connectorId);
            }
            else
            {
                Logger.LogError("{Source} => Error adding new OnlineConnectorStatus for ChargePoint={ChargePointId} / Connector={ConnectorId}",
                    source,
                    ChargePointStatus?.Id,
                    connectorId);
            }
        }

        private string GetConnectorRawStatus(int connectorId)
        {
            if (ChargePointStatus?.OnlineConnectors != null &&
                ChargePointStatus.OnlineConnectors.TryGetValue(connectorId, out OnlineConnectorStatus onlineConnectorStatus))
            {
                return OcppConnectorStatus.Normalize(onlineConnectorStatus.OcppStatus) ?? onlineConnectorStatus.Status.ToString();
            }

            return null;
        }

        private void UpdateIdleTrackingForStatusTransition(int connectorId, string previousRawStatus, string newRawStatus, DateTimeOffset? timestamp)
        {
            bool wasSuspendedEv = OcppConnectorStatus.IsSuspendedEv(previousRawStatus);
            bool isSuspendedEv = OcppConnectorStatus.IsSuspendedEv(newRawStatus);
            DateTime effectiveUtc = (timestamp ?? DateTimeOffset.UtcNow).UtcDateTime;

            if (wasSuspendedEv == isSuspendedEv)
            {
                return;
            }

            try
            {
                var transaction = DbContext.Transactions
                    .Where(t => t.ChargePointId == ChargePointStatus.Id &&
                                t.ConnectorId == connectorId &&
                                !t.StopTime.HasValue)
                    .OrderByDescending(t => t.TransactionId)
                    .FirstOrDefault();

                if (transaction == null)
                {
                    return;
                }

                if (isSuspendedEv)
                {
                    if (!transaction.ChargingEndedAtUtc.HasValue)
                    {
                        transaction.ChargingEndedAtUtc = effectiveUtc;
                        DbContext.SaveChanges();
                        Logger.LogInformation(
                            "UpdateIdleTrackingForStatusTransition => Opened idle interval cp={ChargePointId} connector={ConnectorId} tx={TransactionId} at={Timestamp:u}",
                            transaction.ChargePointId,
                            transaction.ConnectorId,
                            transaction.TransactionId,
                            transaction.ChargingEndedAtUtc);
                    }

                    return;
                }

                if (transaction.ChargingEndedAtUtc.HasValue)
                {
                    FinalizeIdleTracking(transaction, effectiveUtc);
                    DbContext.SaveChanges();
                }
            }
            catch (Exception exp)
            {
                Logger.LogError(exp,
                    "UpdateIdleTrackingForStatusTransition => Error updating idle state for ChargePoint={ChargePointId} Connector={ConnectorId} {PreviousStatus} -> {NewStatus}",
                    ChargePointStatus?.Id,
                    connectorId,
                    previousRawStatus ?? "(none)",
                    newRawStatus ?? "(none)");
            }
        }

        private PaymentFlowOptions GetPaymentFlowOptions()
        {
            return new PaymentFlowOptions
            {
                StartWindowMinutes = Configuration.GetValue<int?>("Payments:StartWindowMinutes") ?? 2,
                EnableReservationProfile = Configuration.GetValue<bool?>("Payments:EnableReservationProfile") ?? false,
                IdleFeeExcludedWindow = Configuration.GetValue<string>("Payments:IdleFeeExcludedWindow"),
                IdleFeeExcludedTimeZoneId = Configuration.GetValue<string>("Payments:IdleFeeExcludedTimeZoneId"),
                IdleAutoStopMinutes = Configuration.GetValue<int?>("Payments:IdleAutoStopMinutes") ?? 0
            };
        }
    }
}
