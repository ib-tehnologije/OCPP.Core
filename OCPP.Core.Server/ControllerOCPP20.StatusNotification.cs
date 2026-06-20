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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using OCPP.Core.Database;
using OCPP.Core.Server.Messages_OCPP20;

namespace OCPP.Core.Server
{
    public partial class ControllerOCPP20
    {
        public string HandleStatusNotification(OCPPMessage msgIn, OCPPMessage msgOut, OCPPMiddleware ocppMiddleware)
        {
            string errorCode = null;
            StatusNotificationResponse statusNotificationResponse = new StatusNotificationResponse();

            statusNotificationResponse.CustomData = new CustomDataType();
            statusNotificationResponse.CustomData.VendorId = VendorId;

            int connectorId = 0;

            try
            {
                Logger.LogTrace("Processing status notification...");
                StatusNotificationRequest statusNotificationRequest = DeserializeMessage<StatusNotificationRequest>(msgIn);
                Logger.LogTrace("StatusNotification => Message deserialized");

                connectorId = statusNotificationRequest.ConnectorId;

                // Write raw status in DB
                WriteMessageLog(ChargePointStatus.Id, connectorId, msgIn.Action, string.Format("Status={0}", statusNotificationRequest.ConnectorStatus), string.Empty);

                string rawStatus = statusNotificationRequest.ConnectorStatus.ToString();
                ConnectorStatusEnum newStatus = OcppConnectorStatus.ToConnectorStatusEnum(rawStatus);
                Logger.LogInformation("StatusNotification => ChargePoint={0} / Connector={1} / newStatus={2}", ChargePointStatus?.Id, connectorId, newStatus.ToString());

                if (connectorId > 0)
                {
                    if (UpdateConnectorStatus(connectorId, rawStatus, statusNotificationRequest.Timestamp, null, null) == false)
                    {
                        errorCode = ErrorCodes.InternalError;
                    }

                    ApplyConnectorStatusTransition(connectorId, rawStatus, statusNotificationRequest.Timestamp);
                    ocppMiddleware?.NotifyConnectorOcppStatus(DbContext, ChargePointStatus, connectorId, rawStatus, statusNotificationRequest.Timestamp);

                    if (newStatus == ConnectorStatusEnum.Preparing)
                    {
                        ocppMiddleware?.NotifyConnectorPreparing(ChargePointStatus.Id, connectorId);
                    }
                }
                else
                {
                    Logger.LogWarning("StatusNotification => Status for unexpected ConnectorId={1} on ChargePoint={0}", ChargePointStatus?.Id, connectorId);
                }

                msgOut.JsonPayload = JsonConvert.SerializeObject(statusNotificationResponse);
                Logger.LogTrace("StatusNotification => Response serialized");
            }
            catch (Exception exp)
            {
                Logger.LogError(exp, "StatusNotification => ChargePoint={0} / Exception: {1}", ChargePointStatus.Id, exp.Message);
                errorCode = ErrorCodes.InternalError;
            }

            return errorCode;
        }
    }
}
