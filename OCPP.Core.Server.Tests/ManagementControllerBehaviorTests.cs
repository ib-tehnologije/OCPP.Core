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
                        "[{\"id\":\"CP-LIVE\",\"onlineConnectors\":{\"1\":{\"status\":\"Occupied\",\"chargeRateKW\":22.5,\"meterKWH\":18.75,\"currentImportA\":31.2,\"soC\":66},\"2\":{\"status\":\"Available\",\"chargeRateKW\":0.0,\"meterKWH\":2.2,\"currentImportA\":0.0,\"soC\":null}}}]");
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
