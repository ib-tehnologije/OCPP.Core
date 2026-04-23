/*
 * OCPP.Core - https://github.com/dallmann-consulting/OCPP.Core
 * Copyright (C) 2020-2024 dallmann consulting GmbH.
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
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using OCPP.Core.Database;
using OCPP.Core.Server.Payments;
using OCPP.Core.Server.Payments.Invoices;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace OCPP.Core.Server
{
    public partial class OCPPMiddleware
    {
        // Supported OCPP protocols (in order)
        private const string Protocol_OCPP16 = "ocpp1.6";
        private const string Protocol_OCPP201 = "ocpp2.0.1";
        private const string Protocol_OCPP21 = "ocpp2.1";
        private static readonly string[] SupportedProtocols = { Protocol_OCPP16, Protocol_OCPP201, Protocol_OCPP21 };

        // RegExp for splitting ocpp message parts
        // ^\[\s*(\d)\s*,\s*\"([^"]*)\"\s*,(?:\s*\"(\w*)\"\s*,)?\s*(.*)\s*\]$
        // Third block is optional, because responses don't have an action
        private static string MessageRegExp = "^\\[\\s*(\\d+)\\s*,\\s*\"([^\"]+)\"\\s*,\\s*(?:\\s*\"(\\w+)\"\\s*,)?\\s*(\\{[^}]+\\}|\\{[\\s\\S]*?\\})\\s*\\]$";

        // Timeout in ms for waiting for responses from charger (for communication scheme server => charger)
        private int TimoutWaitForCharger = 60 * 1000;   // 60 seconds

        private static readonly TimeSpan PersistedStatusStaleAfter = TimeSpan.FromMinutes(10);
        private const double MaxEnergyComparisonToleranceKwh = 0.0001d;

        private readonly RequestDelegate _next;
        private readonly ILoggerFactory _logFactory;
        private readonly ILogger _logger;
        private readonly IConfiguration _configuration;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IPaymentCoordinator _paymentCoordinator;
        private readonly StartChargingMediator _startMediator;
        private readonly ReservationLinkService _reservationLinkService;
        private readonly Func<DateTime> _utcNow;
        private const string ConnectorBusyStatus = "ConnectorBusy";
        // Reservation profile disabled: Remove charger-side ReserveNow/CancelReservation to avoid flaky stations.
        private bool ReservationProfileEnabled => false;
        private readonly ConcurrentDictionary<int, DateTime> _idleAutoStopTransactions = new ConcurrentDictionary<int, DateTime>();
        private readonly ConcurrentDictionary<int, DateTime> _maxEnergyAutoStopTransactions = new ConcurrentDictionary<int, DateTime>();

        // Dictionary with status objects for each charge point
        private static readonly ConcurrentDictionary<string, ChargePointStatus> _chargePointStatusDict =
            new ConcurrentDictionary<string, ChargePointStatus>(StringComparer.OrdinalIgnoreCase);

        // Dictionary for processing asynchronous API calls
        private readonly ConcurrentDictionary<string, OCPPMessage> _requestQueue = new ConcurrentDictionary<string, OCPPMessage>();

        public OCPPMiddleware(RequestDelegate next, ILoggerFactory logFactory, IConfiguration configuration, IServiceScopeFactory scopeFactory, IPaymentCoordinator paymentCoordinator, StartChargingMediator startMediator, ReservationLinkService reservationLinkService)
        {
            _next = next;
            _logFactory = logFactory;
            _configuration = configuration;
            _scopeFactory = scopeFactory;
            _paymentCoordinator = paymentCoordinator;
            _startMediator = startMediator;
            _reservationLinkService = reservationLinkService;
            _utcNow = () => DateTime.UtcNow;

            _logger = logFactory.CreateLogger("OCPPMiddleware");

            var chargerTimeoutMs = _configuration.GetValue<int?>("Payments:ChargerResponseTimeoutMs");
            if (chargerTimeoutMs.HasValue && chargerTimeoutMs.Value > 0)
            {
                // Clamp to a reasonable minimum so we don't hammer the charger.
                TimoutWaitForCharger = Math.Max(5_000, chargerTimeoutMs.Value);
            }

            LoadExtensions();
            if (_startMediator != null)
            {
                _startMediator.TryStartAsync = TryStartChargingAsync;
            }
        }

        private bool RequirePreparingBeforeRemoteStart()
        {
            // Default to "true" because several stations (e.g., Huawei) may ACK RemoteStart
            // without starting if the cable is not connected yet.
            return _configuration.GetValue<bool?>("Payments:RequirePreparingBeforeRemoteStart") ?? true;
        }

        private bool TryResolveLiveChargePointStatus(
            string chargePointId,
            string operation,
            out ChargePointStatus status,
            bool requireOpenWebSocket = true,
            bool logIfMissing = true)
        {
            status = null;

            if (string.IsNullOrWhiteSpace(chargePointId))
            {
                if (logIfMissing)
                {
                    _logger.LogWarning("{Operation} => Missing chargepoint ID for live lookup", operation);
                }
                return false;
            }

            if (!_chargePointStatusDict.TryGetValue(chargePointId, out var candidateStatus))
            {
                if (logIfMissing)
                {
                    var connectedIds = _chargePointStatusDict.Keys
                        .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                        .Take(10)
                        .ToArray();

                    _logger.LogWarning(
                        "{Operation} => Live chargepoint not found requestedId={RequestedId} connectedCount={ConnectedCount} connectedSample={ConnectedSample}",
                        operation,
                        chargePointId,
                        _chargePointStatusDict.Count,
                        connectedIds.Length > 0 ? string.Join(",", connectedIds) : "(none)");
                }
                return false;
            }

            if (requireOpenWebSocket &&
                (candidateStatus.WebSocket == null || candidateStatus.WebSocket.State != WebSocketState.Open))
            {
                _logger.LogWarning(
                    "{Operation} => Chargepoint session without open websocket requestedId={RequestedId} resolvedId={ResolvedId} protocol={Protocol} wsState={WebSocketState}",
                    operation,
                    chargePointId,
                    candidateStatus.Id,
                    candidateStatus.Protocol ?? "(none)",
                    candidateStatus.WebSocket?.State.ToString() ?? "(null)");
                return false;
            }

            status = candidateStatus;
            _logger.LogDebug(
                "{Operation} => Resolved live chargepoint requestedId={RequestedId} resolvedId={ResolvedId} protocol={Protocol} wsState={WebSocketState}",
                operation,
                chargePointId,
                candidateStatus.Id,
                candidateStatus.Protocol ?? "(none)",
                candidateStatus.WebSocket?.State.ToString() ?? "(null)");
            return true;
        }

        private string ResolveRemoteStartIdTokenTypeValue(string operation, ILogger logger)
        {
            const string defaultValue = Messages_OCPP21.IdTokenEnumStringType.ISO14443;

            string configuredValue = _configuration.GetValue<string>("Payments:RemoteStartIdTokenType");
            if (string.IsNullOrWhiteSpace(configuredValue))
            {
                return defaultValue;
            }

            string trimmedValue = configuredValue.Trim();
            string[] allowedValues =
            {
                Messages_OCPP21.IdTokenEnumStringType.Central,
                Messages_OCPP21.IdTokenEnumStringType.eMAID,
                Messages_OCPP21.IdTokenEnumStringType.ISO14443,
                Messages_OCPP21.IdTokenEnumStringType.ISO15693,
                Messages_OCPP21.IdTokenEnumStringType.KeyCode,
                Messages_OCPP21.IdTokenEnumStringType.Local,
                Messages_OCPP21.IdTokenEnumStringType.MacAddress,
                Messages_OCPP21.IdTokenEnumStringType.NoAuthorization
            };

            string matchedValue = allowedValues.FirstOrDefault(value =>
                string.Equals(value, trimmedValue, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(matchedValue))
            {
                return matchedValue;
            }

            logger?.LogWarning(
                "{Operation} => Invalid Payments:RemoteStartIdTokenType='{ConfiguredValue}', falling back to '{DefaultValue}'",
                operation,
                configuredValue,
                defaultValue);
            return defaultValue;
        }

        private Messages_OCPP20.IdTokenEnumType ResolveRemoteStartIdTokenType20(ILogger logger)
        {
            string configuredValue = ResolveRemoteStartIdTokenTypeValue("OCPPMiddleware.OCPP20", logger);
            return configuredValue switch
            {
                Messages_OCPP21.IdTokenEnumStringType.Central => Messages_OCPP20.IdTokenEnumType.Central,
                Messages_OCPP21.IdTokenEnumStringType.eMAID => Messages_OCPP20.IdTokenEnumType.EMAID,
                Messages_OCPP21.IdTokenEnumStringType.ISO14443 => Messages_OCPP20.IdTokenEnumType.ISO14443,
                Messages_OCPP21.IdTokenEnumStringType.ISO15693 => Messages_OCPP20.IdTokenEnumType.ISO15693,
                Messages_OCPP21.IdTokenEnumStringType.KeyCode => Messages_OCPP20.IdTokenEnumType.KeyCode,
                Messages_OCPP21.IdTokenEnumStringType.Local => Messages_OCPP20.IdTokenEnumType.Local,
                Messages_OCPP21.IdTokenEnumStringType.MacAddress => Messages_OCPP20.IdTokenEnumType.MacAddress,
                Messages_OCPP21.IdTokenEnumStringType.NoAuthorization => Messages_OCPP20.IdTokenEnumType.NoAuthorization,
                _ => Messages_OCPP20.IdTokenEnumType.ISO14443
            };
        }

        private string ResolveRemoteStartIdTokenType21(ILogger logger)
        {
            return ResolveRemoteStartIdTokenTypeValue("OCPPMiddleware.OCPP21", logger);
        }

        private PaymentFlowOptions GetPaymentFlowOptions()
        {
            return PaymentFlowOptionsResolver.Resolve(_configuration, null);
        }

        private static bool IsStatus(string value, string expected) =>
            !string.IsNullOrWhiteSpace(value) &&
            string.Equals(value.Trim(), expected, StringComparison.OrdinalIgnoreCase);

        private static string NormalizeChargeTag(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return tag;
            int separatorIndex = tag.IndexOf('_');
            return separatorIndex >= 0 ? tag.Substring(0, separatorIndex) : tag;
        }

        private static string GetLiveConnectorRawStatus(OnlineConnectorStatus connectorStatus)
        {
            if (connectorStatus == null)
            {
                return null;
            }

            return OcppConnectorStatus.Normalize(connectorStatus.OcppStatus) ??
                   (connectorStatus.Status == ConnectorStatusEnum.Undefined ? null : connectorStatus.Status.ToString());
        }

        private static string GetLiveConnectorRawStatus(ChargePointStatus chargePointStatus, int connectorId)
        {
            if (chargePointStatus?.OnlineConnectors != null &&
                chargePointStatus.OnlineConnectors.TryGetValue(connectorId, out var connectorStatus))
            {
                return GetLiveConnectorRawStatus(connectorStatus);
            }

            return null;
        }

        private Transaction ResolveReservationTransaction(OCPPCoreContext dbContext, ChargePaymentReservation reservation)
        {
            if (dbContext == null || reservation == null)
            {
                return null;
            }

            if (reservation.TransactionId.HasValue)
            {
                var linkedTransaction = dbContext.Transactions.Find(reservation.TransactionId.Value);
                if (linkedTransaction != null)
                {
                    return linkedTransaction;
                }
            }

            IQueryable<Transaction> openTransactions = dbContext.Transactions
                .Where(t =>
                    t.ChargePointId == reservation.ChargePointId &&
                    t.ConnectorId == reservation.ConnectorId &&
                    !t.StopTime.HasValue);

            string normalizedOcppIdTag = NormalizeChargeTag(reservation.OcppIdTag);
            string normalizedChargeTag = NormalizeChargeTag(reservation.ChargeTagId);
            if (!string.IsNullOrWhiteSpace(normalizedOcppIdTag) || !string.IsNullOrWhiteSpace(normalizedChargeTag))
            {
                var tagMatches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(normalizedOcppIdTag))
                {
                    tagMatches.Add(normalizedOcppIdTag);
                }

                if (!string.IsNullOrWhiteSpace(normalizedChargeTag))
                {
                    tagMatches.Add(normalizedChargeTag);
                }

                var byTag = openTransactions
                    .Where(t => t.StartTagId != null && tagMatches.Contains(t.StartTagId))
                    .OrderByDescending(t => t.TransactionId)
                    .FirstOrDefault();
                if (byTag != null)
                {
                    return byTag;
                }
            }

            return openTransactions
                .Where(t => t.StartTime >= reservation.CreatedAtUtc)
                .OrderByDescending(t => t.TransactionId)
                .FirstOrDefault();
        }

        private static double? ResolveLiveSessionEnergyKwh(Transaction transaction, ChargePaymentReservation reservation, double? liveMeterKwh)
        {
            if (reservation?.ActualEnergyKwh.HasValue == true)
            {
                return Math.Max(0, reservation.ActualEnergyKwh.Value);
            }

            if (transaction == null)
            {
                return null;
            }

            if (transaction.StopTime.HasValue && transaction.MeterStop.HasValue)
            {
                return Math.Max(0, transaction.MeterStop.Value - transaction.MeterStart);
            }

            if (liveMeterKwh.HasValue)
            {
                return Math.Max(0, liveMeterKwh.Value - transaction.MeterStart);
            }

            if (transaction.MeterStop.HasValue)
            {
                return Math.Max(0, transaction.MeterStop.Value - transaction.MeterStart);
            }

            if (transaction.EnergyKwh > 0)
            {
                return transaction.EnergyKwh;
            }

            return null;
        }

        private static bool IsMaxEnergyReached(double sessionEnergyKwh, double maxEnergyKwh)
        {
            return maxEnergyKwh > 0 &&
                   sessionEnergyKwh >= 0 &&
                   sessionEnergyKwh + MaxEnergyComparisonToleranceKwh >= maxEnergyKwh;
        }

        private ChargePaymentReservation ResolveReservationForTransaction(OCPPCoreContext dbContext, Transaction transaction)
        {
            if (dbContext == null || transaction == null)
            {
                return null;
            }

            IQueryable<ChargePaymentReservation> baseQuery = dbContext.ChargePaymentReservations
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

            if (!string.IsNullOrWhiteSpace(transaction.StartTagId))
            {
                var byTag = baseQuery
                    .Where(r => r.OcppIdTag == transaction.StartTagId || r.ChargeTagId == transaction.StartTagId)
                    .OrderByDescending(r => r.CreatedAtUtc)
                    .FirstOrDefault();
                if (byTag != null)
                {
                    return byTag;
                }
            }

            DateTime referenceUtc = transaction.StopTime ?? transaction.StartTime;
            return baseQuery
                .Where(r => r.CreatedAtUtc <= referenceUtc.AddHours(6))
                .OrderByDescending(r => r.CreatedAtUtc)
                .FirstOrDefault();
        }

        private double DetermineTransactionMaxEnergyKwh(OCPPCoreContext dbContext, Transaction transaction)
        {
            if (transaction == null)
            {
                return 0;
            }

            if (transaction.MaxEnergyKwh > 0)
            {
                return transaction.MaxEnergyKwh;
            }

            var reservation = ResolveReservationForTransaction(dbContext, transaction);
            if (reservation?.MaxEnergyKwh > 0)
            {
                return reservation.MaxEnergyKwh;
            }

            var chargePoint = dbContext?.ChargePoints?.Find(transaction.ChargePointId);
            return Math.Max(0, chargePoint?.MaxSessionKwh ?? 0);
        }

        private void ApplyTransactionMaxEnergySnapshot(OCPPCoreContext dbContext, int transactionId)
        {
            if (dbContext == null || transactionId <= 0)
            {
                return;
            }

            try
            {
                var transaction = dbContext.Transactions.Find(transactionId);
                if (transaction == null || transaction.MaxEnergyKwh > 0)
                {
                    return;
                }

                double maxEnergyKwh = DetermineTransactionMaxEnergyKwh(dbContext, transaction);
                if (maxEnergyKwh <= 0)
                {
                    return;
                }

                transaction.MaxEnergyKwh = maxEnergyKwh;
                dbContext.SaveChanges();

                _logger.LogInformation(
                    "ApplyTransactionMaxEnergySnapshot => tx={TransactionId} cp={ChargePointId} connector={ConnectorId} maxEnergyKwh={MaxEnergyKwh:0.###}",
                    transaction.TransactionId,
                    transaction.ChargePointId,
                    transaction.ConnectorId,
                    transaction.MaxEnergyKwh);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ApplyTransactionMaxEnergySnapshot => Failed for tx={TransactionId}", transactionId);
            }
        }

        public void NotifyTransactionMeterUpdated(
            OCPPCoreContext dbContext,
            ChargePointStatus chargePointStatus,
            int connectorId,
            int? transactionId,
            double meterKwh,
            string source)
        {
            if (dbContext == null ||
                chargePointStatus == null ||
                connectorId <= 0 ||
                meterKwh < 0)
            {
                return;
            }

            Transaction transaction = null;
            if (transactionId.HasValue && transactionId.Value > 0)
            {
                transaction = dbContext.Transactions.Find(transactionId.Value);
                if (transaction != null &&
                    (transaction.StopTime.HasValue ||
                     !string.Equals(transaction.ChargePointId, chargePointStatus.Id, StringComparison.OrdinalIgnoreCase) ||
                     transaction.ConnectorId != connectorId))
                {
                    transaction = null;
                }
            }

            transaction ??= dbContext.Transactions
                .Where(t => t.ChargePointId == chargePointStatus.Id &&
                            t.ConnectorId == connectorId &&
                            !t.StopTime.HasValue)
                .OrderByDescending(t => t.TransactionId)
                .FirstOrDefault();

            if (transaction == null || transaction.StopTime.HasValue)
            {
                return;
            }

            double maxEnergyKwh = DetermineTransactionMaxEnergyKwh(dbContext, transaction);
            if (maxEnergyKwh <= 0)
            {
                return;
            }

            if (transaction.MaxEnergyKwh <= 0)
            {
                transaction.MaxEnergyKwh = maxEnergyKwh;
                dbContext.SaveChanges();
            }

            double sessionEnergyKwh = Math.Max(0, meterKwh - transaction.MeterStart);
            if (!IsMaxEnergyReached(sessionEnergyKwh, maxEnergyKwh))
            {
                return;
            }

            if (!_maxEnergyAutoStopTransactions.TryAdd(transaction.TransactionId, _utcNow()))
            {
                return;
            }

            _logger.LogWarning(
                "NotifyTransactionMeterUpdated => Max energy reached cp={ChargePointId} connector={ConnectorId} tx={TransactionId} sessionEnergyKwh={SessionEnergyKwh:0.###} maxEnergyKwh={MaxEnergyKwh:0.###} source={Source}",
                chargePointStatus.Id,
                connectorId,
                transaction.TransactionId,
                sessionEnergyKwh,
                maxEnergyKwh,
                source ?? "(none)");

            _ = Task.Run(() => TryAutoStopMaxEnergyTransactionAsync(chargePointStatus.Id, connectorId, transaction.TransactionId, source));
        }

        private static string ResolveSessionStage(
            ChargePaymentReservation reservation,
            Transaction transaction,
            bool activeTransaction,
            string liveStatus,
            string liveOcppStatus,
            string persistedStatus)
        {
            string reservationStatus = reservation?.Status;
            if (string.Equals(reservationStatus, PaymentReservationStatus.Failed, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(reservationStatus, PaymentReservationStatus.Cancelled, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(reservationStatus, PaymentReservationStatus.StartRejected, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(reservationStatus, PaymentReservationStatus.StartTimeout, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(reservationStatus, PaymentReservationStatus.Abandoned, StringComparison.OrdinalIgnoreCase))
            {
                return "error";
            }

            if (string.Equals(reservationStatus, PaymentReservationStatus.WaitingForDisconnect, StringComparison.OrdinalIgnoreCase) ||
                (reservation?.StopTransactionAtUtc.HasValue == true && reservation.DisconnectedAtUtc == null))
            {
                return "waitingForDisconnect";
            }

            if (reservation?.DisconnectedAtUtc.HasValue == true ||
                string.Equals(reservationStatus, PaymentReservationStatus.Completed, StringComparison.OrdinalIgnoreCase))
            {
                return "done";
            }

            string normalizedLive = (liveOcppStatus ?? liveStatus ?? persistedStatus)?.Trim();
            if (activeTransaction ||
                reservation?.StartTransactionAtUtc.HasValue == true ||
                OcppConnectorStatus.IsSuspendedEv(normalizedLive) ||
                string.Equals(normalizedLive, "Charging", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalizedLive, "SuspendedEVSE", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalizedLive, "Finishing", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalizedLive, "Occupied", StringComparison.OrdinalIgnoreCase) ||
                transaction?.StopTime.HasValue == false)
            {
                return "charging";
            }

            return "waiting";
        }

        private static string NormalizeOibDigits(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return null;
            }

            var digits = new string(input.Where(char.IsDigit).ToArray());
            return digits.Length == 0 ? null : digits;
        }

        private static string NormalizePublicDisplayCode(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? null
                : value.Trim();
        }

        private static string BuildPublicConnectorCode(string publicDisplayCode, int connectorId)
        {
            if (connectorId <= 0)
            {
                return null;
            }

            string normalized = NormalizePublicDisplayCode(publicDisplayCode);
            return string.IsNullOrWhiteSpace(normalized)
                ? null
                : $"{normalized}*{connectorId}";
        }

        private static string BuildPublicConnectorShortCode(string publicDisplayCode, int connectorId)
        {
            if (connectorId <= 0)
            {
                return null;
            }

            string normalized = NormalizePublicDisplayCode(publicDisplayCode);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            string lastSegment = normalized
                .Split(new[] { '*', '-' }, StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault();

            return string.IsNullOrWhiteSpace(lastSegment)
                ? null
                : $"{lastSegment}*{connectorId}";
        }

        /// <summary>
        /// Croatian OIB validation (ISO 7064 MOD 11,10).
        /// </summary>
        private static bool IsValidOib(string oibDigits)
        {
            if (string.IsNullOrWhiteSpace(oibDigits) ||
                oibDigits.Length != 11 ||
                oibDigits.Any(c => c < '0' || c > '9'))
            {
                return false;
            }

            int a = 10;
            for (int i = 0; i < 10; i++)
            {
                a = a + (oibDigits[i] - '0');
                a = a % 10;
                if (a == 0) a = 10;
                a = (a * 2) % 11;
            }

            int control = 11 - a;
            if (control == 10) control = 0;
            return control == (oibDigits[10] - '0');
        }

        // Removes entry only when key and object instance still match.
        // This prevents stale receive loops from removing a newer reconnect session.
        internal static bool TryRemoveStatusIfSameInstance(
            ConcurrentDictionary<string, ChargePointStatus> statusDict,
            ChargePointStatus candidateStatus)
        {
            if (statusDict == null ||
                candidateStatus == null ||
                string.IsNullOrWhiteSpace(candidateStatus.Id))
            {
                return false;
            }

            return ((ICollection<KeyValuePair<string, ChargePointStatus>>)statusDict)
                .Remove(new KeyValuePair<string, ChargePointStatus>(candidateStatus.Id, candidateStatus));
        }

        private void RemoveChargePointStatusIfCurrentSession(ChargePointStatus chargePointStatus, string source)
        {
            if (chargePointStatus == null)
            {
                return;
            }

            if (TryRemoveStatusIfSameInstance(_chargePointStatusDict, chargePointStatus))
            {
                _logger.LogInformation("OCPPMiddleware => Removed status for '{0}' ({1})", chargePointStatus.Id, source);
            }
            else
            {
                _logger.LogInformation("OCPPMiddleware => Keeping status for '{0}' ({1}, already replaced)", chargePointStatus.Id, source);
            }
        }

        private bool IsConnectorPreparing(OCPPCoreContext dbContext, ChargePaymentReservation reservation, ChargePointStatus cpStatus)
        {
            if (reservation == null) return false;

            try
            {
                if (cpStatus?.OnlineConnectors != null &&
                    cpStatus.OnlineConnectors.TryGetValue(reservation.ConnectorId, out var online))
                {
                    string liveRawStatus = GetLiveConnectorRawStatus(online);
                    if (!string.IsNullOrWhiteSpace(liveRawStatus))
                    {
                        return IsStatus(liveRawStatus, OcppConnectorStatus.Preparing);
                    }
                }
            }
            catch
            {
                // best-effort only
            }

            try
            {
                var persisted = dbContext?.ConnectorStatuses?.Find(reservation.ChargePointId, reservation.ConnectorId);
                return IsStatus(persisted?.LastStatus, "Preparing");
            }
            catch
            {
                return false;
            }
        }

        private async Task<(string Status, string Reason)> TryStartChargingOrAwaitPlugAsync(OCPPCoreContext dbContext, ChargePaymentReservation reservation, string caller)
        {
            if (reservation == null) return ("Error", "NoReservation");

            _chargePointStatusDict.TryGetValue(reservation.ChargePointId, out var cpStatus);

            if (RequirePreparingBeforeRemoteStart())
            {
                bool preparing = IsConnectorPreparing(dbContext, reservation, cpStatus);
                if (!preparing)
                {
                    // Only show "plug cable" when the connector is otherwise available.
                    // If it's occupied/faulted/unavailable, we keep AwaitingPlug=false and let UI show the real reason.
                    bool available = false;
                    try
                    {
                        bool checkedAuthoritativeLiveStatus = false;
                        if (cpStatus?.OnlineConnectors != null &&
                            cpStatus.OnlineConnectors.TryGetValue(reservation.ConnectorId, out var online))
                        {
                            string liveRawStatus = GetLiveConnectorRawStatus(online);
                            if (!string.IsNullOrWhiteSpace(liveRawStatus))
                            {
                                available = IsStatus(liveRawStatus, OcppConnectorStatus.Available);
                                checkedAuthoritativeLiveStatus = true;
                            }
                        }

                        if (!checkedAuthoritativeLiveStatus)
                        {
                            var persisted = dbContext?.ConnectorStatuses?.Find(reservation.ChargePointId, reservation.ConnectorId);
                            available = IsStatus(persisted?.LastStatus, "Available");
                        }
                    }
                    catch
                    {
                        // If we can't determine availability, prefer prompting for cable.
                        available = true;
                    }

                    reservation.AwaitingPlug = available ? true : (bool?)false;
                    reservation.UpdatedAtUtc = _utcNow();
                    dbContext.SaveChanges();

                    _logger.LogInformation("TryStartChargingOrAwaitPlug => Awaiting plug reservation={ReservationId} cp={ChargePointId} connector={ConnectorId} available={Available} caller={Caller}",
                        reservation.ReservationId,
                        reservation.ChargePointId,
                        reservation.ConnectorId,
                        available,
                        caller);

                    return (reservation.Status ?? PaymentReservationStatus.Authorized, available ? "AwaitingPlug" : "NotReady");
                }
            }

            // Cable is detected (Preparing) OR we don't require it.
            reservation.AwaitingPlug = false;
            reservation.UpdatedAtUtc = _utcNow();
            dbContext.SaveChanges();
            return await TryStartChargingAsync(dbContext, reservation, caller);
        }

        private IQueryable<ChargePaymentReservation> BuildAwaitingPlugReservationQuery(
            OCPPCoreContext dbContext,
            string chargePointId,
            DateTime nowUtc)
        {
            return dbContext.ChargePaymentReservations
                .Where(r =>
                    r.ChargePointId == chargePointId &&
                    r.Status == PaymentReservationStatus.Authorized &&
                    r.TransactionId == null &&
                    r.AwaitingPlug == true &&
                    (!r.StartDeadlineAtUtc.HasValue || r.StartDeadlineAtUtc > nowUtc));
        }

        private async Task<ChargePaymentReservation> TryClaimAwaitingPlugReservationAsync(
            OCPPCoreContext dbContext,
            string chargePointId,
            int preparingConnectorId,
            DateTime nowUtc)
        {
            var candidates = await BuildAwaitingPlugReservationQuery(dbContext, chargePointId, nowUtc)
                .OrderByDescending(r => r.CreatedAtUtc)
                .ToListAsync();

            if (candidates.Count == 0)
            {
                return null;
            }

            var directCandidate = candidates.FirstOrDefault(r => r.ConnectorId == preparingConnectorId);
            if (directCandidate != null)
            {
                return await ClaimAwaitingPlugReservationAsync(
                    dbContext,
                    directCandidate,
                    preparingConnectorId,
                    nowUtc,
                    autoReassigned: false);
            }

            var mismatchedCandidates = candidates
                .Where(r => r.ConnectorId != preparingConnectorId)
                .ToList();

            if (mismatchedCandidates.Count != 1)
            {
                _logger.LogInformation(
                    "NotifyConnectorPreparing => Skipping auto-remap cp={ChargePointId} preparingConnector={PreparingConnectorId} awaitingReservations={AwaitingReservationCount}",
                    chargePointId,
                    preparingConnectorId,
                    mismatchedCandidates.Count);
                return null;
            }

            _chargePointStatusDict.TryGetValue(chargePointId, out var liveChargePointStatus);
            var targetStartability = GetConnectorStartability(
                dbContext,
                chargePointId,
                preparingConnectorId,
                liveChargePointStatus,
                reservationToIgnore: null);

            if (!targetStartability.Startable)
            {
                _logger.LogInformation(
                    "NotifyConnectorPreparing => Skipping auto-remap reservation={ReservationId} cp={ChargePointId} fromConnector={FromConnectorId} toConnector={ToConnectorId} reason={Reason}",
                    mismatchedCandidates[0].ReservationId,
                    chargePointId,
                    mismatchedCandidates[0].ConnectorId,
                    preparingConnectorId,
                    targetStartability.Reason ?? "NotStartable");
                return null;
            }

            return await ClaimAwaitingPlugReservationAsync(
                dbContext,
                mismatchedCandidates[0],
                preparingConnectorId,
                nowUtc,
                autoReassigned: true);
        }

        private async Task<ChargePaymentReservation> ClaimAwaitingPlugReservationAsync(
            OCPPCoreContext dbContext,
            ChargePaymentReservation reservation,
            int preparingConnectorId,
            DateTime nowUtc,
            bool autoReassigned)
        {
            if (dbContext == null || reservation == null)
            {
                return null;
            }

            int originalConnectorId = reservation.ConnectorId;

            try
            {
                int updated = await dbContext.ChargePaymentReservations
                    .Where(r =>
                        r.ReservationId == reservation.ReservationId &&
                        r.Status == PaymentReservationStatus.Authorized &&
                        r.AwaitingPlug == true)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(r => r.ConnectorId, preparingConnectorId)
                        .SetProperty(r => r.AwaitingPlug, (bool?)false)
                        .SetProperty(r => r.UpdatedAtUtc, nowUtc));

                if (updated == 0)
                {
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "NotifyConnectorPreparing => Failed to claim awaiting reservation={ReservationId} cp={ChargePointId} fromConnector={FromConnectorId} toConnector={ToConnectorId}",
                    reservation.ReservationId,
                    reservation.ChargePointId,
                    originalConnectorId,
                    preparingConnectorId);
                return null;
            }

            dbContext.ChangeTracker.Clear();
            var claimedReservation = await dbContext.ChargePaymentReservations
                .FirstOrDefaultAsync(r => r.ReservationId == reservation.ReservationId);

            if (claimedReservation == null)
            {
                return null;
            }

            if (autoReassigned && originalConnectorId != preparingConnectorId)
            {
                _logger.LogInformation(
                    "NotifyConnectorPreparing => Auto-remapped reservation={ReservationId} cp={ChargePointId} fromConnector={FromConnectorId} toConnector={ToConnectorId}",
                    claimedReservation.ReservationId,
                    claimedReservation.ChargePointId,
                    originalConnectorId,
                    preparingConnectorId);
            }

            return claimedReservation;
        }

        public void NotifyConnectorPreparing(string chargePointId, int connectorId)
        {
            if (string.IsNullOrWhiteSpace(chargePointId) || connectorId <= 0) return;
            if (!RequirePreparingBeforeRemoteStart()) return;
            if (_scopeFactory == null) return;

            // Fire-and-forget: MUST NOT block the WebSocket receive loop.
            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<OCPPCoreContext>();

                    var now = _utcNow();
                    var reservation = await TryClaimAwaitingPlugReservationAsync(db, chargePointId, connectorId, now);

                    if (reservation == null) return;

                    _logger.LogInformation("NotifyConnectorPreparing => Triggering remote start reservation={ReservationId} cp={ChargePointId} connector={ConnectorId}",
                        reservation.ReservationId,
                        reservation.ChargePointId,
                        reservation.ConnectorId);

                    await TryStartChargingAsync(db, reservation, "StatusNotification");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "NotifyConnectorPreparing => Failed to auto-start for cp={ChargePointId} connector={ConnectorId}", chargePointId, connectorId);
                }
            });
        }

        public void NotifyConnectorOcppStatus(OCPPCoreContext dbContext, ChargePointStatus chargePointStatus, int connectorId, string rawStatus, DateTimeOffset? statusTime = null)
        {
            if (dbContext == null || chargePointStatus == null || connectorId <= 0)
            {
                return;
            }

            if (string.Equals(OcppConnectorStatus.Normalize(rawStatus), "Available", StringComparison.OrdinalIgnoreCase))
            {
                _paymentCoordinator?.HandleConnectorAvailable(
                    dbContext,
                    chargePointStatus.Id,
                    connectorId,
                    (statusTime ?? DateTimeOffset.UtcNow).UtcDateTime);
            }

            var transaction = dbContext.Transactions
                .Where(t =>
                    t.ChargePointId == chargePointStatus.Id &&
                    t.ConnectorId == connectorId &&
                    !t.StopTime.HasValue)
                .OrderByDescending(t => t.TransactionId)
                .FirstOrDefault();

            if (transaction == null)
            {
                return;
            }

            if (!OcppConnectorStatus.IsSuspendedEv(rawStatus))
            {
                _idleAutoStopTransactions.TryRemove(transaction.TransactionId, out _);
                return;
            }

            int idleAutoStopMinutes = GetPaymentFlowOptions().IdleAutoStopMinutes;
            if (idleAutoStopMinutes <= 0 || _scopeFactory == null)
            {
                return;
            }

            if (!_idleAutoStopTransactions.TryAdd(transaction.TransactionId, _utcNow()))
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(idleAutoStopMinutes));
                    await TryAutoStopIdleTransactionAsync(chargePointStatus.Id, connectorId, transaction.TransactionId, idleAutoStopMinutes);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "NotifyConnectorOcppStatus => Idle auto-stop scheduling failed cp={ChargePointId} connector={ConnectorId} tx={TransactionId}",
                        chargePointStatus.Id,
                        connectorId,
                        transaction.TransactionId);
                }
            });
        }

        private async Task TryAutoStopIdleTransactionAsync(string chargePointId, int connectorId, int transactionId, int idleAutoStopMinutes)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<OCPPCoreContext>();

                var transaction = await dbContext.Transactions.FindAsync(transactionId);
                if (transaction == null || transaction.StopTime.HasValue || !transaction.ChargingEndedAtUtc.HasValue)
                {
                    return;
                }

                if ((_utcNow() - transaction.ChargingEndedAtUtc.Value) < TimeSpan.FromMinutes(idleAutoStopMinutes))
                {
                    return;
                }

                if (!_chargePointStatusDict.TryGetValue(chargePointId, out var chargePointStatus) ||
                    chargePointStatus.WebSocket == null ||
                    chargePointStatus.WebSocket.State != WebSocketState.Open)
                {
                    _logger.LogInformation(
                        "TryAutoStopIdleTransaction => Skipping because charger is offline cp={ChargePointId} connector={ConnectorId} tx={TransactionId}",
                        chargePointId,
                        connectorId,
                        transactionId);
                    return;
                }

                string liveRawStatus = GetLiveConnectorRawStatus(chargePointStatus, connectorId);
                if (!OcppConnectorStatus.IsSuspendedEv(liveRawStatus))
                {
                    return;
                }

                string apiResult = await ExecuteRemoteStopAsync(chargePointStatus, dbContext, transaction);
                _logger.LogInformation(
                    "TryAutoStopIdleTransaction => Auto-stop attempted cp={ChargePointId} connector={ConnectorId} tx={TransactionId} result={Result}",
                    chargePointId,
                    connectorId,
                    transactionId,
                    ExtractStatusFromApiResult(apiResult) ?? apiResult ?? "(null)");
            }
            finally
            {
                _idleAutoStopTransactions.TryRemove(transactionId, out _);
            }
        }

        private async Task TryAutoStopMaxEnergyTransactionAsync(string chargePointId, int connectorId, int transactionId, string source)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<OCPPCoreContext>();

                var transaction = await dbContext.Transactions.FindAsync(transactionId);
                if (transaction == null || transaction.StopTime.HasValue)
                {
                    _maxEnergyAutoStopTransactions.TryRemove(transactionId, out _);
                    return;
                }

                double maxEnergyKwh = DetermineTransactionMaxEnergyKwh(dbContext, transaction);
                if (maxEnergyKwh <= 0)
                {
                    _maxEnergyAutoStopTransactions.TryRemove(transactionId, out _);
                    return;
                }

                double? meterKwh = transaction.MeterStop;
                if ((!meterKwh.HasValue || meterKwh.Value < 0) &&
                    _chargePointStatusDict.TryGetValue(chargePointId, out var liveStatus) &&
                    liveStatus.OnlineConnectors != null &&
                    liveStatus.OnlineConnectors.TryGetValue(connectorId, out var onlineConnector))
                {
                    meterKwh = onlineConnector.MeterKWH;
                }

                double sessionEnergyKwh = meterKwh.HasValue
                    ? Math.Max(0, meterKwh.Value - transaction.MeterStart)
                    : -1;
                if (!meterKwh.HasValue || !IsMaxEnergyReached(sessionEnergyKwh, maxEnergyKwh))
                {
                    _maxEnergyAutoStopTransactions.TryRemove(transactionId, out _);
                    return;
                }

                if (!TryResolveLiveChargePointStatus(chargePointId, "TryAutoStopMaxEnergyTransaction", out var chargePointStatus))
                {
                    _logger.LogInformation(
                        "TryAutoStopMaxEnergyTransaction => Skipping because charger is offline cp={ChargePointId} connector={ConnectorId} tx={TransactionId} source={Source}",
                        chargePointId,
                        connectorId,
                        transactionId,
                        source ?? "(none)");
                    return;
                }

                string apiResult = await ExecuteRemoteStopAsync(chargePointStatus, dbContext, transaction);
                _logger.LogInformation(
                    "TryAutoStopMaxEnergyTransaction => Auto-stop attempted cp={ChargePointId} connector={ConnectorId} tx={TransactionId} source={Source} sessionEnergyKwh={SessionEnergyKwh:0.###} maxEnergyKwh={MaxEnergyKwh:0.###} result={Result}",
                    chargePointId,
                    connectorId,
                    transactionId,
                    source ?? "(none)",
                    sessionEnergyKwh,
                    maxEnergyKwh,
                    ExtractStatusFromApiResult(apiResult) ?? apiResult ?? "(null)");
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "TryAutoStopMaxEnergyTransaction => Failed cp={ChargePointId} connector={ConnectorId} tx={TransactionId} source={Source}",
                    chargePointId,
                    connectorId,
                    transactionId,
                    source ?? "(none)");
            }
        }

        public async Task Invoke(HttpContext context, OCPPCoreContext dbContext)
        {
            _logger.LogTrace("OCPPMiddleware => Websocket request: Path='{0}'", context.Request.Path);

            ChargePointStatus chargePointStatus = null;

            if (context.Request.Path.StartsWithSegments("/OCPP"))
            {
                string chargepointIdentifier;
                string[] parts = context.Request.Path.Value.Split('/');
                if (string.IsNullOrWhiteSpace(parts[parts.Length - 1]))
                {
                    // (Last part - 1) is chargepoint identifier
                    chargepointIdentifier = parts[parts.Length - 2];
                }
                else
                {
                    // Last part is chargepoint identifier
                    chargepointIdentifier = parts[parts.Length - 1];
                }
                _logger.LogInformation("OCPPMiddleware => Connection request with chargepoint identifier = '{0}'", chargepointIdentifier);

                // Known chargepoint?
                if (!string.IsNullOrWhiteSpace(chargepointIdentifier))
                {
                    ChargePoint chargePoint = dbContext.Find<ChargePoint>(chargepointIdentifier);
                    if (chargePoint != null)
                    {
                        _logger.LogInformation("OCPPMiddleware => SUCCESS: Found chargepoint with identifier={0}", chargePoint.ChargePointId);

                        // Check optional chargepoint authentication
                        if (!string.IsNullOrWhiteSpace(chargePoint.Username))
                        {
                            // Chargepoint MUST send basic authentication header

                            bool basicAuthSuccess = false;
                            string authHeader = context.Request.Headers["Authorization"];
                            if (!string.IsNullOrEmpty(authHeader))
                            {
                                string[] cred = System.Text.ASCIIEncoding.ASCII.GetString(Convert.FromBase64String(authHeader.Substring(6))).Split(':');
                                if (cred.Length == 2 && chargePoint.Username == cred[0] && chargePoint.Password == cred[1])
                                {
                                    // Authentication match => OK
                                    _logger.LogInformation("OCPPMiddleware => SUCCESS: Basic authentication for chargepoint '{0}' match", chargePoint.ChargePointId);
                                    basicAuthSuccess = true;
                                }
                                else
                                {
                                    // Authentication does NOT match => Failure
                                    _logger.LogWarning("OCPPMiddleware => FAILURE: Basic authentication for chargepoint '{0}' does NOT match", chargePoint.ChargePointId);
                                }
                            }
                            if (basicAuthSuccess == false)
                            {
                                context.Response.Headers.Append("WWW-Authenticate", "Basic realm=\"OCPP.Core\"");
                                context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                                return;
                            }

                        }
                        else if (!string.IsNullOrWhiteSpace(chargePoint.ClientCertThumb))
                        {
                            // Chargepoint MUST send basic authentication header

                            bool certAuthSuccess = false;
                            X509Certificate2 clientCert = context.Connection.ClientCertificate;
                            if (clientCert != null)
                            {
                                if (clientCert.Thumbprint.Equals(chargePoint.ClientCertThumb, StringComparison.InvariantCultureIgnoreCase))
                                {
                                    // Authentication match => OK
                                    _logger.LogInformation("OCPPMiddleware => SUCCESS: Certificate authentication for chargepoint '{0}' match", chargePoint.ChargePointId);
                                    certAuthSuccess = true;
                                }
                                else
                                {
                                    // Authentication does NOT match => Failure
                                    _logger.LogWarning("OCPPMiddleware => FAILURE: Certificate authentication for chargepoint '{0}' does NOT match", chargePoint.ChargePointId);
                                }
                            }
                            if (certAuthSuccess == false)
                            {
                                context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                                return;
                            }
                        }
                        else
                        {
                            _logger.LogInformation("OCPPMiddleware => No authentication for chargepoint '{0}' configured", chargePoint.ChargePointId);
                        }

                        // Store chargepoint data
                        chargePointStatus = new ChargePointStatus(chargePoint);
                    }
                    else
                    {
                        _logger.LogWarning("OCPPMiddleware => FAILURE: Found no chargepoint with identifier={0}", chargepointIdentifier);
                    }
                }

                if (chargePointStatus != null)
                {
                    if (context.WebSockets.IsWebSocketRequest)
                    {
                        // Match supported sub protocols
                        string subProtocol = null;
                        foreach (string supportedProtocol in SupportedProtocols)
                        {
                            if (context.WebSockets.WebSocketRequestedProtocols.Contains(supportedProtocol))
                            {
                                subProtocol = supportedProtocol;
                                break;
                            }
                        }
                        if (string.IsNullOrEmpty(subProtocol))
                        {
                            // Not matching protocol! => failure
                            string protocols = string.Empty;
                            foreach (string p in context.WebSockets.WebSocketRequestedProtocols)
                            {
                                if (string.IsNullOrEmpty(protocols)) protocols += ",";
                                protocols += p;
                            }
                            _logger.LogWarning("OCPPMiddleware => No supported sub-protocol in '{0}' from charge station '{1}'", protocols, chargepointIdentifier);
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        }
                        else
                        {
                            chargePointStatus.Protocol = subProtocol;

                            bool statusSuccess = false;
                            try
                            {
                                _logger.LogTrace("OCPPMiddleware => Store/Update status object");
                                // Replace any existing status entry to avoid duplicate-key errors on reconnects
                                _chargePointStatusDict[chargepointIdentifier] = chargePointStatus;
                                statusSuccess = true;
                            }
                            catch (Exception exp)
                            {
                                _logger.LogError(exp, "OCPPMiddleware => Error storing status object in dictionary => refuse connection");
                                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                            }

                            if (statusSuccess)
                            {
                                // Handle socket communication
                                _logger.LogTrace("OCPPMiddleware => Waiting for message...");

                                try
                                {
                                    using (WebSocket webSocket = await context.WebSockets.AcceptWebSocketAsync(subProtocol))
                                    {
                                        _logger.LogTrace("OCPPMiddleware => WebSocket connection with charge point '{0}'", chargepointIdentifier);
                                        chargePointStatus.WebSocket = webSocket;

                                        if (subProtocol == Protocol_OCPP21)
                                        {
                                            // OCPP V2.1
                                            await Receive21(chargePointStatus, context, dbContext);
                                        }
                                        else if (subProtocol == Protocol_OCPP201)
                                        {
                                            // OCPP V2.0
                                            await Receive20(chargePointStatus, context, dbContext);
                                        }
                                        else
                                        {
                                            // OCPP V1.6
                                            await Receive16(chargePointStatus, context, dbContext);
                                        }
                                    }
                                }
                                catch (Exception exp)
                                {
                                    if ((exp is WebSocketException || exp is TaskCanceledException) &&
                                        chargePointStatus?.WebSocket?.State != WebSocketState.Open)
                                    {
                                        _logger.LogInformation("OCPPMiddleware => WebSocket connection lost on charge point '{0}' with state '{1}' / close-status '{2}'", chargePointStatus.Id, chargePointStatus.WebSocket.State, chargePointStatus.WebSocket.CloseStatus);
                                    }
                                    else
                                    {
                                        _logger.LogTrace("OCPPMiddleware => Receive() unhandled exception '{0}'", exp.Message);
                                    }

                                    // Receive loop has ended anyway
                                    // => Close connection
                                    if (chargePointStatus?.WebSocket?.State == WebSocketState.Open)
                                    {
                                        try
                                        {
                                            await chargePointStatus?.WebSocket.CloseAsync(WebSocketCloseStatus.InternalServerError, null, CancellationToken.None);
                                        }
                                        catch { }
                                    }
                                    // Remove chargepoint status
                                    RemoveChargePointStatusIfCurrentSession(chargePointStatus, "accept-loop-catch");
                                }
                            }
                        }
                    }
                    else
                    {
                        // no websocket request => failure
                        _logger.LogWarning("OCPPMiddleware => Non-Websocket request");
                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    }
                }
                else
                {
                    // unknown chargepoint
                    _logger.LogTrace("OCPPMiddleware => no chargepoint: http 412");
                    context.Response.StatusCode = (int)HttpStatusCode.PreconditionFailed;
                }
            }
            else if (context.Request.Path.StartsWithSegments("/API"))
            {
                // format: /API/<command>[/chargepointId[/connectorId[/parameter]]]
                string[] urlParts = context.Request.Path.Value.Split('/');
                bool isStripeWebhook = urlParts.Length >= 4 &&
                                       string.Equals(urlParts[2], "Payments", StringComparison.OrdinalIgnoreCase) &&
                                       string.Equals(urlParts[3], "Webhook", StringComparison.OrdinalIgnoreCase);

                // Check authentication (X-API-Key)
                if (!isStripeWebhook)
                {
                    string apiKeyConfig = _configuration.GetValue<string>("ApiKey");
                    if (!string.IsNullOrWhiteSpace(apiKeyConfig))
                    {
                        // ApiKey specified => check request
                        string apiKeyCaller = context.Request.Headers["X-API-Key"].FirstOrDefault();
                        if (apiKeyConfig == apiKeyCaller)
                        {
                            // API-Key matches
                            _logger.LogInformation("OCPPMiddleware => Success: X-API-Key matches");
                        }
                        else
                        {
                            // API-Key does NOT matches => authentication failure!!!
                            _logger.LogWarning("OCPPMiddleware => Failure: Wrong X-API-Key! Caller='{0}'", apiKeyCaller);
                            context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                            return;
                        }
                    }
                    else
                    {
                        // No API-Key configured => no authentication
                        _logger.LogWarning("OCPPMiddleware => No X-API-Key configured!");
                    }
                }
                else
                {
                    _logger.LogDebug("OCPPMiddleware => Skipping API key check for Stripe webhook");
                }

                if (urlParts.Length >= 3)
                {
                    string cmd = urlParts[2];
                    string urlChargePointId = (urlParts.Length >= 4) ? urlParts[3] : null;
                    string urlConnectorId = (urlParts.Length >= 5) ? urlParts[4] : null;
                    string urlParam = (urlParts.Length >= 6) ? urlParts[5] : null;
                    _logger.LogTrace("OCPPMiddleware => cmd='{0}' / cpId='{1}' / conId='{2}' / param='{3}' / FullPath='{4}')", cmd, urlChargePointId, urlConnectorId, urlParam, context.Request.Path.Value);

                    if (cmd == "Status")
                    {
                        try
                        {
                            List<ChargePointStatus> statusList = new List<ChargePointStatus>();
                            foreach (ChargePointStatus status in _chargePointStatusDict.Values)
                            {
                                if (status.WebSocket != null && status.WebSocket.State == WebSocketState.Open)
                                {
                                    statusList.Add(status);
                                }
                            }
                            string jsonStatus = JsonConvert.SerializeObject(statusList);
                            context.Response.ContentType = "application/json";
                            await context.Response.WriteAsync(jsonStatus);
                        }
                        catch (Exception exp)
                        {
                            _logger.LogError(exp, "OCPPMiddleware => Error: {0}", exp.Message);
                            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                        }
                    }
                    else if (cmd == "Reset")
                    {
                        if (!string.IsNullOrEmpty(urlChargePointId))
                        {
                            try
                            {
                                if (TryResolveLiveChargePointStatus(urlChargePointId, "OCPPMiddleware Reset", out var status))
                                {
                                    // Send message to chargepoint
                                    if (status.Protocol == Protocol_OCPP21)
                                    {
                                        // OCPP V2.1
                                        await Reset21(status, context, dbContext);
                                    }
                                    else if (status.Protocol == Protocol_OCPP201)
                                    {
                                        // OCPP V2.0
                                        await Reset20(status, context, dbContext);
                                    }
                                    else
                                    {
                                        // OCPP V1.6
                                        await Reset16(status, context, dbContext);
                                    }
                                }
                                else
                                {
                                    // Chargepoint offline
                                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                                }
                            }
                            catch (Exception exp)
                            {
                                _logger.LogError(exp, "OCPPMiddleware SoftReset => Error: {0}", exp.Message);
                                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                            }
                        }
                        else
                        {
                            _logger.LogError("OCPPMiddleware SoftReset => Missing chargepoint ID");
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        }
                    }
                    else if (cmd == "UnlockConnector")
                    {
                        if (!string.IsNullOrEmpty(urlChargePointId))
                        {
                            try
                            {
                                if (TryResolveLiveChargePointStatus(urlChargePointId, "OCPPMiddleware UnlockConnector", out var status))
                                {
                                    // Send message to chargepoint
                                    if (status.Protocol == Protocol_OCPP21)
                                    {
                                        // OCPP V2.1
                                        await UnlockConnector21(status, context, dbContext, urlConnectorId);
                                    }
                                    else if (status.Protocol == Protocol_OCPP201)
                                    {
                                        // OCPP V2.0
                                        await UnlockConnector20(status, context, dbContext, urlConnectorId);
                                    }
                                    else
                                    {
                                        // OCPP V1.6
                                        await UnlockConnector16(status, context, dbContext, urlConnectorId);
                                    }
                                }
                                else
                                {
                                    // Chargepoint offline
                                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                                }
                            }
                            catch (Exception exp)
                            {
                                _logger.LogError(exp, "OCPPMiddleware UnlockConnector => Error: {0}", exp.Message);
                                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                            }
                        }
                        else
                        {
                            _logger.LogError("OCPPMiddleware UnlockConnector => Missing chargepoint ID");
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        }
                    }
                    else if (cmd == "SetChargingLimit")
                    {
                        if (!string.IsNullOrEmpty(urlChargePointId))
                        {
                            if (!string.IsNullOrEmpty(urlParam))
                            {
                                string pattern = @"^([0-9]+)([AWaw])$";
                                Regex regex = new Regex(pattern, RegexOptions.IgnoreCase);
                                Match match = regex.Match(urlParam);
                                if (match.Success && match.Groups.Count == 3)
                                {
                                    int power = int.Parse(match.Groups[1].Value);
                                    string unit = match.Groups[2].Value;

                                    try
                                    {
                                        if (TryResolveLiveChargePointStatus(urlChargePointId, "OCPPMiddleware SetChargingProfile", out var status))
                                        {
                                            // Send message to chargepoint
                                            if (status.Protocol == Protocol_OCPP21)
                                            {
                                                // OCPP V2.1
                                                await SetChargingProfile21(status, context, dbContext, urlConnectorId, power, unit);
                                            }
                                            else if (status.Protocol == Protocol_OCPP201)
                                            {
                                                // OCPP V2.0
                                                await SetChargingProfile20(status, context, dbContext, urlConnectorId, power, unit);
                                            }
                                            else
                                            {
                                                // OCPP V1.6
                                                await SetChargingProfile16(status, context, dbContext, urlConnectorId, power, unit);
                                            }
                                        }
                                        else
                                        {
                                            // Chargepoint offline
                                            context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                                        }
                                    }
                                    catch (Exception exp)
                                    {
                                        _logger.LogError(exp, "OCPPMiddleware SetChargingProfile => Error: {0}", exp.Message);
                                        context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                                    }
                                }
                                else
                                {
                                    _logger.LogError("OCPPMiddleware SetChargingProfile => Bad parameter (power)");
                                    context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                                }
                            }
                            else
                            {
                                _logger.LogError("OCPPMiddleware SetChargingProfile => Missing parameter (power)");
                                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                            }
                        }
                        else
                        {
                            _logger.LogError("OCPPMiddleware SetChargingProfile => Missing chargepoint ID");
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        }
                    }
                    else if (cmd == "ClearChargingLimit")
                    {
                        if (!string.IsNullOrEmpty(urlChargePointId))
                        {
                            try
                            {
                                if (TryResolveLiveChargePointStatus(urlChargePointId, "OCPPMiddleware ClearChargingProfile", out var status))
                                {
                                    // Send message to chargepoint
                                    if (status.Protocol == Protocol_OCPP21)
                                    {
                                        // OCPP V2.1
                                        await ClearChargingProfile21(status, context, dbContext, urlConnectorId);
                                    }
                                    else if (status.Protocol == Protocol_OCPP201)
                                    {
                                        // OCPP V2.0
                                        await ClearChargingProfile20(status, context, dbContext, urlConnectorId);
                                    }
                                    else
                                    {
                                        // OCPP V1.6
                                        await ClearChargingProfile16(status, context, dbContext, urlConnectorId);
                                    }
                                }
                                else
                                {
                                    // Chargepoint offline
                                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                                }
                            }
                            catch (Exception exp)
                            {
                                _logger.LogError(exp, "OCPPMiddleware ClearChargingProfile => Error: {0}", exp.Message);
                                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                            }
                        }
                        else
                        {
                            _logger.LogError("OCPPMiddleware ClearChargingProfile => Missing chargepoint ID");
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        }
                    }
                    else if (cmd == "GetConfiguration")
                    {
                        if (!string.IsNullOrEmpty(urlChargePointId))
                        {
                            try
                            {
                                if (TryResolveLiveChargePointStatus(urlChargePointId, "OCPPMiddleware GetConfiguration", out var status))
                                {
                                    if (status.Protocol == Protocol_OCPP16)
                                    {
                                        await GetConfiguration16(status, context, dbContext, urlConnectorId);
                                    }
                                    else
                                    {
                                        context.Response.StatusCode = (int)HttpStatusCode.NotImplemented;
                                        context.Response.ContentType = "application/json";
                                        await context.Response.WriteAsync("{\"status\":\"NotSupported\"}");
                                    }
                                }
                                else
                                {
                                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                                }
                            }
                            catch (Exception exp)
                            {
                                _logger.LogError(exp, "OCPPMiddleware GetConfiguration => Error: {0}", exp.Message);
                                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                            }
                        }
                        else
                        {
                            _logger.LogError("OCPPMiddleware GetConfiguration => Missing chargepoint ID");
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        }
                    }
                    else if (cmd == "ChangeConfiguration")
                    {
                        if (!string.IsNullOrEmpty(urlChargePointId) && !string.IsNullOrEmpty(urlConnectorId) && urlParam != null)
                        {
                            try
                            {
                                if (TryResolveLiveChargePointStatus(urlChargePointId, "OCPPMiddleware ChangeConfiguration", out var status))
                                {
                                    if (status.Protocol == Protocol_OCPP16)
                                    {
                                        await ChangeConfiguration16(status, context, dbContext, urlConnectorId, urlParam);
                                    }
                                    else
                                    {
                                        context.Response.StatusCode = (int)HttpStatusCode.NotImplemented;
                                        context.Response.ContentType = "application/json";
                                        await context.Response.WriteAsync("{\"status\":\"NotSupported\"}");
                                    }
                                }
                                else
                                {
                                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                                }
                            }
                            catch (Exception exp)
                            {
                                _logger.LogError(exp, "OCPPMiddleware ChangeConfiguration => Error: {0}", exp.Message);
                                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                            }
                        }
                        else
                        {
                            _logger.LogError("OCPPMiddleware ChangeConfiguration => Missing chargepoint ID, key or value");
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        }
                    }
                    else if (cmd == "StartTransaction")
                    {
                        if (!string.IsNullOrEmpty(urlChargePointId))
                        {
                            // Parse connector id (int value)
                            if (!string.IsNullOrEmpty(urlConnectorId) && int.TryParse(urlConnectorId, out int connectorId))
                            {
                                if (!string.IsNullOrEmpty(urlParam))
                                {
                                    try
                                    {
                                        if (TryResolveLiveChargePointStatus(urlChargePointId, "OCPPMiddleware StartTransaction", out var status))
                                        {
                                            // Send message to chargepoint
                                            if (status.Protocol == Protocol_OCPP21)
                                            {
                                                // OCPP V2.1
                                                await RequestStartTransaction21(status, context, dbContext, urlConnectorId, urlParam);
                                            }
                                            else if (status.Protocol == Protocol_OCPP201)
                                            {
                                                // OCPP V2.0
                                                await RequestStartTransaction20(status, context, dbContext, urlConnectorId, urlParam);
                                            }
                                            else
                                            {
                                                // OCPP V1.6
                                                await RemoteStartTransaction16(status, context, dbContext, urlConnectorId, urlParam);
                                            }
                                        }
                                        else
                                        {
                                            // Chargepoint offline
                                            context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                                        }
                                    }
                                    catch (Exception exp)
                                    {
                                        _logger.LogError(exp, "OCPPMiddleware StartTransaction => Error: {0}", exp.Message);
                                        context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                                    }
                                }
                                else
                                {
                                    _logger.LogError("OCPPMiddleware StartTransaction => Missing tokenId");
                                    context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                                }
                            }
                            else
                            {
                                _logger.LogError($"OCPPMiddleware StartTransaction => Bad connector ID: '{0}'", urlConnectorId);
                                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                            }
                        }
                        else
                        {
                            _logger.LogError("OCPPMiddleware StartTransaction => Missing chargepoint ID");
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        }
                    }
                    else if (cmd == "StopTransaction")
                    {
                        if (!string.IsNullOrEmpty(urlChargePointId))
                        {
                            try
                            {
                                if (TryResolveLiveChargePointStatus(urlChargePointId, "OCPPMiddleware StopTransaction", out var status))
                                {
                                    if (int.TryParse(urlConnectorId, out int connectorId))
                                    {
                                        // Check last (open) transaction
                                        Transaction transaction = dbContext.Transactions
                                            .Where(t => t.ChargePointId == urlChargePointId && t.ConnectorId == connectorId)
                                            .OrderByDescending(t => t.TransactionId)
                                            .FirstOrDefault();

                                        if (transaction != null && !transaction.StopTime.HasValue)
                                        {
                                            string apiResult = await ExecuteRemoteStopAsync(status, dbContext, transaction);
                                            context.Response.StatusCode = (int)HttpStatusCode.OK;
                                            context.Response.ContentType = "application/json";
                                            await context.Response.WriteAsync(apiResult);
                                        }
                                        else
                                        {
                                            _logger.LogError("OCPPMiddleware StopTransaction => connector '{0}' has no open transaction", urlConnectorId);
                                            context.Response.StatusCode = (int)HttpStatusCode.FailedDependency;
                                        }
                                    }
                                    else
                                    {
                                        _logger.LogError("OCPPMiddleware StopTransaction => invalid connector ID: '{0}'", urlConnectorId);
                                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                                    }
                                }
                                else
                                {
                                    // Chargepoint offline
                                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                                }
                            }
                            catch (Exception exp)
                            {
                                _logger.LogError(exp, "OCPPMiddleware StopTransaction => Error: {0}", exp.Message);
                                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                            }
                        }
                        else
                        {
                            _logger.LogError("OCPPMiddleware StopTransaction => Missing chargepoint ID");
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        }
                    }
                    else if (string.Equals(cmd, "Payments", StringComparison.OrdinalIgnoreCase))
                    {
                        await HandlePaymentsAsync(context, dbContext, urlParts);
                    }
                    else
                    {
                        // Unknown action/function
                        _logger.LogWarning("OCPPMiddleware => action/function: {0}", cmd);
                        context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    }
                }
            }
            else if (context.Request.Path.StartsWithSegments("/"))
            {
                try
                {
                    bool showIndexInfo = _configuration.GetValue<bool>("ShowIndexInfo");
                    if (showIndexInfo)
                    {
                        _logger.LogTrace("OCPPMiddleware => Index status page");

                        context.Response.ContentType = "text/plain";
                        await context.Response.WriteAsync(string.Format("Running...\r\n\r\n{0} chargepoints connected", _chargePointStatusDict.Values.Count));
                    }
                    else
                    {
                        _logger.LogInformation("OCPPMiddleware => Root path with deactivated index page");
                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    }
                }
                catch (Exception exp)
                {
                    _logger.LogError(exp, "OCPPMiddleware => Error: {0}", exp.Message);
                    context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                }
            }
            else
            {
                _logger.LogWarning("OCPPMiddleware => Bad path request");
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
            }
        }

        private async Task HandlePaymentsAsync(HttpContext context, OCPPCoreContext dbContext, string[] urlParts)
        {
            string action = (urlParts.Length >= 4) ? urlParts[3] : null;

            if (string.Equals(action, "Webhook", StringComparison.OrdinalIgnoreCase))
            {
                await HandlePaymentWebhookAsync(context, dbContext);
                return;
            }

            if (_paymentCoordinator == null || !_paymentCoordinator.IsEnabled)
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("{\"status\":\"Disabled\"}");
                return;
            }

            if (string.Equals(action, "Create", StringComparison.OrdinalIgnoreCase))
            {
                await HandlePaymentCreateAsync(context, dbContext);
            }
            else if (string.Equals(action, "Resume", StringComparison.OrdinalIgnoreCase))
            {
                await HandlePaymentResumeAsync(context, dbContext);
            }
            else if (string.Equals(action, "Confirm", StringComparison.OrdinalIgnoreCase))
            {
                await HandlePaymentConfirmAsync(context, dbContext);
            }
            else if (string.Equals(action, "Cancel", StringComparison.OrdinalIgnoreCase))
            {
                await HandlePaymentCancelAsync(context, dbContext);
            }
            else if (string.Equals(action, "Stop", StringComparison.OrdinalIgnoreCase))
            {
                await HandlePaymentStopAsync(context, dbContext);
            }
            else if (string.Equals(action, "RequestR1", StringComparison.OrdinalIgnoreCase))
            {
                await HandlePaymentRequestR1Async(context, dbContext);
            }
            else if (string.Equals(action, "Status", StringComparison.OrdinalIgnoreCase))
            {
                await HandlePaymentStatusAsync(context, dbContext);
            }
            else
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
            }
        }

        private async Task HandlePaymentCreateAsync(HttpContext context, OCPPCoreContext dbContext)
        {
            if (!HttpMethods.IsPost(context.Request.Method))
            {
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            PaymentSessionRequest request = null;
            try
            {
                string body = await ReadRequestBodyAsync(context);
                request = JsonConvert.DeserializeObject<PaymentSessionRequest>(body);
            }
            catch (Exception exp)
            {
                _logger.LogError(exp, "Payments/Create => Invalid request payload");
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                return;
            }

            if (request == null ||
                string.IsNullOrWhiteSpace(request.ChargePointId) ||
                string.IsNullOrWhiteSpace(request.ChargeTagId))
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                return;
            }

            _logger.LogInformation("Payments/Create => Incoming request cp={ChargePointId} connector={ConnectorId} tag={ChargeTagId} origin={Origin} ip={RemoteIp} ua={UserAgent}",
                request.ChargePointId,
                request.ConnectorId,
                request.ChargeTagId,
                request.Origin ?? "(none)",
                context.Connection.RemoteIpAddress?.ToString() ?? "(unknown)",
                context.Request.Headers["User-Agent"].FirstOrDefault() ?? "(none)");

            if (!TryResolveLiveChargePointStatus(request.ChargePointId, "Payments/Create", out var status))
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                await context.Response.WriteAsync("{\"status\":\"ChargerOffline\",\"reason\":\"Offline\"}");
                return;
            }

            if (IsConnectorBusy(dbContext, request.ChargePointId, request.ConnectorId, status, null, out var busyReason))
            {
                _logger.LogWarning("Payments/Create => Connector busy: {Reason} (ChargePoint={ChargePointId}, Connector={ConnectorId})",
                    busyReason ?? "Unknown",
                    request.ChargePointId,
                    request.ConnectorId);
                context.Response.StatusCode = (int)HttpStatusCode.Conflict;
                await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                {
                    status = ConnectorBusyStatus,
                    reason = busyReason ?? "Unknown"
                }));
                return;
            }

            var chargePoint = dbContext.ChargePoints.Find(request.ChargePointId);
            bool isFreeChargePoint = chargePoint != null &&
                                     (chargePoint.FreeChargingEnabled ||
                                      ((chargePoint.PricePerKwh <= 0m) && (chargePoint.ConnectorUsageFeePerMinute <= 0m) && (chargePoint.UserSessionFee <= 0m)));
            bool isFreeChargeTag = HasFreeTagAccess(dbContext, chargePoint, request.ChargeTagId);

            if (isFreeChargePoint || isFreeChargeTag)
            {
                EnsureChargeTagExists(dbContext, request.ChargeTagId);
                string apiResult = await ExecuteRemoteStartAsync(status, dbContext, request.ConnectorId.ToString(), request.ChargeTagId);
                string remoteStatus = ExtractStatusFromApiResult(apiResult);
                if (string.IsNullOrWhiteSpace(apiResult))
                {
                    apiResult = "{\"status\":\"Error\"}";
                }
                // Do not mutate persisted status on remote-start acceptance; rely on charger reports.
                context.Response.StatusCode = (int)HttpStatusCode.OK;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(apiResult);
                return;
            }

            try
            {
                var result = _paymentCoordinator.CreateCheckoutSession(dbContext, request);
                var payload = new
                {
                    status = "Redirect",
                    checkoutUrl = result.CheckoutUrl,
                    reservationId = result.Reservation.ReservationId,
                    currency = result.Reservation.Currency,
                    maxAmountCents = result.Reservation.MaxAmountCents,
                    maxEnergyKwh = result.Reservation.MaxEnergyKwh
                };

                context.Response.StatusCode = (int)HttpStatusCode.OK;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonConvert.SerializeObject(payload));
            }
            catch (InvalidOperationException ioe) when (string.Equals(ioe.Message, ConnectorBusyStatus, StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = (int)HttpStatusCode.Conflict;
                await context.Response.WriteAsync("{\"status\":\"ConnectorBusy\",\"reason\":\"ActiveReservation\"}");
            }
            catch (Exception exp)
            {
                _logger.LogError(exp, "Payments/Create => Exception: {0}", exp.Message);
                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                await context.Response.WriteAsync("{\"status\":\"Error\"}");
            }
        }

        private async Task HandlePaymentConfirmAsync(HttpContext context, OCPPCoreContext dbContext)
        {
            if (!HttpMethods.IsPost(context.Request.Method))
            {
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            PaymentConfirmRequest request = null;
            try
            {
                string body = await ReadRequestBodyAsync(context);
                request = JsonConvert.DeserializeObject<PaymentConfirmRequest>(body);
            }
            catch (Exception exp)
            {
                _logger.LogError(exp, "Payments/Confirm => Invalid request payload");
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                return;
            }

            if (request == null || request.ReservationId == Guid.Empty || string.IsNullOrWhiteSpace(request.CheckoutSessionId))
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                return;
            }

            _logger.LogInformation("Payments/Confirm => Incoming request reservation={ReservationId} checkoutSession={CheckoutSessionId} ip={RemoteIp}",
                request.ReservationId,
                request.CheckoutSessionId,
                context.Connection.RemoteIpAddress?.ToString() ?? "(unknown)");

            var confirmation = _paymentCoordinator.ConfirmReservation(dbContext, request.ReservationId, request.CheckoutSessionId);
            if (!confirmation.Success)
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonConvert.SerializeObject(new { status = confirmation.Status, error = confirmation.Error }));
                return;
            }

            var reservation = confirmation.Reservation;
            var tryStart = await TryStartChargingOrAwaitPlugAsync(dbContext, reservation, "Confirm");

            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonConvert.SerializeObject(new { status = tryStart.Status, reason = tryStart.Reason }));
        }

        private async Task HandlePaymentResumeAsync(HttpContext context, OCPPCoreContext dbContext)
        {
            if (!HttpMethods.IsPost(context.Request.Method))
            {
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            PaymentCancelRequest request = null;
            try
            {
                string body = await ReadRequestBodyAsync(context);
                request = JsonConvert.DeserializeObject<PaymentCancelRequest>(body);
            }
            catch (Exception exp)
            {
                _logger.LogError(exp, "Payments/Resume => Invalid request payload");
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                return;
            }

            if (request == null || request.ReservationId == Guid.Empty)
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                return;
            }

            _logger.LogInformation("Payments/Resume => Incoming resume reservation={ReservationId} ip={RemoteIp}",
                request.ReservationId,
                context.Connection.RemoteIpAddress?.ToString() ?? "(unknown)");

            var resume = _paymentCoordinator.ResumeReservation(dbContext, request.ReservationId);
            context.Response.StatusCode = string.Equals(resume.Status, "NotFound", StringComparison.OrdinalIgnoreCase)
                ? (int)HttpStatusCode.NotFound
                : (int)HttpStatusCode.OK;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonConvert.SerializeObject(new
            {
                status = resume.Status,
                reservationId = resume.Reservation?.ReservationId ?? request.ReservationId,
                reservationStatus = resume.Reservation?.Status,
                checkoutUrl = resume.CheckoutUrl,
                error = resume.Error,
                locksConnector = PaymentReservationStatus.LocksConnector(resume.Reservation?.Status)
            }));
        }

        private async Task HandlePaymentCancelAsync(HttpContext context, OCPPCoreContext dbContext)
        {
            if (!HttpMethods.IsPost(context.Request.Method))
            {
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            PaymentCancelRequest request = null;
            try
            {
                string body = await ReadRequestBodyAsync(context);
                request = JsonConvert.DeserializeObject<PaymentCancelRequest>(body);
            }
            catch (Exception exp)
            {
                _logger.LogError(exp, "Payments/Cancel => Invalid request payload");
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                return;
            }

            if (request == null || request.ReservationId == Guid.Empty)
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                return;
            }

            _logger.LogInformation("Payments/Cancel => Incoming cancel reservation={ReservationId} reason={Reason}", request.ReservationId, request.Reason ?? "(none)");

            _paymentCoordinator.CancelReservation(dbContext, request.ReservationId, request.Reason);
            try
            {
                var reservation = dbContext.ChargePaymentReservations.Find(request.ReservationId);
                if (reservation != null && TryResolveLiveChargePointStatus(reservation.ChargePointId, "Payments/Cancel", out var cpStatus, logIfMissing: false))
                {
                    // Reservation profile disabled (was CancelReservation)
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Payments/Cancel => Best-effort CancelReservation failed");
            }

            var updatedReservation = dbContext.ChargePaymentReservations.Find(request.ReservationId);
            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonConvert.SerializeObject(new
            {
                status = updatedReservation?.Status ?? "NotFound",
                cancellationApplied = string.Equals(updatedReservation?.Status, PaymentReservationStatus.Cancelled, StringComparison.OrdinalIgnoreCase)
            }));
        }

        private async Task HandlePaymentStopAsync(HttpContext context, OCPPCoreContext dbContext)
        {
            if (!HttpMethods.IsPost(context.Request.Method))
            {
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            PaymentStopRequest request = null;
            try
            {
                string body = await ReadRequestBodyAsync(context);
                request = JsonConvert.DeserializeObject<PaymentStopRequest>(body);
            }
            catch (Exception exp)
            {
                _logger.LogError(exp, "Payments/Stop => Invalid request payload");
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                return;
            }

            if (request == null || request.ReservationId == Guid.Empty)
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                return;
            }

            var reservation = await dbContext.ChargePaymentReservations.FindAsync(request.ReservationId);
            if (reservation == null)
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("{\"status\":\"NotFound\"}");
                return;
            }

            if (PaymentReservationStatus.IsTerminal(reservation.Status))
            {
                context.Response.StatusCode = (int)HttpStatusCode.OK;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                {
                    status = "AlreadyStopped",
                    reservationId = reservation.ReservationId,
                    reservationStatus = reservation.Status
                }));
                return;
            }

            var transaction = ResolveReservationTransaction(dbContext, reservation);
            bool hasActiveTransaction = transaction != null && !transaction.StopTime.HasValue;
            bool canCancelReservationOnly =
                !hasActiveTransaction &&
                (string.Equals(reservation.Status, PaymentReservationStatus.Authorized, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(reservation.Status, PaymentReservationStatus.StartRequested, StringComparison.OrdinalIgnoreCase));

            if (canCancelReservationOnly)
            {
                _paymentCoordinator.CancelReservation(dbContext, reservation.ReservationId, "Stopped by user before charging started.");
                var updatedReservation = await dbContext.ChargePaymentReservations.FindAsync(reservation.ReservationId);

                context.Response.StatusCode = (int)HttpStatusCode.OK;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                {
                    status = "Cancelled",
                    reservationId = updatedReservation?.ReservationId ?? reservation.ReservationId,
                    reservationStatus = updatedReservation?.Status ?? reservation.Status
                }));
                return;
            }

            if (!hasActiveTransaction)
            {
                context.Response.StatusCode = (int)HttpStatusCode.OK;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                {
                    status = "NoOpenTransaction",
                    reservationId = reservation.ReservationId,
                    reservationStatus = reservation.Status
                }));
                return;
            }

            if (!TryResolveLiveChargePointStatus(reservation.ChargePointId, "Payments/Stop", out var chargePointStatus))
            {
                context.Response.StatusCode = (int)HttpStatusCode.OK;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                {
                    status = "ChargerOffline",
                    reservationId = reservation.ReservationId,
                    reservationStatus = reservation.Status,
                    transactionId = transaction.TransactionId
                }));
                return;
            }

            string apiResult = await ExecuteRemoteStopAsync(chargePointStatus, dbContext, transaction);
            if (string.IsNullOrWhiteSpace(apiResult))
            {
                apiResult = "{\"status\":\"Error\"}";
            }

            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(apiResult);
        }

        private async Task HandlePaymentRequestR1Async(HttpContext context, OCPPCoreContext dbContext)
        {
            if (!HttpMethods.IsPost(context.Request.Method))
            {
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            PaymentR1InvoiceRequest request = null;
            try
            {
                string body = await ReadRequestBodyAsync(context);
                request = JsonConvert.DeserializeObject<PaymentR1InvoiceRequest>(body);
            }
            catch (Exception exp)
            {
                _logger.LogError(exp, "Payments/RequestR1 => Invalid request payload");
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("{\"status\":\"Invalid\",\"error\":\"Invalid payload\"}");
                return;
            }

            if (request == null || request.ReservationId == Guid.Empty)
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("{\"status\":\"Invalid\",\"error\":\"reservationId required\"}");
                return;
            }

            request.BuyerOib = NormalizeOibDigits(request.BuyerOib);
            request.BuyerCompanyName = (request.BuyerCompanyName ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(request.BuyerOib) || !IsValidOib(request.BuyerOib))
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("{\"status\":\"InvalidOib\",\"error\":\"Valid OIB (11 digits) is required.\"}");
                return;
            }

            _logger.LogInformation(
                "Payments/RequestR1 => Incoming request reservation={ReservationId} hasCompany={HasCompany} ip={RemoteIp}",
                request.ReservationId,
                !string.IsNullOrWhiteSpace(request.BuyerCompanyName),
                context.Connection.RemoteIpAddress?.ToString() ?? "(unknown)");

            var updateResult = _paymentCoordinator.RequestR1Invoice(dbContext, request);
            if (!updateResult.Success)
            {
                context.Response.StatusCode = string.Equals(updateResult.Status, "NotFound", StringComparison.OrdinalIgnoreCase)
                    ? (int)HttpStatusCode.NotFound
                    : (int)HttpStatusCode.BadRequest;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                {
                    status = updateResult.Status ?? "Error",
                    error = updateResult.Error ?? "Unable to update R1 request."
                }));
                return;
            }

            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonConvert.SerializeObject(new
            {
                status = updateResult.Status ?? "Updated",
                reservationId = updateResult.Reservation?.ReservationId ?? request.ReservationId,
                buyerCompanyName = updateResult.BuyerCompanyName,
                buyerOib = updateResult.BuyerOib
            }));
        }

        private async Task HandlePaymentStatusAsync(HttpContext context, OCPPCoreContext dbContext)
        {
            // Accept GET for easy polling
            if (!HttpMethods.IsGet(context.Request.Method))
            {
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            if (!Guid.TryParse(context.Request.Query["reservationId"], out var reservationId) || reservationId == Guid.Empty)
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                await context.Response.WriteAsync("{\"status\":\"Error\",\"message\":\"reservationId required\"}");
                return;
            }

            var reservation = await dbContext.ChargePaymentReservations.FindAsync(reservationId);
            if (reservation == null)
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                await context.Response.WriteAsync("{\"status\":\"NotFound\"}");
                return;
            }

            string liveStatus = null;
            string liveOcppStatus = null;
            string persistedStatus = null;
            DateTime? persistedStatusTime = null;
            double? liveChargeRateKw = null;
            double? liveCurrentImportA = null;
            double? liveMeterKwh = null;
            double? liveSoC = null;
            DateTime? liveMeterValueAtUtc = null;
            string connectorName = null;
            string publicDisplayCode = null;
            string publicConnectorShortCode = null;
            string publicConnectorCode = null;
            bool activeTx = false;
            bool activeReservation = false;
            StartabilityResult startability = null;
            Transaction transaction = null;
            IdleFeeSnapshot idleFeeSnapshot = null;
            var latestInvoiceLog = InvoiceSubmissionLogLookup.TryGetLatest(
                dbContext,
                reservation.ReservationId,
                _logger,
                "Payments/Status");
            var latestInvoiceUrl = InvoiceSubmissionLogLookup.GetPreferredDocumentUrl(latestInvoiceLog);

            TryResolveLiveChargePointStatus(reservation.ChargePointId, "Payments/Status", out var cpStatus, logIfMissing: false);
            if (cpStatus?.OnlineConnectors != null &&
                cpStatus.OnlineConnectors.TryGetValue(reservation.ConnectorId, out var online))
            {
                liveOcppStatus = GetLiveConnectorRawStatus(online);
                liveStatus = liveOcppStatus ?? (online.Status == ConnectorStatusEnum.Undefined ? null : online.Status.ToString());
                liveChargeRateKw = online.ChargeRateKW;
                liveCurrentImportA = online.CurrentImportA;
                liveMeterKwh = online.MeterKWH;
                liveSoC = online.SoC;
                if (online.MeterValueDate != default)
                {
                    liveMeterValueAtUtc = online.MeterValueDate.UtcDateTime;
                }
            }

            var persisted = dbContext.ConnectorStatuses.Find(reservation.ChargePointId, reservation.ConnectorId);
            if (persisted != null)
            {
                connectorName = string.IsNullOrWhiteSpace(persisted.ConnectorName)
                    ? null
                    : persisted.ConnectorName.Trim();
                persistedStatus = persisted.LastStatus;
                persistedStatusTime = persisted.LastStatusTime;
            }

            var chargePoint = dbContext.ChargePoints.Find(reservation.ChargePointId);
            if (chargePoint != null)
            {
                publicDisplayCode = NormalizePublicDisplayCode(chargePoint.PublicDisplayCode);
                publicConnectorShortCode = BuildPublicConnectorShortCode(publicDisplayCode, reservation.ConnectorId);
                publicConnectorCode = BuildPublicConnectorCode(publicDisplayCode, reservation.ConnectorId);
            }

            transaction = ResolveReservationTransaction(dbContext, reservation);
            activeTx = transaction != null && !transaction.StopTime.HasValue;
            if (!activeTx)
            {
                activeTx = dbContext.Transactions.Any(t =>
                    t.ChargePointId == reservation.ChargePointId &&
                    t.ConnectorId == reservation.ConnectorId &&
                    t.StopTime == null);
            }

            bool locksConnector = PaymentReservationStatus.LocksConnector(reservation.Status);
            bool otherActiveReservation = dbContext.ChargePaymentReservations.Any(r =>
                r.ChargePointId == reservation.ChargePointId &&
                r.ConnectorId == reservation.ConnectorId &&
                r.ReservationId != reservation.ReservationId &&
                r.Status != null &&
                PaymentReservationStatus.ConnectorLockStatuses.Contains(r.Status));

            activeReservation = locksConnector || otherActiveReservation;

            startability = GetConnectorStartability(dbContext, reservation.ChargePointId, reservation.ConnectorId, cpStatus, reservation.ReservationId);
            string blockingReason = otherActiveReservation
                ? "ActiveReservation"
                : (startability != null && !startability.Startable ? startability.Reason : null);

            if (transaction == null && reservation.TransactionId.HasValue)
            {
                try
                {
                    transaction = await dbContext.Transactions.FindAsync(reservation.TransactionId.Value);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Payments/Status => Unable to load transaction {TransactionId}", reservation.TransactionId.Value);
                }
            }

            if (transaction != null)
            {
                var flowOptions = PaymentFlowOptionsResolver.Resolve(_configuration, dbContext, GetPaymentFlowOptions());
                idleFeeSnapshot = IdleFeeCalculator.CalculateSnapshot(
                    transaction,
                    reservation,
                    flowOptions,
                    _utcNow(),
                    _logger);
            }

            double? liveSessionEnergyKwh = ResolveLiveSessionEnergyKwh(transaction, reservation, liveMeterKwh);
            double effectiveMaxEnergyKwh = transaction?.MaxEnergyKwh > 0
                ? transaction.MaxEnergyKwh
                : reservation.MaxEnergyKwh;
            DateTime? serverMaxEnergyStopRequestedAtUtc = null;
            bool serverMaxEnergyStopRequested = false;
            if (transaction != null &&
                _maxEnergyAutoStopTransactions.TryGetValue(transaction.TransactionId, out var maxEnergyStopRequestedAtUtcRaw))
            {
                serverMaxEnergyStopRequested = true;
                serverMaxEnergyStopRequestedAtUtc = maxEnergyStopRequestedAtUtcRaw;
            }
            bool serverMaxEnergyLimitReached = false;
            if (effectiveMaxEnergyKwh > 0)
            {
                if (serverMaxEnergyStopRequested ||
                    (transaction?.StopReason?.IndexOf("EnergyLimitReached", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    serverMaxEnergyLimitReached = true;
                }
                else if (liveSessionEnergyKwh.HasValue && IsMaxEnergyReached(liveSessionEnergyKwh.Value, effectiveMaxEnergyKwh))
                {
                    serverMaxEnergyLimitReached = true;
                }
            }
            string sessionStage = ResolveSessionStage(reservation, transaction, activeTx, liveStatus, liveOcppStatus, persistedStatus);

            var payload = new
            {
                status = reservation.Status,
                sessionStage,
                reservationId = reservation.ReservationId,
                chargePointId = reservation.ChargePointId,
                connectorId = reservation.ConnectorId,
                connectorName,
                publicConnectorShortCode,
                publicConnectorCode,
                transactionId = transaction?.TransactionId ?? reservation.TransactionId,
                startTransactionId = reservation.StartTransactionId,
                lastError = reservation.LastError,
                failureCode = reservation.FailureCode,
                failureMessage = reservation.FailureMessage,
                ocppIdTag = reservation.OcppIdTag,
                createdAtUtc = reservation.CreatedAtUtc,
                updatedAtUtc = reservation.UpdatedAtUtc,
                authorizedAtUtc = reservation.AuthorizedAtUtc,
                capturedAtUtc = reservation.CapturedAtUtc,
                startDeadlineAtUtc = reservation.StartDeadlineAtUtc,
                awaitingPlug = reservation.AwaitingPlug,
                remoteStartSentAtUtc = reservation.RemoteStartSentAtUtc,
                remoteStartResult = reservation.RemoteStartResult,
                remoteStartAcceptedAtUtc = reservation.RemoteStartAcceptedAtUtc,
                startTransactionAtUtc = reservation.StartTransactionAtUtc,
                stopTransactionAtUtc = reservation.StopTransactionAtUtc,
                disconnectedAtUtc = reservation.DisconnectedAtUtc,
                lastOcppEventAtUtc = reservation.LastOcppEventAtUtc,
                maxEnergyKwh = effectiveMaxEnergyKwh,
                transactionMaxEnergyKwh = transaction?.MaxEnergyKwh,
                pricePerKwh = reservation.PricePerKwh,
                userSessionFee = reservation.UserSessionFee,
                usageFeePerMinute = reservation.UsageFeePerMinute,
                startUsageFeeAfterMinutes = reservation.StartUsageFeeAfterMinutes,
                maxUsageFeeMinutes = reservation.MaxUsageFeeMinutes,
                usageFeeAnchorMinutes = reservation.UsageFeeAnchorMinutes,
                maxAmountCents = reservation.MaxAmountCents,
                capturedAmountCents = reservation.CapturedAmountCents,
                actualEnergyKwh = reservation.ActualEnergyKwh,
                liveStatus,
                liveOcppStatus,
                liveChargeRateKw,
                liveCurrentImportA,
                liveMeterKwh,
                liveSessionEnergyKwh,
                liveSoC,
                liveMeterValueAtUtc,
                persistedStatus,
                persistedStatusTime,
                activeTransaction = activeTx,
                activeReservation = activeReservation,
                locksConnector,
                otherActiveReservation,
                blockingReason,
                currency = reservation.Currency,
                transactionMeterStart = transaction?.MeterStart,
                transactionMeterStop = transaction?.MeterStop,
                transactionEnergyKwh = transaction?.EnergyKwh,
                transactionEnergyCost = transaction?.EnergyCost,
                transactionUsageFeeMinutes = transaction?.UsageFeeMinutes,
                transactionUsageFeeAmount = transaction?.UsageFeeAmount,
                transactionIdleFeeMinutes = transaction?.IdleUsageFeeMinutes,
                transactionIdleFeeAmount = transaction?.IdleUsageFeeAmount,
                transactionSessionFeeAmount = transaction?.UserSessionFeeAmount,
                serverMaxEnergyStopRequested,
                serverMaxEnergyStopRequestedAtUtc,
                serverMaxEnergyLimitReached,
                suspendedEvSinceUtc = idleFeeSnapshot?.SuspendedSinceUtc,
                idleFeeStartsAtUtc = idleFeeSnapshot?.IdleFeeStartAtUtc,
                idleBillingPausedByWindow = idleFeeSnapshot?.BillingPausedByExcludedWindow ?? false,
                liveIdleFeeMinutes = idleFeeSnapshot?.TotalMinutes,
                liveIdleFeeAmount = idleFeeSnapshot?.TotalAmount,
                startable = startability?.Startable ?? false,
                startableReason = startability?.Reason,
                startableReasons = startability?.Reasons,
                startableLiveStatus = startability?.LiveStatus,
                startablePersistedStatus = startability?.PersistedStatus,
                startablePersistedAgeMinutes = startability?.PersistedStatusAgeMinutes,
                invoice = latestInvoiceLog == null
                    ? null
                    : new
                    {
                        provider = latestInvoiceLog.Provider,
                        mode = latestInvoiceLog.Mode,
                        status = latestInvoiceLog.Status,
                        invoiceKind = latestInvoiceLog.InvoiceKind,
                        providerOperation = latestInvoiceLog.ProviderOperation,
                        httpStatusCode = latestInvoiceLog.HttpStatusCode,
                        externalDocumentId = latestInvoiceLog.ExternalDocumentId,
                        externalInvoiceNumber = latestInvoiceLog.ExternalInvoiceNumber,
                        externalPublicUrl = latestInvoiceLog.ExternalPublicUrl,
                        externalPdfUrl = latestInvoiceLog.ExternalPdfUrl,
                        invoiceUrl = latestInvoiceUrl,
                        providerResponseStatus = latestInvoiceLog.ProviderResponseStatus,
                        createdAtUtc = latestInvoiceLog.CreatedAtUtc,
                        completedAtUtc = latestInvoiceLog.CompletedAtUtc
                    }
            };

            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonConvert.SerializeObject(payload));
        }

        private bool IsConnectorBusy(OCPPCoreContext dbContext, string chargePointId, int connectorId, ChargePointStatus chargePointStatus, Guid? reservationToIgnore, out string busyReason)
        {
            busyReason = null;

            var status = chargePointStatus;
            if (status == null && _chargePointStatusDict.TryGetValue(chargePointId, out var existingStatus))
            {
                status = existingStatus;
            }

            OnlineConnectorStatus liveConnectorStatus = null;
            if (status != null &&
                status.OnlineConnectors != null &&
                status.OnlineConnectors.TryGetValue(connectorId, out liveConnectorStatus))
            {
                string liveRawStatus = GetLiveConnectorRawStatus(liveConnectorStatus);
                if (!string.IsNullOrWhiteSpace(liveRawStatus))
                {
                    busyReason = $"LiveStatus:{liveRawStatus}";
                }
            }

            string currentLiveStatus = GetLiveConnectorRawStatus(liveConnectorStatus);
            bool hasAuthoritativeLiveStatus = !string.IsNullOrWhiteSpace(currentLiveStatus);
            bool busy = hasAuthoritativeLiveStatus && !OcppConnectorStatus.IsStartable(currentLiveStatus);

            int activeTransactionCount = 0;
            try
            {
                activeTransactionCount = dbContext.Transactions.Count(t =>
                    t.ChargePointId == chargePointId &&
                    t.ConnectorId == connectorId &&
                    t.StopTime == null);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "IsConnectorBusy => Unable to count transactions for cp={ChargePointId} connector={ConnectorId}", chargePointId, connectorId);
            }

            if (!busy && activeTransactionCount > 0)
            {
                busy = true;
                busyReason = "OpenTransaction";
            }

            int activeReservationCount = 0;
            try
            {
                activeReservationCount = dbContext.ChargePaymentReservations.Count(r =>
                    r.ChargePointId == chargePointId &&
                    r.ConnectorId == connectorId &&
                    (!reservationToIgnore.HasValue || r.ReservationId != reservationToIgnore.Value) &&
                    r.Status != null &&
                    PaymentReservationStatus.ConnectorLockStatuses.Contains(r.Status));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "IsConnectorBusy => Unable to count reservations for cp={ChargePointId} connector={ConnectorId}", chargePointId, connectorId);
            }

            if (!busy && activeReservationCount > 0)
            {
                busy = true;
                busyReason = "ActiveReservation";
            }

            string persistedStatusValue = null;
            double? persistedStatusAgeMinutes = null;
            if (!hasAuthoritativeLiveStatus)
            {
                var persistedStatus = dbContext.ConnectorStatuses
                    .Where(cs => cs.ChargePointId == chargePointId && cs.ConnectorId == connectorId)
                    .Select(cs => new { cs.LastStatus, cs.LastStatusTime })
                    .FirstOrDefault();

                if (persistedStatus != null)
                {
                    persistedStatusValue = persistedStatus.LastStatus;
                    if (persistedStatus.LastStatusTime.HasValue)
                    {
                        persistedStatusAgeMinutes = (_utcNow() - persistedStatus.LastStatusTime.Value).TotalMinutes;
                    }

                    if (!OcppConnectorStatus.IsStartable(persistedStatus.LastStatus))
                    {
                        bool isStale = persistedStatusAgeMinutes.HasValue &&
                                       TimeSpan.FromMinutes(persistedStatusAgeMinutes.Value) > PersistedStatusStaleAfter;
                        if (!isStale && !busy)
                        {
                            busy = true;
                            busyReason = $"PersistedStatus:{persistedStatus.LastStatus}";
                        }
                        else if (!busy)
                        {
                            busyReason = $"StalePersistedStatus:{persistedStatus.LastStatus}";
                        }
                    }
                }
            }

            busyReason ??= "Available";

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "IsConnectorBusy trace cp={ChargePointId} connector={ConnectorId} decision={Decision} reason={Reason} wsState={WebSocketState} liveStatus={LiveStatus} meterTs={MeterTs} activeTx={ActiveTxCount} activeRes={ActiveResCount} persistedStatus={PersistedStatus} persistedAgeMin={PersistedAge} ignoreReservation={IgnoreReservation}",
                    chargePointId,
                    connectorId,
                    busy ? "Busy" : "Available",
                    busyReason,
                    status?.WebSocket?.State.ToString() ?? "(none)",
                    currentLiveStatus ?? "(none)",
                    liveConnectorStatus?.MeterValueDate,
                    activeTransactionCount,
                    activeReservationCount,
                    persistedStatusValue ?? "(none)",
                    persistedStatusAgeMinutes?.ToString("0.#") ?? "(n/a)",
                    reservationToIgnore?.ToString() ?? "(none)");
            }

            return busy;
        }

        private bool IsConnectorBusy(OCPPCoreContext dbContext, string chargePointId, int connectorId, ChargePointStatus chargePointStatus = null, Guid? reservationToIgnore = null)
        {
            return IsConnectorBusy(dbContext, chargePointId, connectorId, chargePointStatus, reservationToIgnore, out _);
        }

        private class StartabilityResult
        {
            public bool Startable { get; set; }
            public string Reason { get; set; }
            public List<string> Reasons { get; set; } = new List<string>();
            public string LiveStatus { get; set; }
            public string PersistedStatus { get; set; }
            public double? PersistedStatusAgeMinutes { get; set; }
            public bool ActiveReservation { get; set; }
            public bool ActiveTransaction { get; set; }
            public bool Offline { get; set; }
        }

        private StartabilityResult GetConnectorStartability(OCPPCoreContext dbContext, string chargePointId, int connectorId, ChargePointStatus chargePointStatus, Guid? reservationToIgnore)
        {
            var result = new StartabilityResult { Startable = false, Reason = "Unknown" };

            var status = chargePointStatus;
            if (status == null && _chargePointStatusDict.TryGetValue(chargePointId, out var existingStatus))
            {
                status = existingStatus;
            }

            if (status == null || status.WebSocket == null || status.WebSocket.State != WebSocketState.Open)
            {
                result.Offline = true;
                result.Reason = "Offline";
                result.Reasons.Add(result.Reason);
                return result;
            }

            OnlineConnectorStatus liveConnectorStatus = null;
            if (status.OnlineConnectors != null &&
                status.OnlineConnectors.TryGetValue(connectorId, out liveConnectorStatus))
            {
                result.LiveStatus = GetLiveConnectorRawStatus(liveConnectorStatus);
                if (!string.IsNullOrWhiteSpace(result.LiveStatus) &&
                    !OcppConnectorStatus.IsStartable(result.LiveStatus))
                {
                    result.Reason = $"Status:{result.LiveStatus}";
                    result.Reasons.Add(result.Reason);
                    return result;
                }

                if (string.Equals(result.LiveStatus, OcppConnectorStatus.Preparing, StringComparison.OrdinalIgnoreCase))
                {
                    result.Reasons.Add("Status:Preparing");
                }
            }

            // If offline but we have a very recent last message, allow start if no other locks
            // This prevents rejection when WS hiccups briefly.
            result.Offline = false;

            result.ActiveTransaction = dbContext.Transactions.Any(t =>
                t.ChargePointId == chargePointId &&
                t.ConnectorId == connectorId &&
                t.StopTime == null);
            if (result.ActiveTransaction)
            {
                result.Reason = "OpenTransaction";
                result.Reasons.Add(result.Reason);
                return result;
            }

            result.ActiveReservation = dbContext.ChargePaymentReservations.Any(r =>
                r.ChargePointId == chargePointId &&
                r.ConnectorId == connectorId &&
                (!reservationToIgnore.HasValue || r.ReservationId != reservationToIgnore.Value) &&
                PaymentReservationStatus.ConnectorLockStatuses.Contains(r.Status));
            if (result.ActiveReservation)
            {
                result.Reason = "ActiveReservation";
                result.Reasons.Add(result.Reason);
                return result;
            }

            if (string.IsNullOrWhiteSpace(result.LiveStatus))
            {
                var persistedStatus = dbContext.ConnectorStatuses
                    .Where(cs => cs.ChargePointId == chargePointId && cs.ConnectorId == connectorId)
                    .Select(cs => new { cs.LastStatus, cs.LastStatusTime })
                    .FirstOrDefault();

                if (persistedStatus != null)
                {
                    result.PersistedStatus = persistedStatus.LastStatus;
                    if (persistedStatus.LastStatusTime.HasValue)
                    {
                        result.PersistedStatusAgeMinutes = (_utcNow() - persistedStatus.LastStatusTime.Value).TotalMinutes;
                    }

                    if (!OcppConnectorStatus.IsStartable(persistedStatus.LastStatus))
                    {
                        bool isStale = result.PersistedStatusAgeMinutes.HasValue &&
                                       result.PersistedStatusAgeMinutes.Value > PersistedStatusStaleAfter.TotalMinutes;
                        if (!isStale)
                        {
                            result.Reason = $"PersistedStatus:{persistedStatus.LastStatus}";
                            result.Reasons.Add(result.Reason);
                            return result;
                        }
                    }
                }
            }

            result.Startable = true;
            result.Reason = "Startable";
            result.Reasons.Add(result.Reason);
            return result;
        }

        private void SetConnectorStatus(OCPPCoreContext dbContext, string chargePointId, int connectorId, string status)
        {
            if (string.IsNullOrWhiteSpace(chargePointId) || connectorId <= 0) return;

            var connector = dbContext.ConnectorStatuses.Find(chargePointId, connectorId);
            string previousStatus = connector?.LastStatus;
            DateTime? previousTime = connector?.LastStatusTime;
            if (connector == null)
            {
                connector = new ConnectorStatus
                {
                    ChargePointId = chargePointId,
                    ConnectorId = connectorId
                };
                dbContext.ConnectorStatuses.Add(connector);
            }

            connector.LastStatus = status;
            connector.LastStatusTime = _utcNow();
            dbContext.SaveChanges();

            _logger.LogInformation("SetConnectorStatus => cp={ChargePointId} connector={ConnectorId} {PreviousStatus}@{PreviousTime:u} -> {NewStatus}@{NewTime:u}",
                chargePointId,
                connectorId,
                previousStatus ?? "(none)",
                previousTime,
                connector.LastStatus,
                connector.LastStatusTime);
        }

        private async Task<(string Status, string Reason)> TryStartChargingAsync(OCPPCoreContext dbContext, ChargePaymentReservation reservation, string caller)
        {
            if (reservation == null) return ("Error", "NoReservation");

            _chargePointStatusDict.TryGetValue(reservation.ChargePointId, out var status);
            var startability = GetConnectorStartability(dbContext, reservation.ChargePointId, reservation.ConnectorId, status, reservation.ReservationId);
            if (!startability.Startable)
            {
                _logger.LogWarning("TryStartCharging => Not startable reason={Reason} cp={Cp} connector={Conn} reservation={Res}", startability.Reason, reservation.ChargePointId, reservation.ConnectorId, reservation.ReservationId);
                return ("ConnectorBusy", startability.Reason);
            }

            // Reservation profile disabled (was ReserveNow)

            // Idempotency: if already StartRequested, consider success
            if (reservation.Status == PaymentReservationStatus.StartRequested || reservation.Status == PaymentReservationStatus.Charging)
            {
                return ("StartRequested", "AlreadyRequested");
            }

            string idTag = ResolveRemoteStartIdTag(reservation, status?.Protocol);
            if (string.IsNullOrWhiteSpace(idTag))
            {
                reservation.Status = PaymentReservationStatus.StartRejected;
                reservation.LastError = "Missing idTag for remote start.";
                reservation.FailureCode = "RemoteStartRejected";
                reservation.FailureMessage = reservation.LastError;
                reservation.UpdatedAtUtc = _utcNow();
                dbContext.SaveChanges();
                _paymentCoordinator.CancelPaymentIntentIfCancelable(dbContext, reservation, reservation.LastError);
                _logger.LogWarning("TryStartCharging => Missing idTag reservation={ReservationId} caller={Caller}", reservation.ReservationId, caller);
                return ("StartRejected", "MissingIdTag");
            }

            if (string.Equals(status?.Protocol, Protocol_OCPP16, StringComparison.OrdinalIgnoreCase) && idTag.Length > 20)
            {
                reservation.Status = PaymentReservationStatus.StartRejected;
                reservation.LastError = $"idTag too long for OCPP 1.6 (len={idTag.Length})";
                reservation.FailureCode = "RemoteStartRejected";
                reservation.FailureMessage = reservation.LastError;
                reservation.UpdatedAtUtc = _utcNow();
                dbContext.SaveChanges();
                _paymentCoordinator.CancelPaymentIntentIfCancelable(dbContext, reservation, reservation.LastError);
                _logger.LogWarning("TryStartCharging => idTag too long for OCPP 1.6 reservation={ReservationId} len={Length}", reservation.ReservationId, idTag.Length);
                return ("StartRejected", "IdTagTooLong");
            }

            // Ensure idTag exists for authorization flow
            if (!string.IsNullOrWhiteSpace(reservation.OcppIdTag))
            {
                EnsureChargeTagExists(dbContext, reservation.OcppIdTag);
            }

            string connectorId = reservation.ConnectorId.ToString();
            string apiResult = await ExecuteRemoteStartAsync(status, dbContext, connectorId, idTag);
            string remoteStatus = ExtractStatusFromApiResult(apiResult);
            DateTime remoteStartResultAtUtc = _utcNow();

            await dbContext.Entry(reservation).ReloadAsync();

            reservation.RemoteStartSentAtUtc = remoteStartResultAtUtc;
            reservation.RemoteStartResult = remoteStatus;
            reservation.UpdatedAtUtc = remoteStartResultAtUtc;

            if (reservation.TransactionId.HasValue ||
                !string.Equals(reservation.Status, PaymentReservationStatus.Authorized, StringComparison.OrdinalIgnoreCase))
            {
                dbContext.SaveChanges();
                _logger.LogInformation(
                    "TryStartCharging => Preserving advanced reservation state after remote start result reservation={ReservationId} caller={Caller} status={Status} tx={TransactionId} remoteStatus={RemoteStatus}",
                    reservation.ReservationId,
                    caller,
                    reservation.Status,
                    reservation.TransactionId,
                    remoteStatus ?? "(null)");
                return (reservation.Status ?? PaymentReservationStatus.Authorized, "AlreadyAdvanced");
            }

            if (string.Equals(remoteStatus, "Accepted", StringComparison.OrdinalIgnoreCase))
            {
                reservation.Status = PaymentReservationStatus.StartRequested;
                reservation.RemoteStartAcceptedAtUtc = remoteStartResultAtUtc;
                dbContext.SaveChanges();
                _logger.LogInformation("TryStartCharging => Remote start accepted reservation={ReservationId} caller={Caller}", reservation.ReservationId, caller);
                return ("StartRequested", "Accepted");
            }

            if (string.Equals(remoteStatus, "Timeout", StringComparison.OrdinalIgnoreCase))
            {
                reservation.Status = PaymentReservationStatus.Authorized;
                reservation.LastError = "Remote start result: Timeout";
                reservation.UpdatedAtUtc = remoteStartResultAtUtc;
                dbContext.SaveChanges();
                _logger.LogWarning("TryStartCharging => Remote start timeout reservation={ReservationId}", reservation.ReservationId);
                return ("Timeout", "Timeout");
            }

            reservation.Status = PaymentReservationStatus.StartRejected;
            reservation.LastError = $"Remote start result: {remoteStatus}";
            reservation.FailureCode = "RemoteStartRejected";
            reservation.FailureMessage = reservation.LastError;
            dbContext.SaveChanges();
            _paymentCoordinator.CancelPaymentIntentIfCancelable(dbContext, reservation, reservation.LastError);
            // Reservation profile disabled (was CancelReservation)
            _logger.LogWarning("TryStartCharging => Remote start rejected reservation={ReservationId} status={RemoteStatus}", reservation.ReservationId, remoteStatus);
            return ("StartRejected", remoteStatus ?? "RemoteStartFailed");
        }

        private async Task HandlePaymentWebhookAsync(HttpContext context, OCPPCoreContext dbContext)
        {
            if (!HttpMethods.IsPost(context.Request.Method))
            {
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            string payload = await ReadRequestBodyAsync(context);
            string signature = context.Request.Headers["Stripe-Signature"].FirstOrDefault();

            _logger.LogInformation("Payments/Webhook => Incoming webhook ip={RemoteIp} payloadLength={PayloadLength} hasSignature={HasSignature}",
                context.Connection.RemoteIpAddress?.ToString() ?? "(unknown)",
                payload?.Length ?? 0,
                string.IsNullOrWhiteSpace(signature) ? "no" : "yes");

            _paymentCoordinator?.HandleWebhookEvent(dbContext, payload, signature);

            // Best-effort: if this is a checkout completion, kick TryStartCharging to avoid relying on browser redirect.
            try
            {
                var jobj = JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JObject>(payload);
                string type = jobj?["type"]?.ToString();
                if (string.Equals(type, "checkout.session.completed", StringComparison.OrdinalIgnoreCase))
                {
                    var dataObject = jobj?["data"]?["object"];
                    var metadataToken = dataObject?["metadata"];
                    string paymentStatus = dataObject?["payment_status"]?.ToString();
                    string resIdString = null;
                    if (metadataToken is Newtonsoft.Json.Linq.JObject metaObj && metaObj.TryGetValue("reservation_id", out var resToken))
                    {
                        resIdString = resToken?.ToString();
                    }

                    if (Guid.TryParse(resIdString, out var resId))
                    {
                        var reservation = dbContext.ChargePaymentReservations.Find(resId);
                        if (reservation != null)
                        {
                            if (reservation.Status == PaymentReservationStatus.Authorized ||
                                string.Equals(paymentStatus, "paid", StringComparison.OrdinalIgnoreCase))
                            {
                                await TryStartChargingOrAwaitPlugAsync(dbContext, reservation, "StripeWebhook");
                            }
                            else
                            {
                                _logger.LogInformation("Payments/Webhook => Skipping auto-start reservation={ReservationId} status={Status} paymentStatus={PaymentStatus}",
                                    reservation.ReservationId,
                                    reservation.Status,
                                    paymentStatus ?? "(none)");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Payments/Webhook => Unable to parse payload for auto-start");
            }

            context.Response.StatusCode = (int)HttpStatusCode.OK;
        }

        private async Task<string> ExecuteRemoteStartAsync(ChargePointStatus chargePointStatus, OCPPCoreContext dbContext, string connectorId, string idTag)
        {
            if (chargePointStatus.Protocol == Protocol_OCPP21)
            {
                return await ExecuteRequestStartTransaction21(chargePointStatus, dbContext, connectorId, idTag);
            }
            else if (chargePointStatus.Protocol == Protocol_OCPP201)
            {
                return await ExecuteRequestStartTransaction20(chargePointStatus, dbContext, connectorId, idTag);
            }
            else
            {
                return await ExecuteRemoteStartTransaction16(chargePointStatus, dbContext, connectorId, idTag);
            }
        }

        private async Task<string> ExecuteRemoteStopAsync(ChargePointStatus chargePointStatus, OCPPCoreContext dbContext, Transaction transaction)
        {
            if (chargePointStatus == null || transaction == null || transaction.StopTime.HasValue)
            {
                return "{\"status\":\"NoOpenTransaction\"}";
            }

            string connectorId = transaction.ConnectorId.ToString();
            if (chargePointStatus.Protocol == Protocol_OCPP21)
            {
                string transactionUid = !string.IsNullOrWhiteSpace(transaction.Uid) ? transaction.Uid : transaction.TransactionId.ToString();
                return await ExecuteRequestStopTransaction21(chargePointStatus, dbContext, connectorId, transactionUid);
            }
            else if (chargePointStatus.Protocol == Protocol_OCPP201)
            {
                string transactionUid = !string.IsNullOrWhiteSpace(transaction.Uid) ? transaction.Uid : transaction.TransactionId.ToString();
                return await ExecuteRequestStopTransaction20(chargePointStatus, dbContext, connectorId, transactionUid);
            }
            else
            {
                return await ExecuteRemoteStopTransaction16(chargePointStatus, dbContext, connectorId, transaction.TransactionId);
            }
        }

        private static string ExtractStatusFromApiResult(string apiResult)
        {
            if (string.IsNullOrWhiteSpace(apiResult)) return null;
            try
            {
                var payload = JsonConvert.DeserializeObject<Dictionary<string, object>>(apiResult);
                if (payload != null && payload.TryGetValue("status", out var statusValue))
                {
                    return statusValue?.ToString();
                }
            }
            catch
            {
                // ignore parse errors
            }
            return null;
        }

        private static string ResolveRemoteStartIdTag(ChargePaymentReservation reservation, string protocol)
        {
            if (reservation == null) return null;
            if (string.Equals(protocol, Protocol_OCPP16, StringComparison.OrdinalIgnoreCase))
            {
                return reservation.OcppIdTag;
            }
            return reservation.OcppIdTag ?? reservation.ChargeTagId;
        }

        private static async Task<string> ReadRequestBodyAsync(HttpContext context)
        {
            context.Request.EnableBuffering();
            context.Request.Body.Position = 0;
            using (var reader = new StreamReader(context.Request.Body, leaveOpen: true))
            {
                string body = await reader.ReadToEndAsync();
                context.Request.Body.Position = 0;
                return body;
            }
        }

        private int GetChargerReservationId(ChargePaymentReservation reservation)
        {
            if (reservation == null) return 0;
            // Derive a stable positive int from the Guid for OCPP 1.6 reservation id expectations.
            var bytes = reservation.ReservationId.ToByteArray();
            int value = BitConverter.ToInt32(bytes, 0);
            return Math.Abs(value == int.MinValue ? int.MaxValue : value);
        }

        private bool SupportsReservationProfile(ChargePointStatus status)
        {
            if (status == null) return false;

            if (status.SupportsReservationProfile.HasValue)
            {
                return status.SupportsReservationProfile.Value;
            }

            var proto = status.Protocol?.ToLowerInvariant();
            if (proto == "ocpp1.6" || proto == "ocpp201" || proto == "ocpp2.1")
            {
                return true;
            }

            return false;
        }

        // Reservation profile fully disabled; ReserveNow/CancelReservation helpers removed intentionally.

        private static void EnsureChargeTagExists(OCPPCoreContext dbContext, string tagId)
        {
            if (dbContext == null || string.IsNullOrWhiteSpace(tagId)) return;

            var existing = dbContext.ChargeTags.Find(tagId);
            if (existing != null) return;

            var newTag = new ChargeTag
            {
                TagId = tagId,
                TagName = $"Web session {tagId}",
                Blocked = false
            };

            dbContext.ChargeTags.Add(newTag);
            dbContext.SaveChanges();
        }

        private static bool HasFreeTagAccess(OCPPCoreContext dbContext, ChargePoint chargePoint, string chargeTagId)
        {
            if (dbContext == null || string.IsNullOrWhiteSpace(chargeTagId)) return false;

            var tag = dbContext.ChargeTags.Find(chargeTagId);
            if (tag == null || (tag.Blocked ?? false)) return false;

            var privileges = dbContext.ChargeTagPrivileges
                .Where(p => p.FreeChargingEnabled && p.TagId == tag.TagId);

            if (chargePoint != null &&
                privileges.Any(p => p.ChargePointId == chargePoint.ChargePointId))
            {
                return true;
            }

            return privileges.Any(p => p.ChargePointId == null);
        }

        public void NotifyTransactionStarted(OCPPCoreContext dbContext, ChargePointStatus chargePointStatus, int connectorId, string chargeTagId, int transactionId)
        {
            if (chargePointStatus == null) return;
            _idleAutoStopTransactions.TryRemove(transactionId, out _);
            _maxEnergyAutoStopTransactions.TryRemove(transactionId, out _);
            _paymentCoordinator?.MarkTransactionStarted(dbContext, chargePointStatus.Id, connectorId, chargeTagId, transactionId);
            LinkReservationToTransaction(dbContext, chargePointStatus.Id, connectorId, chargeTagId, transactionId, _utcNow());
            ApplyTransactionMaxEnergySnapshot(dbContext, transactionId);
        }

        public void LinkReservationToTransaction(OCPPCoreContext dbContext, string chargePointId, int connectorId, string idTag, int transactionId, DateTime startTime)
        {
            _reservationLinkService?.LinkReservation(dbContext, chargePointId, connectorId, idTag, transactionId, startTime);
        }

        public void NotifyTransactionCompleted(OCPPCoreContext dbContext, Transaction transaction)
        {
            _paymentCoordinator?.CompleteReservation(dbContext, transaction);
            if (transaction != null)
            {
                _idleAutoStopTransactions.TryRemove(transaction.TransactionId, out _);
                _maxEnergyAutoStopTransactions.TryRemove(transaction.TransactionId, out _);
            }
        }

        /// <summary>
        /// Dumps an OCPP message in the dump dir
        /// </summary>
        private void DumpMessage(string nameSuffix, string message)
        {
            string dumpDir = _configuration.GetValue<string>("MessageDumpDir");
            if (!string.IsNullOrWhiteSpace(dumpDir))
            {
                var fullDumpDir = Path.GetFullPath(dumpDir);
                Directory.CreateDirectory(fullDumpDir);
                string path = Path.Combine(fullDumpDir, string.Format("{0}_{1}.txt", DateTime.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss-ffff"), nameSuffix));
                try
                {
                    // Write incoming message into dump directory
                    File.WriteAllText(path, message);
                }
                catch (Exception exp)
                {
                    _logger.LogError(exp, "OCPPMiddleware.DumpMessage => Error dumping message '{0}' to path: '{1}'", nameSuffix, path);
                }
            }
        }
    }

    public static class OCPPMiddlewareExtensions
    {
        public static IApplicationBuilder UseOCPPMiddleware(this IApplicationBuilder builder)
        {
            return builder.UseMiddleware<OCPPMiddleware>();
        }
    }
}
