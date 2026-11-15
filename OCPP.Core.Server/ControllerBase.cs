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
        /// <summary>
        /// Runs a meter observation through the shared evidence processor, applying the
        /// configured fallback capacity ceiling (<c>MeterEvidence:FallbackMaximumPowerKw</c>).
        /// </summary>
        protected MeterEvidenceResult ProcessMeterObservation(Transaction transaction, MeterEvidenceObservation observation)
        {
            if (observation != null && !observation.FallbackMaximumPowerKw.HasValue)
            {
                observation.FallbackMaximumPowerKw = Configuration?.GetValue<double?>("MeterEvidence:FallbackMaximumPowerKw");
            }
            return MeterEvidenceProcessor.Process(DbContext, transaction, observation);
        }

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
                IList<string> messages = new List<string>();
                try
                {
                    JSchemaValidatingReader validatingReader = new JSchemaValidatingReader(reader);
                    validatingReader.Schema = JSchema.Parse(schemaJson);
                    validatingReader.ValidationEventHandler += (o, a) => messages.Add(a.Message);

                    T obj;
                    obj = serializer.Deserialize<T>(validatingReader);

                    if (messages.Count > 0)
                    {
                        foreach (string err in messages)
                        {
                            Logger.LogWarning(
                                "DeserializeMessage => Schema validation error cp={ChargePointId} protocol={ProtocolVersion} action={Action} uniqueId={UniqueId} messageType={MessageType} schemaType={SchemaType} error={ValidationError}",
                                ChargePointStatus?.Id,
                                ProtocolVersion,
                                msg?.Action,
                                msg?.UniqueId,
                                msg?.MessageType,
                                typeof(T).Name,
                                err);
                        }

                        Logger.LogWarning(
                            "DeserializeMessage => Continuing after schema validation errors cp={ChargePointId} protocol={ProtocolVersion} action={Action} uniqueId={UniqueId} messageType={MessageType} schemaType={SchemaType}",
                            ChargePointStatus?.Id,
                            ProtocolVersion,
                            msg?.Action,
                            msg?.UniqueId,
                            msg?.MessageType,
                            typeof(T).Name);
                    }

                    return obj;
                }
                catch (JSchemaException exp)
                {
                    Logger.LogError(
                        exp,
                        "DeserializeMessage => Schema validation runtime failure cp={ChargePointId} protocol={ProtocolVersion} action={Action} uniqueId={UniqueId} messageType={MessageType} payloadBytes={PayloadBytes} schemaType={SchemaType}",
                        ChargePointStatus?.Id,
                        ProtocolVersion,
                        msg?.Action,
                        msg?.UniqueId,
                        msg?.MessageType,
                        msg?.JsonPayload == null ? 0 : System.Text.Encoding.UTF8.GetByteCount(msg.JsonPayload),
                        typeof(T).Name);
                    Logger.LogWarning(
                        "DeserializeMessage => Continuing without schema validation after runtime validation failure cp={ChargePointId} protocol={ProtocolVersion} action={Action} uniqueId={UniqueId} messageType={MessageType} schemaType={SchemaType}",
                        ChargePointStatus?.Id,
                        ProtocolVersion,
                        msg?.Action,
                        msg?.UniqueId,
                        msg?.MessageType,
                        typeof(T).Name);
                }

                reader = new JsonTextReader(new StringReader(msg.JsonPayload));
                return serializer.Deserialize<T>(reader);
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

            CloseIdleIntervalOnMeterProgress(connectorId, ocs, meterKWH);

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
            if (IsUnpoweredConnectorStatus(normalizedStatus))
            {
                ocs.ChargeRateKW = null;
                ocs.CurrentImportA = null;
                ocs.SoC = null;
            }

            if (isNew)
            {
                TryAddOnlineConnectorStatus(connectorId, ocs, "StatusNotification");
            }
        }

        private static bool IsUnpoweredConnectorStatus(string normalizedStatus)
        {
            return string.Equals(normalizedStatus, OcppConnectorStatus.Available, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalizedStatus, OcppConnectorStatus.Unavailable, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalizedStatus, OcppConnectorStatus.Faulted, StringComparison.OrdinalIgnoreCase);
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
                    var snapshot = IdleFeeCalculator.ApplyFinalSnapshot(
                        transaction,
                        reservation,
                        GetPaymentFlowOptions(),
                        asOfUtc,
                        Logger);
                }

                Logger.LogInformation(
                    "FinalizeIdleTracking => ChargePoint={ChargePointId} Connector={ConnectorId} Tx={TransactionId} IdleMinutes={IdleMinutes} IdleAmount={IdleAmount}",
                    transaction.ChargePointId,
                    transaction.ConnectorId,
                    transaction.TransactionId,
                    transaction.IdleUsageFeeMinutes,
                    transaction.IdleUsageFeeAmount);
            }
            catch (Exception exp)
            {
                Logger.LogError(exp, "FinalizeIdleTracking => Error finalizing idle totals for tx={TransactionId}", transaction?.TransactionId);
            }
        }

        protected void UpdateOpenTransactionMeterStop(int connectorId, double meterKwh)
        {
            if (meterKwh < 0)
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

                transaction.MeterStop = meterKwh;
                DbContext.SaveChanges();
            }
            catch (Exception exp)
            {
                Logger.LogDebug(exp, "UpdateOpenTransactionMeterStop => Failed cp={ChargePointId} connector={ConnectorId}", ChargePointStatus?.Id, connectorId);
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

        /// <summary>
        /// Meter increase (kWh) since an idle interval opened that confirms the vehicle really
        /// resumed charging. Smaller changes are treated as a status flap.
        /// </summary>
        internal const double IdleResumeMinMeterIncreaseKwh = 0.1;

        private void UpdateIdleTrackingForStatusTransition(int connectorId, string previousRawStatus, string newRawStatus, DateTimeOffset? timestamp)
        {
            bool wasIdle = OcppConnectorStatus.IsIdleAfterCharging(previousRawStatus);
            bool isIdle = OcppConnectorStatus.IsIdleAfterCharging(newRawStatus);
            DateTime effectiveUtc = (timestamp ?? DateTimeOffset.UtcNow).UtcDateTime;
            OnlineConnectorStatus ocs = TryGetOnlineConnectorStatus(connectorId);

            if (wasIdle == isIdle)
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
                    ClearIdleIntervalMemory(ocs);
                    return;
                }

                if (isIdle)
                {
                    if (ocs != null)
                    {
                        ocs.IdleResumeCandidateAtUtc = null;
                    }

                    if (!transaction.ChargingEndedAtUtc.HasValue)
                    {
                        transaction.ChargingEndedAtUtc = effectiveUtc;
                        DbContext.SaveChanges();
                        if (ocs != null)
                        {
                            ocs.IdleIntervalMeterKwh = ResolveCurrentMeterKwh(connectorId, ocs);
                        }

                        Logger.LogInformation(
                            "UpdateIdleTrackingForStatusTransition => Opened idle interval cp={ChargePointId} connector={ConnectorId} tx={TransactionId} at={Timestamp:u} status={Status}",
                            transaction.ChargePointId,
                            transaction.ConnectorId,
                            transaction.TransactionId,
                            transaction.ChargingEndedAtUtc,
                            newRawStatus);
                    }
                    else if (ocs != null && !ocs.IdleIntervalMeterKwh.HasValue)
                    {
                        // Interval survived a restart or reconnect: start watching the meter from now.
                        ocs.IdleIntervalMeterKwh = ResolveCurrentMeterKwh(connectorId, ocs);
                    }

                    return;
                }

                if (!transaction.ChargingEndedAtUtc.HasValue)
                {
                    ClearIdleIntervalMemory(ocs);
                    return;
                }

                if (OcppConnectorStatus.IsCharging(newRawStatus) && IsMeterUnchangedSinceIdleStart(connectorId, ocs))
                {
                    // Some chargers flap Charging <-> Finishing while the vehicle draws no energy.
                    // Keep the idle interval open; MeterValues closes it once the meter really moves.
                    if (ocs != null && !ocs.IdleResumeCandidateAtUtc.HasValue)
                    {
                        ocs.IdleResumeCandidateAtUtc = timestamp ?? DateTimeOffset.UtcNow;
                    }

                    Logger.LogInformation(
                        "UpdateIdleTrackingForStatusTransition => Kept idle interval open, meter unchanged cp={ChargePointId} connector={ConnectorId} tx={TransactionId} {PreviousStatus} -> {NewStatus}",
                        transaction.ChargePointId,
                        transaction.ConnectorId,
                        transaction.TransactionId,
                        previousRawStatus ?? "(none)",
                        newRawStatus);
                    return;
                }

                FinalizeIdleTracking(transaction, effectiveUtc);
                DbContext.SaveChanges();
                ClearIdleIntervalMemory(ocs);
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

        /// <summary>
        /// Closes an idle interval that was kept open over a Charging status once the meter shows
        /// that the vehicle really resumed charging. The interval ends at the Charging status time.
        /// </summary>
        private void CloseIdleIntervalOnMeterProgress(int connectorId, OnlineConnectorStatus ocs, double meterKwh)
        {
            if (ocs == null ||
                !ocs.IdleIntervalMeterKwh.HasValue ||
                !ocs.IdleResumeCandidateAtUtc.HasValue ||
                meterKwh < 0 ||
                meterKwh - ocs.IdleIntervalMeterKwh.Value < IdleResumeMinMeterIncreaseKwh ||
                !OcppConnectorStatus.IsCharging(ocs.OcppStatus))
            {
                return;
            }

            DateTime resumedAtUtc = ocs.IdleResumeCandidateAtUtc.Value.UtcDateTime;
            try
            {
                var transaction = DbContext.Transactions
                    .Where(t => t.ChargePointId == ChargePointStatus.Id &&
                                t.ConnectorId == connectorId &&
                                !t.StopTime.HasValue)
                    .OrderByDescending(t => t.TransactionId)
                    .FirstOrDefault();

                if (transaction?.ChargingEndedAtUtc.HasValue == true &&
                    transaction.ChargingEndedAtUtc.Value <= resumedAtUtc)
                {
                    FinalizeIdleTracking(transaction, resumedAtUtc);
                    DbContext.SaveChanges();
                    Logger.LogInformation(
                        "CloseIdleIntervalOnMeterProgress => Closed idle interval cp={ChargePointId} connector={ConnectorId} tx={TransactionId} resumedAt={ResumedAt:u} meter={Meter}",
                        transaction.ChargePointId,
                        transaction.ConnectorId,
                        transaction.TransactionId,
                        resumedAtUtc,
                        meterKwh);
                }

                ClearIdleIntervalMemory(ocs);
            }
            catch (Exception exp)
            {
                Logger.LogError(exp,
                    "CloseIdleIntervalOnMeterProgress => Error closing idle interval for ChargePoint={ChargePointId} Connector={ConnectorId}",
                    ChargePointStatus?.Id,
                    connectorId);
            }
        }

        private bool IsMeterUnchangedSinceIdleStart(int connectorId, OnlineConnectorStatus ocs)
        {
            double? startMeterKwh = ocs?.IdleIntervalMeterKwh;
            double? currentMeterKwh = ResolveCurrentMeterKwh(connectorId, ocs);
            if (!startMeterKwh.HasValue || !currentMeterKwh.HasValue)
            {
                return false;
            }

            return currentMeterKwh.Value - startMeterKwh.Value < IdleResumeMinMeterIncreaseKwh;
        }

        private double? ResolveCurrentMeterKwh(int connectorId, OnlineConnectorStatus ocs)
        {
            if (ocs?.MeterKWH.HasValue == true && ocs.MeterKWH.Value >= 0)
            {
                return ocs.MeterKWH.Value;
            }

            var persistedMeter = DbContext.ConnectorStatuses
                .Where(cs => cs.ChargePointId == ChargePointStatus.Id && cs.ConnectorId == connectorId)
                .Select(cs => cs.LastMeter)
                .FirstOrDefault();
            return persistedMeter.HasValue && persistedMeter.Value >= 0 ? persistedMeter : null;
        }

        private OnlineConnectorStatus TryGetOnlineConnectorStatus(int connectorId)
        {
            if (ChargePointStatus?.OnlineConnectors != null &&
                ChargePointStatus.OnlineConnectors.TryGetValue(connectorId, out OnlineConnectorStatus ocs))
            {
                return ocs;
            }

            return null;
        }

        private static void ClearIdleIntervalMemory(OnlineConnectorStatus ocs)
        {
            if (ocs != null)
            {
                ocs.IdleIntervalMeterKwh = null;
                ocs.IdleResumeCandidateAtUtc = null;
            }
        }

        private PaymentFlowOptions GetPaymentFlowOptions()
        {
            return PaymentFlowOptionsResolver.Resolve(Configuration, DbContext);
        }
    }
}
