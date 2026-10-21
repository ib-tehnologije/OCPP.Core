using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using OCPP.Core.Database;
using OCPP.Core.Management;
using OCPP.Core.Management.Controllers;
using OCPP.Core.Management.Models;
using OCPP.Core.Management.Services;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    public class ManagementControllerBehaviorTests
    {
        [Fact]
        public void ManageChargePoint_BuildsLiveConnectorCardsFromTelemetryTransactionsAndReservations()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"manage-live-{Guid.NewGuid():N}.sqlite");

            try
            {
                var now = new DateTime(2026, 3, 9, 12, 0, 0, DateTimeKind.Utc);

                using (var setupContext = CreateContext(databasePath))
                {
                    setupContext.ChargePoints.Add(new ChargePoint
                    {
                        ChargePointId = "CP-LIVE",
                        Name = "Telemetry test"
                    });
                    setupContext.ConnectorStatuses.AddRange(
                        new ConnectorStatus
                        {
                            ChargePointId = "CP-LIVE",
                            ConnectorId = 1,
                            ConnectorName = "Main cable",
                            LastStatus = "Available",
                            LastStatusTime = now.AddMinutes(-2)
                        },
                        new ConnectorStatus
                        {
                            ChargePointId = "CP-LIVE",
                            ConnectorId = 2,
                            LastStatus = "Available",
                            LastStatusTime = now.AddMinutes(-1)
                        });
                    setupContext.Transactions.Add(new Transaction
                    {
                        TransactionId = 77,
                        ChargePointId = "CP-LIVE",
                        ConnectorId = 1,
                        StartTagId = "TAG-77",
                        StartTime = now.AddMinutes(-15),
                        MeterStart = 12.5
                    });
                    setupContext.ChargePaymentReservations.AddRange(
                        new ChargePaymentReservation
                        {
                            ReservationId = Guid.NewGuid(),
                            ChargePointId = "CP-LIVE",
                            ConnectorId = 1,
                            ChargeTagId = "WEB-IN-USE",
                            Status = ChargePaymentReservationState.Charging,
                            Currency = "eur",
                            CreatedAtUtc = now.AddMinutes(-15),
                            UpdatedAtUtc = now.AddMinutes(-5)
                        },
                        new ChargePaymentReservation
                        {
                            ReservationId = Guid.NewGuid(),
                            ChargePointId = "CP-LIVE",
                            ConnectorId = 2,
                            ChargeTagId = "WEB-CANCEL",
                            Status = ChargePaymentReservationState.Pending,
                            Currency = "eur",
                            CreatedAtUtc = now.AddMinutes(-3),
                            UpdatedAtUtc = now.AddMinutes(-1)
                        },
                        new ChargePaymentReservation
                        {
                            ReservationId = Guid.NewGuid(),
                            ChargePointId = "CP-LIVE",
                            ConnectorId = 2,
                            ChargeTagId = "WEB-OLD",
                            Status = ChargePaymentReservationState.Completed,
                            Currency = "eur",
                            CreatedAtUtc = now.AddMinutes(-20),
                            UpdatedAtUtc = now.AddMinutes(-10)
                        });
                    setupContext.SaveChanges();
                }

                using var server = TestHttpServer.Start(request =>
                {
                    Assert.Equal("GET", request.Method);
                    Assert.Equal("/Status", request.Path);
                    Assert.Equal("test-management-key", request.Headers["x-api-key"]);

                    return TestHttpResponse.Json(
                        "[{\"id\":\"CP-LIVE\",\"onlineConnectors\":{\"1\":{\"status\":\"Occupied\",\"ocppStatus\":\"SuspendedEV\",\"chargeRateKW\":22.5,\"meterKWH\":18.75,\"currentImportA\":31.2,\"soC\":66},\"2\":{\"status\":\"Available\",\"ocppStatus\":\"Available\",\"chargeRateKW\":0.0,\"meterKWH\":2.2,\"currentImportA\":0.0,\"soC\":null}}}]");
                });

                using var actionContext = CreateContext(databasePath);
                var controller = CreateHomeController(
                    actionContext,
                    new Dictionary<string, string?>
                    {
                        ["ServerApiUrl"] = server.BaseUri.ToString(),
                        ["ApiKey"] = "test-management-key"
                    });

                var result = controller.ManageChargePoint("CP-LIVE");
                var viewResult = Assert.IsType<ViewResult>(result);
                var model = Assert.IsType<ChargePointManageViewModel>(viewResult.Model);

                Assert.Equal("ChargePointManage", viewResult.ViewName);
                Assert.Equal(2, model.LiveConnectors.Count);
                Assert.True(model.OnlineConnectorStatuses.ContainsKey("CP-LIVE"));

                var connector1 = model.LiveConnectors.Single(c => c.ConnectorId == 1);
                Assert.Equal("Main cable", connector1.ConnectorName);
                Assert.Equal("Occupied", connector1.LiveStatus);
                Assert.Equal("SuspendedEV", connector1.LiveOcppStatus);
                Assert.Equal(22.5, connector1.ChargeRateKw);
                Assert.Equal(31.2, connector1.CurrentImportA);
                Assert.Equal(18.75, connector1.MeterKwh);
                Assert.Equal(6.25, connector1.SessionEnergyKwh);
                Assert.Equal(66, connector1.SoC);
                Assert.Equal(77, connector1.ActiveTransactionId);
                Assert.Equal("TAG-77", connector1.ActiveTagId);
                Assert.False(connector1.CanCancelReservation);
                Assert.Equal(ChargePaymentReservationState.Charging, connector1.ActiveReservationStatus);

                var connector2 = model.LiveConnectors.Single(c => c.ConnectorId == 2);
                Assert.Equal("Connector 2", connector2.ConnectorName);
                Assert.Equal("Available", connector2.LiveStatus);
                Assert.Equal("Available", connector2.LiveOcppStatus);
                Assert.Equal(2.2, connector2.MeterKwh);
                Assert.Null(connector2.ActiveTransactionId);
                Assert.NotNull(connector2.ActiveReservationId);
                Assert.Equal(ChargePaymentReservationState.Pending, connector2.ActiveReservationStatus);
                Assert.True(connector2.CanCancelReservation);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Fact]
        public void ManageChargePoint_UsesCaseInsensitiveOnlineStatusLookup()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"manage-live-case-{Guid.NewGuid():N}.sqlite");

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    setupContext.ChargePoints.Add(new ChargePoint
                    {
                        ChargePointId = "ns2571148979",
                        Name = "Huawei test"
                    });
                    setupContext.ConnectorStatuses.Add(new ConnectorStatus
                    {
                        ChargePointId = "ns2571148979",
                        ConnectorId = 1,
                        LastStatus = "Unknown"
                    });
                    setupContext.SaveChanges();
                }

                using var server = TestHttpServer.Start(request =>
                {
                    Assert.Equal("GET", request.Method);
                    Assert.Equal("/Status", request.Path);

                    return TestHttpResponse.Json(
                        "[{\"id\":\"NS2571148979\",\"onlineConnectors\":{\"1\":{\"status\":\"Available\",\"ocppStatus\":\"Available\",\"meterKWH\":6.958}}}]");
                });

                using var actionContext = CreateContext(databasePath);
                var controller = CreateHomeController(
                    actionContext,
                    new Dictionary<string, string?>
                    {
                        ["ServerApiUrl"] = server.BaseUri.ToString(),
                        ["ApiKey"] = "test-management-key"
                    });

                var result = controller.ManageChargePoint("ns2571148979");
                var viewResult = Assert.IsType<ViewResult>(result);
                var model = Assert.IsType<ChargePointManageViewModel>(viewResult.Model);
                var connector = Assert.Single(model.LiveConnectors);

                Assert.True(model.OnlineConnectorStatuses.ContainsKey("ns2571148979"));
                Assert.Equal("Available", connector.LiveStatus);
                Assert.Equal("Available", connector.LiveOcppStatus);
                Assert.Equal(6.958, connector.MeterKwh);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Fact]
        public async Task CancelReservation_ReturnsAppliedMessage_WhenBackendCancelsReservation()
        {
            using var server = TestHttpServer.Start(request =>
            {
                Assert.Equal("POST", request.Method);
                Assert.Equal("/Payments/Cancel", request.Path);
                Assert.Equal("test-api-key", request.Headers["x-api-key"]);
                Assert.Contains("\"reason\":\"Cancelled from management UI\"", request.Body, StringComparison.Ordinal);
                Assert.Contains("\"reservationId\":\"11111111-1111-1111-1111-111111111111\"", request.Body, StringComparison.OrdinalIgnoreCase);

                return TestHttpResponse.Json("{\"cancellationApplied\":true,\"status\":\"Cancelled\"}");
            });

            using var context = CreateContext(Path.Combine(Path.GetTempPath(), $"cancel-reservation-{Guid.NewGuid():N}.sqlite"));
            var controller = CreateApiController(
                context,
                new Dictionary<string, string?>
                {
                    ["ServerApiUrl"] = server.BaseUri.ToString(),
                    ["ApiKey"] = "test-api-key"
                });

            var result = await controller.CancelReservation(Guid.Parse("11111111-1111-1111-1111-111111111111"));
            var objectResult = Assert.IsType<ObjectResult>(result);

            Assert.Equal(StatusCodes.Status200OK, objectResult.StatusCode);
            Assert.Equal("The reservation has been cancelled.", objectResult.Value);
        }

        [Fact]
        public async Task CancelReservation_UsesConventionalRouteId_WhenActionParameterIsEmpty()
        {
            Guid reservationId = Guid.Parse("22222222-2222-2222-2222-222222222222");

            using var server = TestHttpServer.Start(request =>
            {
                Assert.Contains($"\"reservationId\":\"{reservationId}\"", request.Body, StringComparison.OrdinalIgnoreCase);
                return TestHttpResponse.Json("{\"cancellationApplied\":true,\"status\":\"Cancelled\"}");
            });

            using var context = CreateContext(Path.Combine(Path.GetTempPath(), $"cancel-reservation-route-{Guid.NewGuid():N}.sqlite"));
            var controller = CreateApiController(
                context,
                new Dictionary<string, string?>
                {
                    ["ServerApiUrl"] = server.BaseUri.ToString(),
                    ["ApiKey"] = "test-api-key"
                });

            controller.ControllerContext.RouteData = new Microsoft.AspNetCore.Routing.RouteData();
            controller.RouteData.Values["id"] = reservationId.ToString();

            var result = await controller.CancelReservation(Guid.Empty);
            var objectResult = Assert.IsType<ObjectResult>(result);

            Assert.Equal(StatusCodes.Status200OK, objectResult.StatusCode);
            Assert.Equal("The reservation has been cancelled.", objectResult.Value);
        }

        [Fact]
        public async Task CancelReservation_ReturnsStatusMessage_WhenBackendKeepsReservationActive()
        {
            using var server = TestHttpServer.Start(_ =>
                TestHttpResponse.Json("{\"cancellationApplied\":false,\"status\":\"Charging\"}"));

            using var context = CreateContext(Path.Combine(Path.GetTempPath(), $"cancel-reservation-status-{Guid.NewGuid():N}.sqlite"));
            var controller = CreateApiController(
                context,
                new Dictionary<string, string?>
                {
                    ["ServerApiUrl"] = server.BaseUri.ToString()
                });

            var result = await controller.CancelReservation(Guid.NewGuid());
            var objectResult = Assert.IsType<ObjectResult>(result);

            Assert.Equal(StatusCodes.Status200OK, objectResult.StatusCode);
            Assert.Equal("Reservation status is now 'Charging'.", objectResult.Value);
        }

        [Fact]
        public async Task CancelReservation_ReturnsVisibleError_WhenBackendReturnsMalformedBody()
        {
            using var server = TestHttpServer.Start(_ =>
                TestHttpResponse.Html("<html><body>login required</body></html>"));

            using var context = CreateContext(Path.Combine(Path.GetTempPath(), $"cancel-reservation-error-{Guid.NewGuid():N}.sqlite"));
            var controller = CreateApiController(
                context,
                new Dictionary<string, string?>
                {
                    ["ServerApiUrl"] = server.BaseUri.ToString()
                });

            var result = await controller.CancelReservation(Guid.NewGuid());
            var objectResult = Assert.IsType<ObjectResult>(result);

            Assert.Equal(StatusCodes.Status200OK, objectResult.StatusCode);
            Assert.Equal("The reservation could not be cancelled.", objectResult.Value);
        }

        [Theory]
        [InlineData("Accepted", "The charging station is being restarted.")]
        [InlineData("Rejected", "The charging station has rejected the request.")]
        [InlineData("Scheduled", "The charging station has scheduled the restart.")]
        [InlineData("Timeout", "A timeout has occured")]
        public async Task Reset_RequestsExplicitHardMode(string backendStatus, string expectedResult)
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"reset-hard-{Guid.NewGuid():N}.sqlite");

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    setupContext.ChargePoints.Add(new ChargePoint
                    {
                        ChargePointId = "CP-RESET",
                        Name = "Reset test"
                    });
                    setupContext.SaveChanges();
                }

                using var server = TestHttpServer.Start(request =>
                {
                    Assert.Equal("GET", request.Method);
                    Assert.Equal("/Reset/CP-RESET/Hard", request.Path);
                    Assert.Equal("test-api-key", request.Headers["x-api-key"]);

                    return TestHttpResponse.Json($"{{\"status\":\"{backendStatus}\"}}");
                });

                using var actionContext = CreateContext(databasePath);
                var controller = CreateApiController(
                    actionContext,
                    new Dictionary<string, string?>
                    {
                        ["ServerApiUrl"] = server.BaseUri.ToString(),
                        ["ApiKey"] = "test-api-key"
                    });

                var result = await controller.Reset("CP-RESET");
                var objectResult = Assert.IsType<ObjectResult>(result);

                Assert.Equal(StatusCodes.Status200OK, objectResult.StatusCode);
                Assert.Equal(expectedResult, objectResult.Value);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Fact]
        public async Task StopTransaction_ReturnsVisibleError_WhenBackendReturnsHtmlLoginPage()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"stop-transaction-ui-{Guid.NewGuid():N}.sqlite");

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    setupContext.ChargePoints.Add(new ChargePoint
                    {
                        ChargePointId = "CP-STOP",
                        Name = "Stop transaction test"
                    });
                    setupContext.SaveChanges();
                }

                using var server = TestHttpServer.Start(request =>
                {
                    Assert.Equal("GET", request.Method);
                    Assert.Equal("/StopTransaction/CP-STOP/1", request.Path);
                    return TestHttpResponse.Html("<html><body>sign in</body></html>");
                });

                using var actionContext = CreateContext(databasePath);
                var controller = CreateApiController(
                    actionContext,
                    new Dictionary<string, string?>
                    {
                        ["ServerApiUrl"] = server.BaseUri.ToString()
                    });

                var result = await controller.StopTransaction("CP-STOP", 1);
                var objectResult = Assert.IsType<ObjectResult>(result);

                Assert.Equal(StatusCodes.Status200OK, objectResult.StatusCode);
                Assert.Equal("The charging transaction could not be stopped.", objectResult.Value);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Fact]
        public async Task Index_BuildsOverviewRowsWithRawOcppStatus()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"overview-live-{Guid.NewGuid():N}.sqlite");

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    setupContext.ChargePoints.Add(new ChargePoint
                    {
                        ChargePointId = "CP-INDEX",
                        Name = "Overview test"
                    });
                    setupContext.ConnectorStatuses.Add(new ConnectorStatus
                    {
                        ChargePointId = "CP-INDEX",
                        ConnectorId = 1,
                        LastStatus = "SuspendedEV",
                        LastStatusTime = DateTime.UtcNow.AddMinutes(-2)
                    });
                    setupContext.Transactions.Add(new Transaction
                    {
                        TransactionId = 18,
                        ChargePointId = "CP-INDEX",
                        ConnectorId = 1,
                        StartTagId = "TAG-INDEX",
                        StartTime = DateTime.UtcNow.AddMinutes(-20),
                        MeterStart = 10.0
                    });
                    setupContext.SaveChanges();
                }

                using var server = TestHttpServer.Start(request =>
                {
                    Assert.Equal("GET", request.Method);
                    Assert.Equal("/Status", request.Path);

                    return TestHttpResponse.Json(
                        "[{\"id\":\"CP-INDEX\",\"onlineConnectors\":{\"1\":{\"status\":\"Occupied\",\"ocppStatus\":\"SuspendedEV\",\"meterKWH\":14.25,\"chargeRateKW\":0.0,\"currentImportA\":0.0,\"soC\":77}}}]");
                });

                using var actionContext = CreateContext(databasePath);
                var controller = CreateHomeController(
                    actionContext,
                    new Dictionary<string, string?>
                    {
                        ["ServerApiUrl"] = server.BaseUri.ToString()
                    });

                var result = await controller.Index();
                var viewResult = Assert.IsType<ViewResult>(result);
                var model = Assert.IsType<OverviewViewModel>(viewResult.Model);
                var connector = Assert.Single(model.ChargePoints);

                Assert.True(model.ServerConnection);
                Assert.Equal("CP-INDEX", connector.ChargePointId);
                Assert.Equal(OCPP.Core.Management.Models.ConnectorStatusEnum.Occupied, connector.ConnectorStatus);
                Assert.Equal("SuspendedEV", connector.OcppStatus);
                Assert.Contains("4.3kWh", connector.CurrentChargeData, StringComparison.Ordinal);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Fact]
        public async Task PaymentsStop_ProxiesSuccessfulJsonPayload()
        {
            Guid reservationId = Guid.Parse("33333333-3333-3333-3333-333333333333");

            using var server = TestHttpServer.Start(request =>
            {
                Assert.Equal("POST", request.Method);
                Assert.Equal("/Payments/Stop", request.Path);
                Assert.Equal("test-payments-key", request.Headers["x-api-key"]);
                Assert.Contains($"\"reservationId\":\"{reservationId}\"", request.Body, StringComparison.OrdinalIgnoreCase);

                return TestHttpResponse.Json("{\"status\":\"Accepted\",\"reservationId\":\"33333333-3333-3333-3333-333333333333\"}");
            });

            using var context = CreateContext(Path.Combine(Path.GetTempPath(), $"payments-stop-success-{Guid.NewGuid():N}.sqlite"));
            var controller = CreatePaymentsController(
                context,
                new Dictionary<string, string?>
                {
                    ["ServerApiUrl"] = server.BaseUri.ToString(),
                    ["ApiKey"] = "test-payments-key"
                });

            var result = await controller.Stop(new PaymentsController.StopPayload
            {
                ReservationId = reservationId
            });
            var contentResult = Assert.IsType<ContentResult>(result);

            Assert.Equal("application/json", contentResult.ContentType);
            Assert.Equal("{\"status\":\"Accepted\",\"reservationId\":\"33333333-3333-3333-3333-333333333333\"}", contentResult.Content);
        }

        [Fact]
        public async Task PaymentsStop_ReturnsBadGatewayJson_WhenBackendReturnsMalformedBody()
        {
            using var server = TestHttpServer.Start(_ =>
                TestHttpResponse.Html("<html><body>sign in</body></html>"));

            using var context = CreateContext(Path.Combine(Path.GetTempPath(), $"payments-stop-malformed-{Guid.NewGuid():N}.sqlite"));
            var controller = CreatePaymentsController(
                context,
                new Dictionary<string, string?>
                {
                    ["ServerApiUrl"] = server.BaseUri.ToString()
                });

            var result = await controller.Stop(new PaymentsController.StopPayload
            {
                ReservationId = Guid.NewGuid()
            });
            var contentResult = Assert.IsType<ContentResult>(result);

            Assert.Equal(StatusCodes.Status502BadGateway, controller.Response.StatusCode);
            Assert.Equal("application/json", contentResult.ContentType);
            Assert.Contains("\"status\":\"Error\"", contentResult.Content, StringComparison.Ordinal);
        }

        [Fact]
        public async Task PaymentsRequestR1Invoice_ForwardsConfirmedForeignBuyerPayload()
        {
            Guid reservationId = Guid.Parse("44444444-4444-4444-4444-444444444444");
            using var server = TestHttpServer.Start(request =>
            {
                Assert.Equal("POST", request.Method);
                Assert.Equal("/Payments/RequestR1", request.Path);
                Assert.Contains("\"buyerCountry\":\"CZ\"", request.Body, StringComparison.Ordinal);
                Assert.Contains("\"buyerStreet\":\"Pražská 1\"", request.Body, StringComparison.Ordinal);
                Assert.Contains("\"buyerTaxIdentifier\":\"CZ 123-ABC\"", request.Body, StringComparison.Ordinal);
                Assert.Contains("\"buyerDataVersion\":\"2026-08-28T08:15:00Z\"", request.Body, StringComparison.Ordinal);
                Assert.Contains("\"buyerDataConfirmed\":true", request.Body, StringComparison.OrdinalIgnoreCase);
                return TestHttpResponse.Json("{\"status\":\"UpdatedMetadataPending\",\"buyerCountry\":\"CZ\",\"buyerTaxIdentifier\":\"CZ 123-ABC\"}");
            });
            using var context = CreateContext(Path.Combine(Path.GetTempPath(), $"payments-r1-foreign-{Guid.NewGuid():N}.sqlite"));
            var controller = CreatePaymentsController(context, new Dictionary<string, string?>
            {
                ["ServerApiUrl"] = server.BaseUri.ToString()
            });

            var result = await controller.RequestR1Invoice(new PaymentsController.R1InvoicePayload
            {
                ReservationId = reservationId,
                BuyerDataVersion = new DateTime(2026, 8, 28, 8, 15, 0, DateTimeKind.Utc),
                BuyerCountry = "CZ",
                BuyerCompanyName = "Example s.r.o.",
                BuyerStreet = "Pražská 1",
                BuyerPostalCode = "110 00",
                BuyerCity = "Praha",
                BuyerEmail = "billing@example.cz",
                BuyerTaxIdentifier = "CZ 123-ABC",
                BuyerRegistrationNumber = "C 12345",
                BuyerIdentifierIsVatRegistration = false,
                BuyerDataConfirmed = true
            });

            var json = Assert.IsType<JsonResult>(result);
            Assert.NotNull(json.Value);
            Assert.Contains(
                "metadata synchronization is pending",
                Newtonsoft.Json.JsonConvert.SerializeObject(json.Value),
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(StatusCodes.Status200OK, controller.Response.StatusCode);
        }

        [Fact]
        public async Task PaymentsSuccess_WithoutSessionId_PostsConfirmWithNullCheckoutSessionId_AndRedirectsPublicOriginToStatus()
        {
            Guid reservationId = Guid.Parse("55555555-5555-5555-5555-555555555555");
            TestHttpRequest? captured = null;
            using var server = TestHttpServer.Start(request =>
            {
                captured = request;
                return TestHttpResponse.Json("{\"status\":\"Accepted\",\"reservationId\":\"55555555-5555-5555-5555-555555555555\"}");
            });

            using var context = CreateContext(Path.Combine(Path.GetTempPath(), $"payments-success-no-session-{Guid.NewGuid():N}.sqlite"));
            var controller = CreatePaymentsController(context, new Dictionary<string, string?>
            {
                ["ServerApiUrl"] = server.BaseUri.ToString(),
                ["ApiKey"] = "test-payments-key"
            });

            var result = await controller.Success(reservationId, null, "public");

            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal(nameof(PaymentsController.Status), redirect.ActionName);
            Assert.NotNull(redirect.RouteValues);
            Assert.Equal(reservationId, redirect.RouteValues!["reservationId"]);
            Assert.Equal("public", redirect.RouteValues["origin"]);

            Assert.True(captured.HasValue);
            Assert.Equal("POST", captured!.Value.Method);
            Assert.Equal("/Payments/Confirm", captured.Value.Path);
            Assert.Equal("test-payments-key", captured.Value.Headers["x-api-key"]);
            var body = Newtonsoft.Json.Linq.JObject.Parse(captured.Value.Body);
            Assert.Equal(reservationId, Guid.Parse(body.Value<string>("ReservationId")!));
            Assert.True(body.TryGetValue("CheckoutSessionId", out var checkoutSessionToken));
            Assert.Equal(Newtonsoft.Json.Linq.JTokenType.Null, checkoutSessionToken!.Type);
            Assert.Contains(controller.Response.Headers.SetCookie.ToArray(), value =>
                value != null && value.StartsWith("OCPP.PublicPaymentRecovery=", StringComparison.Ordinal));
        }

        [Fact]
        public async Task PaymentsSuccess_WithoutSessionId_ShowsConfirmFailureStatus()
        {
            Guid reservationId = Guid.Parse("66666666-6666-6666-6666-666666666666");
            using var server = TestHttpServer.Start(_ =>
                TestHttpResponse.Json(
                    "{\"status\":\"Failed\",\"error\":\"The payment was not authorized.\"}",
                    StatusCodes.Status400BadRequest));

            using var context = CreateContext(Path.Combine(Path.GetTempPath(), $"payments-success-no-session-failed-{Guid.NewGuid():N}.sqlite"));
            var controller = CreatePaymentsController(context, new Dictionary<string, string?>
            {
                ["ServerApiUrl"] = server.BaseUri.ToString()
            });

            var result = await controller.Success(reservationId, "", "public");

            // Single-return-URL providers send declines here too, so public payers get the public page.
            var view = Assert.IsType<ViewResult>(result);
            Assert.Equal("PublicResult", view.ViewName);
            var model = Assert.IsType<PaymentResultViewModel>(view.Model);
            Assert.Equal("Failed", model.Status);
            Assert.Equal("The payment was not authorized.", model.Message);
            Assert.Equal(reservationId, model.ReservationId);
            Assert.Equal("public", model.Origin);
            Assert.False(model.Success);
        }

        [Fact]
        public async Task PaymentsSuccess_ConfirmFailureWithoutPublicOrigin_ShowsOperatorResult()
        {
            Guid reservationId = Guid.Parse("68686868-6868-6868-6868-686868686868");
            using var server = TestHttpServer.Start(_ =>
                TestHttpResponse.Json(
                    "{\"status\":\"Cancelled\",\"error\":\"Checkout was cancelled.\"}",
                    StatusCodes.Status400BadRequest));

            using var context = CreateContext(Path.Combine(Path.GetTempPath(), $"payments-success-operator-failed-{Guid.NewGuid():N}.sqlite"));
            var controller = CreatePaymentsController(context, new Dictionary<string, string?>
            {
                ["ServerApiUrl"] = server.BaseUri.ToString()
            });

            var result = await controller.Success(reservationId, null, null);

            var view = Assert.IsType<ViewResult>(result);
            Assert.Equal("Result", view.ViewName);
            var model = Assert.IsType<PaymentResultViewModel>(view.Model);
            Assert.Equal("Cancelled", model.Status);
            Assert.Equal(reservationId, model.ReservationId);
            Assert.False(model.Success);
        }

        [Fact]
        public async Task PaymentsSuccess_WithSessionId_PostsConfirmWithCheckoutSessionId()
        {
            Guid reservationId = Guid.Parse("77777777-7777-7777-7777-777777777777");
            TestHttpRequest? captured = null;
            using var server = TestHttpServer.Start(request =>
            {
                captured = request;
                return TestHttpResponse.Json("{\"status\":\"Accepted\"}");
            });

            using var context = CreateContext(Path.Combine(Path.GetTempPath(), $"payments-success-session-{Guid.NewGuid():N}.sqlite"));
            var controller = CreatePaymentsController(context, new Dictionary<string, string?>
            {
                ["ServerApiUrl"] = server.BaseUri.ToString()
            });

            var result = await controller.Success(reservationId, "cs_test_123", null);

            var view = Assert.IsType<ViewResult>(result);
            Assert.Equal("Result", view.ViewName);
            var model = Assert.IsType<PaymentResultViewModel>(view.Model);
            Assert.Equal("Accepted", model.Status);
            Assert.True(model.Success);
            Assert.Equal(reservationId, model.ReservationId);
            Assert.Equal("/Home/Index", model.ReturnUrl);

            Assert.True(captured.HasValue);
            Assert.Equal("/Payments/Confirm", captured!.Value.Path);
            var body = Newtonsoft.Json.Linq.JObject.Parse(captured.Value.Body);
            Assert.Equal(reservationId, Guid.Parse(body.Value<string>("ReservationId")!));
            Assert.Equal("cs_test_123", body.Value<string>("CheckoutSessionId"));
        }

        [Fact]
        public async Task PaymentsSuccess_WithoutReservationId_ShowsMissingDataError()
        {
            using var context = CreateContext(Path.Combine(Path.GetTempPath(), $"payments-success-missing-{Guid.NewGuid():N}.sqlite"));
            var controller = CreatePaymentsController(context, new Dictionary<string, string?>());

            var result = await controller.Success(Guid.Empty, "cs_test_123", "public");

            var view = Assert.IsType<ViewResult>(result);
            Assert.Equal("Result", view.ViewName);
            var model = Assert.IsType<PaymentResultViewModel>(view.Model);
            Assert.Equal("Error", model.Status);
            Assert.False(model.Success);
        }

        [Fact]
        public async Task PaymentsSuccess_ThrottledConfirm_RedirectsPublicPayerToStatusAndKeepsRecoveryCookie()
        {
            Guid reservationId = Guid.Parse("56565656-5656-5656-5656-565656565656");
            using var server = TestHttpServer.Start(_ =>
                TestHttpResponse.Json(
                    "{\"status\":\"Pending\",\"error\":\"Payment is still being processed.\"}",
                    StatusCodes.Status400BadRequest));

            using var context = CreateContext(Path.Combine(Path.GetTempPath(), $"payments-success-pending-{Guid.NewGuid():N}.sqlite"));
            var controller = CreatePaymentsController(context, new Dictionary<string, string?>
            {
                ["ServerApiUrl"] = server.BaseUri.ToString()
            });

            var result = await controller.Success(reservationId, null, "public");

            // The status page polls; the first return is still confirming the payment.
            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal(nameof(PaymentsController.Status), redirect.ActionName);
            Assert.Equal(reservationId, redirect.RouteValues!["reservationId"]);
            Assert.Equal("public", redirect.RouteValues["origin"]);
            Assert.DoesNotContain(controller.Response.Headers.SetCookie.ToArray(), value =>
                value != null && value.StartsWith("OCPP.PublicPaymentRecovery=", StringComparison.Ordinal));
        }

        [Fact]
        public async Task PaymentsSuccess_ThrottledConfirm_ShowsOperatorResultWithServerMessage()
        {
            Guid reservationId = Guid.Parse("57575757-5757-5757-5757-575757575757");
            using var server = TestHttpServer.Start(_ =>
                TestHttpResponse.Json(
                    "{\"status\":\"Pending\",\"error\":\"Payment is still being processed.\"}",
                    StatusCodes.Status400BadRequest));

            using var context = CreateContext(Path.Combine(Path.GetTempPath(), $"payments-success-pending-operator-{Guid.NewGuid():N}.sqlite"));
            var controller = CreatePaymentsController(context, new Dictionary<string, string?>
            {
                ["ServerApiUrl"] = server.BaseUri.ToString()
            });

            var result = await controller.Success(reservationId, null, null);

            var view = Assert.IsType<ViewResult>(result);
            Assert.Equal("Result", view.ViewName);
            var model = Assert.IsType<PaymentResultViewModel>(view.Model);
            Assert.Equal("Pending", model.Status);
            Assert.Equal("Payment is still being processed.", model.Message);
            Assert.Equal(reservationId, model.ReservationId);
            Assert.False(model.Success);
        }

        [Theory]
        [InlineData(ChargePaymentReservationState.Pending, false)]
        [InlineData(ChargePaymentReservationState.Authorized, true)]
        [InlineData(ChargePaymentReservationState.Cancelled, true)]
        public async Task PaymentsStatus_KeepsRecoveryCookieOnlyWhileThePaymentIsPending(string reservationStatus, bool expectCookieCleared)
        {
            Guid reservationId = Guid.Parse("58585858-5858-5858-5858-585858585858");
            TestHttpRequest? captured = null;
            using var server = TestHttpServer.Start(request =>
            {
                captured = request;
                return TestHttpResponse.Json($"{{\"status\":\"{reservationStatus}\",\"reservationId\":\"{reservationId}\"}}");
            });

            using var context = CreateContext(Path.Combine(Path.GetTempPath(), $"payments-status-cookie-{Guid.NewGuid():N}.sqlite"));
            var controller = CreatePaymentsController(context, new Dictionary<string, string?>
            {
                ["ServerApiUrl"] = server.BaseUri.ToString()
            });

            var result = await controller.Status(reservationId, "public");

            Assert.True(captured.HasValue);
            Assert.Equal($"/Payments/Status?reservationId={reservationId}", captured!.Value.Path);
            var view = Assert.IsType<ViewResult>(result);
            Assert.Equal("PublicStatus", view.ViewName);
            var model = Assert.IsType<PaymentStatusViewModel>(view.Model);
            Assert.True(model.ApiSuccess);
            bool cleared = controller.Response.Headers.SetCookie.ToArray().Any(value =>
                value != null && value.StartsWith("OCPP.PublicPaymentRecovery=", StringComparison.Ordinal));
            Assert.Equal(expectCookieCleared, cleared);
        }

        [Theory]
        [InlineData("{\"status\":\"Cancelled\",\"cancellationApplied\":true,\"message\":null}", "public")]
        [InlineData("{\"status\":\"Cancelled\",\"cancellationApplied\":true,\"message\":null}", null)]
        public async Task PaymentsCancel_ReportsCancelledOnlyWhenTheServerAppliedIt(string answer, string? origin)
        {
            Guid reservationId = Guid.Parse("59595959-5959-5959-5959-595959595959");
            TestHttpRequest? captured = null;
            using var server = TestHttpServer.Start(request =>
            {
                captured = request;
                return TestHttpResponse.Json(answer);
            });

            using var context = CreateContext(Path.Combine(Path.GetTempPath(), $"payments-cancel-applied-{Guid.NewGuid():N}.sqlite"));
            var controller = CreatePaymentsController(context, new Dictionary<string, string?>
            {
                ["ServerApiUrl"] = server.BaseUri.ToString()
            });

            var result = await controller.Cancel(reservationId, origin!);

            var view = Assert.IsType<ViewResult>(result);
            Assert.Equal(origin == "public" ? "PublicResult" : "Result", view.ViewName);
            var model = Assert.IsType<PaymentResultViewModel>(view.Model);
            Assert.Equal("Cancelled", model.Status);
            Assert.Equal("PaymentCancelled", model.Message);
            Assert.False(model.Success);

            Assert.True(captured.HasValue);
            Assert.Equal("/Payments/Cancel", captured!.Value.Path);
            var body = Newtonsoft.Json.Linq.JObject.Parse(captured.Value.Body);
            Assert.Equal(reservationId, Guid.Parse(body.Value<string>("ReservationId")!));
            Assert.Equal("checkout_cancelled", body.Value<string>("Reason"));
            Assert.Contains(controller.Response.Headers.SetCookie.ToArray(), value =>
                value != null && value.StartsWith("OCPP.PublicPaymentRecovery=", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData("{\"status\":\"Failed\",\"cancellationApplied\":false,\"message\":\"Payment was captured immediately by the payment method; refund required.\"}", "public", "PublicResult", "Failed", "Payment was captured immediately by the payment method; refund required.")]
        [InlineData("{\"status\":\"Failed\",\"cancellationApplied\":false,\"message\":\"Payment was captured immediately by the payment method; refund required.\"}", null, "Result", "Failed", "Payment was captured immediately by the payment method; refund required.")]
        [InlineData("{\"status\":\"Completed\",\"cancellationApplied\":false,\"message\":null}", "public", "PublicResult", "Completed", "PaymentCancelNotPossible")]
        [InlineData("{\"status\":\"Charging\",\"cancellationApplied\":false,\"message\":null}", null, "Result", "Charging", "PaymentCancelSessionStarted")]
        [InlineData("{\"status\":\"Authorized\",\"cancellationApplied\":false,\"message\":null}", null, "Result", "Authorized", "PaymentAuthorizedMessage")]
        public async Task PaymentsCancel_ShowsTheServerStatusWhenTheCancelWasNotApplied(
            string answer,
            string? origin,
            string expectedView,
            string expectedStatus,
            string expectedMessage)
        {
            Guid reservationId = Guid.Parse("5a5a5a5a-5a5a-5a5a-5a5a-5a5a5a5a5a5a");
            using var server = TestHttpServer.Start(_ => TestHttpResponse.Json(answer));

            using var context = CreateContext(Path.Combine(Path.GetTempPath(), $"payments-cancel-not-applied-{Guid.NewGuid():N}.sqlite"));
            var controller = CreatePaymentsController(context, new Dictionary<string, string?>
            {
                ["ServerApiUrl"] = server.BaseUri.ToString()
            });

            var result = await controller.Cancel(reservationId, origin!);

            var view = Assert.IsType<ViewResult>(result);
            Assert.Equal(expectedView, view.ViewName);
            var model = Assert.IsType<PaymentResultViewModel>(view.Model);
            Assert.Equal(expectedStatus, model.Status);
            Assert.Equal(expectedMessage, model.Message);
            Assert.NotEqual("PaymentCancelled", model.Message);
            Assert.Equal(reservationId, model.ReservationId);
        }

        [Theory]
        [InlineData(ChargePaymentReservationState.Charging)]
        [InlineData(ChargePaymentReservationState.StartRequested)]
        public async Task PaymentsCancel_ActiveSessionSendsPublicPayerToTheStatusPage(string reservationStatus)
        {
            Guid reservationId = Guid.Parse("5b5b5b5b-5b5b-5b5b-5b5b-5b5b5b5b5b5b");
            using var server = TestHttpServer.Start(_ =>
                TestHttpResponse.Json($"{{\"status\":\"{reservationStatus}\",\"cancellationApplied\":false,\"message\":null}}"));

            using var context = CreateContext(Path.Combine(Path.GetTempPath(), $"payments-cancel-active-{Guid.NewGuid():N}.sqlite"));
            var controller = CreatePaymentsController(context, new Dictionary<string, string?>
            {
                ["ServerApiUrl"] = server.BaseUri.ToString()
            });

            var result = await controller.Cancel(reservationId, "public");

            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal(nameof(PaymentsController.Status), redirect.ActionName);
            Assert.Equal(reservationId, redirect.RouteValues!["reservationId"]);
            Assert.Equal("public", redirect.RouteValues["origin"]);
        }

        [Fact]
        public async Task PaymentsCancel_WithoutServerAnswerKeepsRecoveryCookieAndReportsAnError()
        {
            Guid reservationId = Guid.Parse("5c5c5c5c-5c5c-5c5c-5c5c-5c5c5c5c5c5c");
            using var server = TestHttpServer.Start(_ =>
                TestHttpResponse.Html("<html><body>bad gateway</body></html>", StatusCodes.Status502BadGateway));

            using var context = CreateContext(Path.Combine(Path.GetTempPath(), $"payments-cancel-no-answer-{Guid.NewGuid():N}.sqlite"));
            var controller = CreatePaymentsController(context, new Dictionary<string, string?>
            {
                ["ServerApiUrl"] = server.BaseUri.ToString()
            });

            var result = await controller.Cancel(reservationId, "public");

            var view = Assert.IsType<ViewResult>(result);
            Assert.Equal("PublicResult", view.ViewName);
            var model = Assert.IsType<PaymentResultViewModel>(view.Model);
            Assert.Equal("Error", model.Status);
            Assert.Equal("PaymentCancelUnknown", model.Message);
            Assert.False(model.Success);
            Assert.DoesNotContain(controller.Response.Headers.SetCookie.ToArray(), value =>
                value != null && value.StartsWith("OCPP.PublicPaymentRecovery=", StringComparison.Ordinal));
        }

        [Fact]
        public void PaymentsCancel_NewPublicMessagesAreTranslatedInEveryPortalLanguage()
        {
            string script = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "OCPP.Core.Management", "wwwroot", "js", "public-portal.js"));

            foreach (string key in new[] { "message.cancelNotPossible", "message.capturedAtCheckout" })
            {
                var lines = script.Split('\n').Where(line => line.TrimStart().StartsWith($"\"{key}\":", StringComparison.Ordinal)).ToList();
                Assert.Equal(6, lines.Count);
                Assert.All(lines, line => Assert.DoesNotContain(line, ch => ch >= 0x0400 && ch <= 0x04FF));
            }

            Assert.Contains("\"This session can no longer be cancelled.\": \"message.cancelNotPossible\"", script, StringComparison.Ordinal);
            Assert.Contains("\"Payment was captured immediately by the payment method; refund required.\": \"message.capturedAtCheckout\"", script, StringComparison.Ordinal);
        }

        private static string FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "OCPP.Core.sln")))
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);
            return directory!.FullName;
        }

        [Fact]
        public void PaymentsMockCheckout_WithoutSessionId_RendersProviderTitle()
        {
            Guid reservationId = Guid.Parse("88888888-8888-8888-8888-888888888888");
            using var context = CreateContext(Path.Combine(Path.GetTempPath(), $"payments-mock-provider-{Guid.NewGuid():N}.sqlite"));
            var controller = CreatePaymentsController(context, new Dictionary<string, string?>());
            string successUrl = $"http://localhost:8082/Payments/Success?reservationId={reservationId}&origin=public";
            string cancelUrl = $"http://localhost:8082/Payments/Cancel?reservationId={reservationId}&origin=public";

            var result = controller.MockCheckout(reservationId, null, successUrl, cancelUrl, "Saferpay");

            var view = Assert.IsType<ViewResult>(result);
            Assert.Null(view.ViewName);
            var model = Assert.IsType<MockCheckoutViewModel>(view.Model);
            Assert.Equal(reservationId, model.ReservationId);
            Assert.Null(model.SessionId);
            Assert.False(model.HasSessionId);
            Assert.Equal("Saferpay", model.ProviderName);
            Assert.Equal("Mock Saferpay checkout", model.Title);
            Assert.Equal(successUrl, model.SuccessUrl);
            Assert.Equal(cancelUrl, model.CancelUrl);
        }

        [Fact]
        public void PaymentsMockCheckout_WithSessionIdAndNoProvider_UsesNeutralTitle()
        {
            Guid reservationId = Guid.Parse("99999999-9999-9999-9999-999999999999");
            using var context = CreateContext(Path.Combine(Path.GetTempPath(), $"payments-mock-session-{Guid.NewGuid():N}.sqlite"));
            var controller = CreatePaymentsController(context, new Dictionary<string, string?>());

            var result = controller.MockCheckout(reservationId, "cs_mock_1", "http://localhost:8082/Payments/Success", null);

            var view = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<MockCheckoutViewModel>(view.Model);
            Assert.Equal("cs_mock_1", model.SessionId);
            Assert.True(model.HasSessionId);
            Assert.Null(model.ProviderName);
            Assert.Equal("Mock payment checkout", model.Title);
        }

        [Fact]
        public void PaymentsMockCheckout_IgnoresUnsafeProviderName()
        {
            using var context = CreateContext(Path.Combine(Path.GetTempPath(), $"payments-mock-unsafe-{Guid.NewGuid():N}.sqlite"));
            var controller = CreatePaymentsController(context, new Dictionary<string, string?>());

            var result = controller.MockCheckout(Guid.NewGuid(), null, "http://localhost:8082/Payments/Success", null, "<script>alert(1)</script>");

            var model = Assert.IsType<MockCheckoutViewModel>(Assert.IsType<ViewResult>(result).Model);
            Assert.Null(model.ProviderName);
            Assert.Equal("Mock payment checkout", model.Title);
        }

        [Theory]
        [InlineData(false, true)]
        [InlineData(true, false)]
        public void PaymentsMockCheckout_StillRequiresReservationAndSuccessUrl(bool emptyReservation, bool emptySuccessUrl)
        {
            using var context = CreateContext(Path.Combine(Path.GetTempPath(), $"payments-mock-invalid-{Guid.NewGuid():N}.sqlite"));
            var controller = CreatePaymentsController(context, new Dictionary<string, string?>());

            var result = controller.MockCheckout(
                emptyReservation ? Guid.Empty : Guid.NewGuid(),
                "cs_mock_1",
                emptySuccessUrl ? "" : "http://localhost:8082/Payments/Success",
                null,
                "Saferpay");

            var view = Assert.IsType<ViewResult>(result);
            Assert.Equal("Result", view.ViewName);
            var model = Assert.IsType<PaymentResultViewModel>(view.Model);
            Assert.Equal("Error", model.Status);
        }

        [Theory]
        [InlineData("javascript:alert(document.cookie)", null)]
        [InlineData("JavaScript:alert(1)", null)]
        [InlineData(" javascript:alert(1)", null)]
        [InlineData("java\tscript:alert(1)", null)]
        [InlineData("javascript://%0Aalert(1)", null)]
        [InlineData("data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==", null)]
        [InlineData("vbscript:msgbox(1)", null)]
        [InlineData("file:///etc/passwd", null)]
        [InlineData("//attacker.example/Payments/Success", null)]
        [InlineData("/\\attacker.example/Payments/Success", null)]
        [InlineData("https:attacker.example", null)]
        [InlineData("http://localhost:8082/Payments/Success", "javascript:alert(1)")]
        [InlineData("http://localhost:8082/Payments/Success", "data:text/html,<script>alert(1)</script>")]
        public void PaymentsMockCheckout_RejectsUnsafeReturnUrls(string successUrl, string? cancelUrl)
        {
            using var context = CreateContext(Path.Combine(Path.GetTempPath(), $"payments-mock-unsafe-url-{Guid.NewGuid():N}.sqlite"));
            var controller = CreatePaymentsController(context, new Dictionary<string, string?>());

            var result = controller.MockCheckout(Guid.NewGuid(), "cs_mock_1", successUrl, cancelUrl!, "Saferpay");

            var view = Assert.IsType<ViewResult>(result);
            Assert.Equal("Result", view.ViewName);
            Assert.Equal(400, view.StatusCode);
            var model = Assert.IsType<PaymentResultViewModel>(view.Model);
            Assert.Equal("Error", model.Status);
            Assert.False(model.Success);
        }

        [Theory]
        [InlineData("http://localhost:8082/Payments/Success?reservationId=r1&origin=public", "https://portal.example.com/Payments/Cancel?reservationId=r1")]
        [InlineData("HTTPS://portal.example.com/Payments/Success", null)]
        [InlineData("/Payments/Success?reservationId=r1&next=http://x", "/Payments/Cancel?reservationId=r1")]
        [InlineData("Success?reservationId=r1", "?cancel=1")]
        public void PaymentsMockCheckout_AcceptsRelativeAndHttpReturnUrls(string successUrl, string? cancelUrl)
        {
            using var context = CreateContext(Path.Combine(Path.GetTempPath(), $"payments-mock-safe-url-{Guid.NewGuid():N}.sqlite"));
            var controller = CreatePaymentsController(context, new Dictionary<string, string?>());

            var result = controller.MockCheckout(Guid.NewGuid(), null, successUrl, cancelUrl!, "Saferpay");

            var view = Assert.IsType<ViewResult>(result);
            Assert.Null(view.ViewName);
            Assert.Null(view.StatusCode);
            var model = Assert.IsType<MockCheckoutViewModel>(view.Model);
            Assert.Equal(successUrl, model.SuccessUrl);
            Assert.Equal(cancelUrl, model.CancelUrl);
        }

        private static HomeController CreateHomeController(OCPPCoreContext dbContext, IDictionary<string, string?> configValues)
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(configValues)
                .Build();

            var resolver = new PublicPortalSettingsResolver(configuration, dbContext);
            var httpContext = CreateAdminHttpContext();

            return new HomeController(
                null!,
                new DictionaryStringLocalizer<HomeController>(),
                resolver,
                null!,
                NullLoggerFactory.Instance,
                configuration,
                dbContext)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = httpContext
                },
                TempData = new TempDataDictionary(httpContext, new TestTempDataProvider())
            };
        }

        private static ApiController CreateApiController(OCPPCoreContext dbContext, IDictionary<string, string?> configValues)
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(configValues)
                .Build();

            return new ApiController(
                null!,
                new DictionaryStringLocalizer<ApiController>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["CancelReservationApplied"] = "The reservation has been cancelled.",
                    ["CancelReservationStatus"] = "Reservation status is now '{0}'.",
                    ["CancelReservationError"] = "The reservation could not be cancelled.",
                    ["StopTransactionError"] = "The charging transaction could not be stopped.",
                    ["ResetAccepted"] = "The charging station is being restarted.",
                    ["ResetRejected"] = "The charging station has rejected the request.",
                    ["ResetScheduled"] = "The charging station has scheduled the restart.",
                    ["Timeout"] = "A timeout has occured",
                    ["UnknownChargepoint"] = "Unknown charge point"
                }),
                NullLoggerFactory.Instance,
                configuration,
                dbContext)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = CreateAdminHttpContext()
                }
            };
        }

        private static PaymentsController CreatePaymentsController(OCPPCoreContext dbContext, IDictionary<string, string?> configValues)
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(configValues)
                .Build();

            var controllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            };

            return new PaymentsController(
                null!,
                new DictionaryStringLocalizer<PaymentsController>(),
                NullLoggerFactory.Instance,
                configuration,
                dbContext)
            {
                ControllerContext = controllerContext,
                Url = new TestUrlHelper(controllerContext)
            };
        }

        private static HttpContext CreateAdminHttpContext()
        {
            var context = new DefaultHttpContext();
            context.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, "admin"),
                new Claim(ClaimTypes.Role, Constants.AdminRoleName)
            }, "TestAuth"));
            return context;
        }

        private static OCPPCoreContext CreateContext(string databasePath)
        {
            var options = new DbContextOptionsBuilder<OCPPCoreContext>()
                .UseSqlite($"Data Source={databasePath}")
                .Options;

            var context = new OCPPCoreContext(options);
            context.Database.EnsureCreated();
            return context;
        }

        private static void TryDelete(string databasePath)
        {
            try
            {
                if (File.Exists(databasePath))
                {
                    File.Delete(databasePath);
                }
            }
            catch
            {
                // best effort cleanup for temp DBs
            }
        }

        private sealed class DictionaryStringLocalizer<T> : IStringLocalizer<T>
        {
            private readonly IReadOnlyDictionary<string, string> _values;

            public DictionaryStringLocalizer(IReadOnlyDictionary<string, string>? values = null)
            {
                _values = values ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            public LocalizedString this[string name]
            {
                get
                {
                    var value = _values.TryGetValue(name, out var found) ? found : name;
                    return new LocalizedString(name, value);
                }
            }

            public LocalizedString this[string name, params object[] arguments]
            {
                get
                {
                    var template = _values.TryGetValue(name, out var found) ? found : name;
                    return new LocalizedString(name, string.Format(CultureInfo.InvariantCulture, template, arguments));
                }
            }

            public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => Array.Empty<LocalizedString>();

            public IStringLocalizer WithCulture(CultureInfo culture) => this;
        }

        private sealed class TestTempDataProvider : ITempDataProvider
        {
            private Dictionary<string, object> _values = new Dictionary<string, object>();

            public IDictionary<string, object> LoadTempData(HttpContext context) =>
                new Dictionary<string, object>(_values);

            public void SaveTempData(HttpContext context, IDictionary<string, object> values)
            {
                _values = values?.ToDictionary(x => x.Key, x => x.Value) ?? new Dictionary<string, object>();
            }
        }

        private sealed class TestUrlHelper : IUrlHelper
        {
            public TestUrlHelper(ActionContext actionContext)
            {
                ActionContext = actionContext;
            }

            public ActionContext ActionContext { get; }

            public string? Action(UrlActionContext actionContext) =>
                $"/{actionContext.Controller}/{actionContext.Action}";

            public string? Content(string? contentPath) => contentPath;

            public bool IsLocalUrl(string? url) =>
                !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal);

            public string? Link(string? routeName, object? values) => null;

            public string? RouteUrl(UrlRouteContext routeContext) => null;
        }

        private sealed class TestHttpServer : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly CancellationTokenSource _cts = new CancellationTokenSource();
            private readonly Task _serverTask;
            private readonly Func<TestHttpRequest, TestHttpResponse> _responseFactory;

            private TestHttpServer(Func<TestHttpRequest, TestHttpResponse> responseFactory)
            {
                _responseFactory = responseFactory;
                int port = ReservePort();
                BaseUri = new Uri($"http://127.0.0.1:{port}/");
                _listener = new HttpListener();
                _listener.Prefixes.Add(BaseUri.ToString());
                _listener.Start();
                _serverTask = Task.Run(ServeSingleRequestAsync);
            }

            public Uri BaseUri { get; }

            public static TestHttpServer Start(Func<TestHttpRequest, TestHttpResponse> responseFactory) =>
                new TestHttpServer(responseFactory);

            public void Dispose()
            {
                _cts.Cancel();
                if (_listener.IsListening)
                {
                    _listener.Stop();
                }
                _listener.Close();

                try
                {
                    _serverTask.GetAwaiter().GetResult();
                }
                catch
                {
                    // ignore shutdown race from stopped listener
                }

                _cts.Dispose();
            }

            private async Task ServeSingleRequestAsync()
            {
                try
                {
                    HttpListenerContext context = await _listener.GetContextAsync();
                    var headers = context.Request.Headers.AllKeys
                        .Where(key => !string.IsNullOrWhiteSpace(key))
                        .ToDictionary(
                            key => key!,
                            key => context.Request.Headers[key!] ?? string.Empty,
                            StringComparer.OrdinalIgnoreCase);

                    string body;
                    using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8))
                    {
                        body = await reader.ReadToEndAsync();
                    }

                    var response = _responseFactory(new TestHttpRequest(
                        context.Request.HttpMethod,
                        context.Request.RawUrl ?? "/",
                        headers,
                        body));

                    byte[] bodyBytes = Encoding.UTF8.GetBytes(response.Body);
                    context.Response.StatusCode = response.StatusCode;
                    context.Response.ContentType = $"{response.ContentType}; charset=utf-8";
                    context.Response.ContentLength64 = bodyBytes.LongLength;
                    await context.Response.OutputStream.WriteAsync(bodyBytes, 0, bodyBytes.Length, _cts.Token);
                    await context.Response.OutputStream.FlushAsync(_cts.Token);
                    context.Response.Close();
                }
                catch (OperationCanceledException)
                {
                    // normal shutdown
                }
                catch (ObjectDisposedException)
                {
                    // normal shutdown
                }
            }

            private static int ReservePort()
            {
                var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();
                return port;
            }
        }

        private readonly record struct TestHttpRequest(
            string Method,
            string Path,
            IReadOnlyDictionary<string, string> Headers,
            string Body);

        private readonly record struct TestHttpResponse(int StatusCode, string ContentType, string Body)
        {
            public static TestHttpResponse Json(string body, int statusCode = StatusCodes.Status200OK) =>
                new TestHttpResponse(statusCode, "application/json", body);

            public static TestHttpResponse Html(string body, int statusCode = StatusCodes.Status200OK) =>
                new TestHttpResponse(statusCode, "text/html", body);
        }
    }
}
