using System;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using OCPP.Core.Server.Messages_OCPP20;

namespace OCPP.Core.Server
{
    public partial class ControllerOCPP20
    {
        private string HandleReserveNow(OCPPMessage msgIn, OCPPMessage msgOut)
        {
            try
            {
                var response = DeserializeMessage<ReserveNowResponse>(msgIn);
                Logger.LogInformation("HandleReserveNow(20) => Status={Status}", response?.Status);
                if (msgOut?.TaskCompletionSource != null)
                {
                    string apiResult = "{\"status\": " + JsonConvert.ToString(response?.Status.ToString()) + "}";
                    msgOut.TaskCompletionSource.SetResult(apiResult);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "HandleReserveNow(20) => Exception");
            }

            return null;
        }

        private string HandleCancelReservation(OCPPMessage msgIn, OCPPMessage msgOut)
        {
            try
            {
                var response = DeserializeMessage<CancelReservationResponse>(msgIn);
                Logger.LogInformation("HandleCancelReservation(20) => Status={Status}", response?.Status);
                if (msgOut?.TaskCompletionSource != null)
                {
                    string apiResult = "{\"status\": " + JsonConvert.ToString(response?.Status.ToString()) + "}";
                    msgOut.TaskCompletionSource.SetResult(apiResult);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "HandleCancelReservation(20) => Exception");
            }

            return null;
        }
    }
}
