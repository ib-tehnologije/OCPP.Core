using System;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using OCPP.Core.Server.Messages_OCPP16;

namespace OCPP.Core.Server
{
    public partial class ControllerOCPP16
    {
        public void HandleChangeAvailability(OCPPMessage msgIn, OCPPMessage msgOut)
        {
            Logger.LogInformation("ChangeAvailability answer: ChargePointId={0} / MsgType={1} / ErrCode={2}", ChargePointStatus.Id, msgIn.MessageType, msgIn.ErrorCode);

            try
            {
                ChangeAvailabilityResponse response = DeserializeMessage<ChangeAvailabilityResponse>(msgIn);
                Logger.LogInformation("HandleChangeAvailability => Answer status: {0}", response?.Status);
                WriteMessageLog(ChargePointStatus?.Id, null, msgOut.Action, response?.Status.ToString(), msgIn.ErrorCode);

                if (msgOut.TaskCompletionSource != null)
                {
                    // Set API response as TaskCompletion-result
                    string apiResult = "{\"status\": " + JsonConvert.ToString(response.Status.ToString()) + "}";
                    Logger.LogTrace("HandleChangeAvailability => API response: {0}", apiResult);

                    msgOut.TaskCompletionSource.SetResult(apiResult);
                }
            }
            catch (Exception exp)
            {
                Logger.LogError(exp, "HandleChangeAvailability => Exception: {0}", exp.Message);
            }
        }
    }
}
