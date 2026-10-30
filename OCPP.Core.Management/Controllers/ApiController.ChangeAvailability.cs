using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using OCPP.Core.Database;

namespace OCPP.Core.Management.Controllers
{
    public partial class ApiController
    {
        [Authorize]
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public async Task<IActionResult> ChangeAvailability(string Id, int connectorId, string param)
        {
            if (User != null && !User.IsInRole(Constants.AdminRoleName))
            {
                Logger.LogWarning("ChangeAvailability: Request by non-administrator: {0}", User?.Identity?.Name);
                return StatusCode((int)HttpStatusCode.Unauthorized);
            }

            int httpStatuscode = (int)HttpStatusCode.OK;
            string resultContent = string.Empty;

            Logger.LogTrace("ChangeAvailability: Request for chargepoint '{0}' / connector {1} / type '{2}'", Id, connectorId, param);
            if (!string.IsNullOrEmpty(Id) && !string.IsNullOrEmpty(param))
            {
                try
                {
                    ChargePoint chargePoint = DbContext.ChargePoints.Find(Id);
                    if (chargePoint != null)
                    {
                        string serverApiUrl = base.Config.GetValue<string>("ServerApiUrl");
                        string apiKeyConfig = base.Config.GetValue<string>("ApiKey");
                        if (!string.IsNullOrEmpty(serverApiUrl))
                        {
                            try
                            {
                                using (var httpClient = new HttpClient())
                                {
                                    if (!serverApiUrl.EndsWith('/'))
                                    {
                                        serverApiUrl += "/";
                                    }
                                    Uri uri = new Uri(serverApiUrl);
                                    uri = new Uri(uri, $"ChangeAvailability/{Uri.EscapeDataString(Id)}/{connectorId}/{Uri.EscapeDataString(param)}");
                                    httpClient.Timeout = new TimeSpan(0, 0, 15); // use short timeout

                                    // API-Key authentication?
                                    if (!string.IsNullOrWhiteSpace(apiKeyConfig))
                                    {
                                        httpClient.DefaultRequestHeaders.Add("X-API-Key", apiKeyConfig);
                                    }
                                    else
                                    {
                                        Logger.LogWarning("ChangeAvailability: No API-Key configured!");
                                    }

                                    HttpResponseMessage response = await httpClient.GetAsync(uri);
                                    if (response.StatusCode == HttpStatusCode.OK)
                                    {
                                        string jsonResult = await response.Content.ReadAsStringAsync();
                                        Logger.LogInformation("ChangeAvailability: Result of API request is '{0}'", jsonResult);
                                        dynamic jsonObject = JsonConvert.DeserializeObject(jsonResult);
                                        string status = jsonObject.status;
                                        switch (status)
                                        {
                                            case "Accepted":
                                                resultContent = _localizer["ChangeAvailabilityAccepted"];
                                                break;
                                            case "Scheduled":
                                                resultContent = _localizer["ChangeAvailabilityScheduled"];
                                                break;
                                            case "Rejected":
                                                resultContent = _localizer["ChangeAvailabilityRejected"];
                                                break;
                                            case "Timeout":
                                                resultContent = _localizer["Timeout"];
                                                break;
                                            default:
                                                resultContent = string.Format(_localizer["UnknownStatus"], status);
                                                break;
                                        }
                                    }
                                    else if (response.StatusCode == HttpStatusCode.NotFound)
                                    {
                                        // Chargepoint offline
                                        httpStatuscode = (int)HttpStatusCode.OK;
                                        resultContent = _localizer["ChargerOffline"];
                                    }
                                    else
                                    {
                                        Logger.LogError("ChangeAvailability: Result of API  request => httpStatus={0}", response.StatusCode);
                                        httpStatuscode = (int)HttpStatusCode.OK;
                                        resultContent = _localizer["ChangeAvailabilityError"];
                                    }
                                }
                            }
                            catch (Exception exp)
                            {
                                Logger.LogError(exp, "ChangeAvailability: Error in API request => {0}", exp.Message);
                                httpStatuscode = (int)HttpStatusCode.OK;
                                resultContent = _localizer["ChangeAvailabilityError"];
                            }
                        }
                    }
                    else
                    {
                        Logger.LogWarning("ChangeAvailability: Error loading charge point '{0}' from database", Id);
                        httpStatuscode = (int)HttpStatusCode.OK;
                        resultContent = _localizer["UnknownChargepoint"];
                    }
                }
                catch (Exception exp)
                {
                    Logger.LogError(exp, "ChangeAvailability: Error loading charge point from database");
                    httpStatuscode = (int)HttpStatusCode.OK;
                    resultContent = _localizer["ChangeAvailabilityError"];
                }
            }
            else
            {
                httpStatuscode = (int)HttpStatusCode.BadRequest;
            }

            return StatusCode(httpStatuscode, resultContent);
        }
    }
}
