using System;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using OCPP.Core.Server.Messages_OCPP16;

namespace OCPP.Core.Server
{
    public partial class ControllerOCPP16
    {
        public void HandleReserveNow(OCPPMessage msgIn, OCPPMessage msgOut)
        {
            try
            {
                var response = DeserializeMessage<ReserveNowResponse>(msgIn);
                Logger.LogInformation("HandleReserveNow => ReservationId={ReservationId} Status={Status}", msgOut?.JsonPayload, response?.Status);

                if (msgOut?.TaskCompletionSource != null)
                {
                    string apiResult = "{\"status\": " + JsonConvert.ToString(response?.Status.ToString()) + "}";
                    msgOut.TaskCompletionSource.SetResult(apiResult);
                }
            }
            catch (Exception exp)
            {
                Logger.LogError(exp, "HandleReserveNow => Exception: {Message}", exp.Message);
            }
        }

        public void HandleCancelReservation(OCPPMessage msgIn, OCPPMessage msgOut)
        {
            try
            {
                var response = DeserializeMessage<CancelReservationResponse>(msgIn);
                Logger.LogInformation("HandleCancelReservation => Status={Status}", response?.Status);

                if (msgOut?.TaskCompletionSource != null)
                {
                    string apiResult = "{\"status\": " + JsonConvert.ToString(response?.Status.ToString()) + "}";
                    msgOut.TaskCompletionSource.SetResult(apiResult);
                }
            }
            catch (Exception exp)
            {
                Logger.LogError(exp, "HandleCancelReservation => Exception: {Message}", exp.Message);
            }
        }
    }
}
