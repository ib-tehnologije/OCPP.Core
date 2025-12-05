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
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using OCPP.Core.Server.Messages_OCPP16;

namespace OCPP.Core.Server
{
    public partial class ControllerOCPP16
    {
        public void HandleGetConfiguration(OCPPMessage msgIn, OCPPMessage msgOut)
        {
            Logger.LogInformation("GetConfiguration answer: ChargePointId={0} / MsgType={1} / ErrCode={2}", ChargePointStatus.Id, msgIn.MessageType, msgIn.ErrorCode);

            try
            {
                GetConfigurationResponse response = DeserializeMessage<GetConfigurationResponse>(msgIn);
                WriteMessageLog(ChargePointStatus?.Id, null, msgOut.Action, JsonConvert.SerializeObject(response?.ConfigurationKey), msgIn.ErrorCode);

                if (msgOut.TaskCompletionSource != null)
                {
                    string apiResult = JsonConvert.SerializeObject(response);
                    msgOut.TaskCompletionSource.SetResult(apiResult);
                }
            }
            catch (Exception exp)
            {
                Logger.LogError(exp, "HandleGetConfiguration => Exception: {0}", exp.Message);
            }
        }

        public void HandleChangeConfiguration(OCPPMessage msgIn, OCPPMessage msgOut)
        {
            Logger.LogInformation("ChangeConfiguration answer: ChargePointId={0} / MsgType={1} / ErrCode={2}", ChargePointStatus.Id, msgIn.MessageType, msgIn.ErrorCode);

            try
            {
                ChangeConfigurationResponse response = DeserializeMessage<ChangeConfigurationResponse>(msgIn);
                WriteMessageLog(ChargePointStatus?.Id, null, msgOut.Action, response?.Status.ToString(), msgIn.ErrorCode);

                if (msgOut.TaskCompletionSource != null)
                {
                    string apiResult = "{\"status\": " + JsonConvert.ToString(response.Status.ToString()) + "}";
                    msgOut.TaskCompletionSource.SetResult(apiResult);
                }
            }
            catch (Exception exp)
            {
                Logger.LogError(exp, "HandleChangeConfiguration => Exception: {0}", exp.Message);
            }
        }
    }
}

