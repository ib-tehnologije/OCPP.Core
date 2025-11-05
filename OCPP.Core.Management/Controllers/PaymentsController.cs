using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Localization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OCPP.Core.Database;
using OCPP.Core.Management.Models;

namespace OCPP.Core.Management.Controllers
{
    [AllowAnonymous]
    public class PaymentsController : BaseController
    {
        private readonly IStringLocalizer<PaymentsController> _localizer;

        public PaymentsController(
            IUserManager userManager,
            IStringLocalizer<PaymentsController> localizer,
            ILoggerFactory loggerFactory,
            IConfiguration config,
            OCPPCoreContext dbContext) : base(userManager, loggerFactory, config, dbContext)
        {
            _localizer = localizer;
            Logger = loggerFactory.CreateLogger<PaymentsController>();
        }

        public async Task<IActionResult> Success(Guid reservationId, string session_id)
        {
            if (reservationId == Guid.Empty || string.IsNullOrWhiteSpace(session_id))
            {
                return View("Result", BuildResult("Error", L("PaymentMissingData", "Missing payment session information.")));
            }

            var apiResult = await PostAsync("Payments/Confirm", new ConfirmPayload
            {
                ReservationId = reservationId,
                CheckoutSessionId = session_id
            });

            if (!apiResult.Success)
            {
                var errorMessage = !string.IsNullOrWhiteSpace(apiResult.ErrorMessage)
                    ? apiResult.ErrorMessage
                    : L("StartTransactionError", "Unable to start the transaction.");
                return View("Result", BuildResult("Error", errorMessage));
            }

            string status = ExtractStatus(apiResult.Payload);
            string message = MapStatusToMessage(status, apiResult.Payload);

            var model = BuildResult(status ?? "Error", message);
            model.ReservationId = reservationId;

            return View("Result", model);
        }

        public async Task<IActionResult> Cancel(Guid reservationId)
        {
            if (reservationId != Guid.Empty)
            {
                await PostAsync("Payments/Cancel", new CancelPayload
                {
                    ReservationId = reservationId,
                    Reason = "checkout_cancelled"
                });
            }

            return View("Result", BuildResult("Cancelled", L("PaymentCancelled", "Payment cancelled.")));
        }

        private PaymentResultViewModel BuildResult(string status, string message)
        {
            return new PaymentResultViewModel
            {
                Status = status,
                Message = message,
                Success = string.Equals(status, "Accepted", StringComparison.OrdinalIgnoreCase)
            };
        }

        private async Task<(bool Success, string Payload, string ErrorMessage)> PostAsync(string relativePath, object payload)
        {
            string serverApiUrl = Config.GetValue<string>("ServerApiUrl");
            string apiKeyConfig = Config.GetValue<string>("ApiKey");

            if (string.IsNullOrWhiteSpace(serverApiUrl))
            {
                return (false, null, L("ServerNotConfigured", "Server API URL is not configured."));
            }

            if (!serverApiUrl.EndsWith('/'))
            {
                serverApiUrl += "/";
            }

            var targetUri = new Uri(new Uri(serverApiUrl), relativePath);

            try
            {
                using var httpClient = new HttpClient();
                httpClient.Timeout = TimeSpan.FromSeconds(30);

                if (!string.IsNullOrWhiteSpace(apiKeyConfig))
                {
                    httpClient.DefaultRequestHeaders.Add("X-API-Key", apiKeyConfig);
                }

                string jsonPayload = JsonConvert.SerializeObject(payload);
                using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                HttpResponseMessage response = await httpClient.PostAsync(targetUri, content);
                string responsePayload = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    return (true, responsePayload, null);
                }
                else
                {
                    Logger.LogWarning("PaymentsController => POST {Path} failed: {StatusCode} / {Payload}", relativePath, response.StatusCode, responsePayload);
                    return (false, responsePayload, response.StatusCode.ToString());
                }
            }
            catch (Exception exp)
            {
                Logger.LogError(exp, "PaymentsController => POST {Path} failed: {Message}", relativePath, exp.Message);
                return (false, null, exp.Message);
            }
        }

        private static string ExtractStatus(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload)) return null;
            try
            {
                var json = JsonConvert.DeserializeObject<Dictionary<string, object>>(payload);
                if (json != null && json.TryGetValue("status", out var statusValue))
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

        private string MapStatusToMessage(string status, string payload)
        {
            switch (status)
            {
                case "Accepted":
                    return L("PaymentAuthorizedMessage", "Payment authorized. Charging session will start shortly.");
                case "Rejected":
                    return L("StartTransactionRejected", "Start request rejected by the charger.");
                case "Timeout":
                    return L("Timeout", "No response received from the charger in time.");
                case "ChargerOffline":
                    return L("ChargerOffline", "The charger is currently offline.");
                case "Cancelled":
                    return L("PaymentCancelled", "Payment cancelled.");
                default:
                    string errorMessage = ExtractMessage(payload);
                    if (!string.IsNullOrWhiteSpace(errorMessage))
                    {
                        return errorMessage;
                    }
                    return L("StartTransactionError", "An error occurred while starting the transaction.");
            }
        }

        private string ExtractMessage(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                return null;
            }

            try
            {
                var json = JObject.Parse(payload);
                var message = json["message"]?.Value<string>();
                if (!string.IsNullOrWhiteSpace(message))
                {
                    return message;
                }
            }
            catch
            {
                // ignore parse errors
            }

            return null;
        }

        private string L(string key, string fallback)
        {
            var value = _localizer[key];
            return value.ResourceNotFound ? fallback : value.Value;
        }

        private class ConfirmPayload
        {
            public Guid ReservationId { get; set; }
            public string CheckoutSessionId { get; set; }
        }

        private class CancelPayload
        {
            public Guid ReservationId { get; set; }
            public string Reason { get; set; }
        }
    }
}
