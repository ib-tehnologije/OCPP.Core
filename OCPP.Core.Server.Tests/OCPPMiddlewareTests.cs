using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using OCPP.Core.Database;
using OCPP.Core.Server.Payments;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    public class OCPPMiddlewareTests
    {
        [Fact]
        public async Task TryStartChargingAsync_PreservesCompletedReservation_WhenRemoteStartCompletesLate()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-middleware-{Guid.NewGuid():N}.sqlite");
            Guid reservationId = Guid.NewGuid();

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    setupContext.ChargePaymentReservations.Add(new ChargePaymentReservation
                    {
                        ReservationId = reservationId,
                        ChargePointId = "CP-RACE",
                        ConnectorId = 1,
                        ChargeTagId = "PAY-RACE",
                        OcppIdTag = "PAYRACE16",
                        Currency = "eur",
                        Status = PaymentReservationStatus.Authorized,
                        CreatedAtUtc = DateTime.UtcNow,
                        UpdatedAtUtc = DateTime.UtcNow
                    });
                    setupContext.SaveChanges();
                }

                var middleware = CreateMiddleware();
                var fakeSocket = new FakeOpenWebSocket(async () =>
                {
                    using (var completionContext = CreateContext(databasePath))
                    {
                        var completedReservation = completionContext.ChargePaymentReservations.Single(r => r.ReservationId == reservationId);
                        completedReservation.Status = PaymentReservationStatus.Completed;
                        completedReservation.TransactionId = 987;
                        completedReservation.CapturedAmountCents = 50;
                        completedReservation.UpdatedAtUtc = DateTime.UtcNow;
                        completionContext.SaveChanges();
                    }

                    var queuedMessage = GetQueuedRequest(middleware);
                    Assert.NotNull(queuedMessage?.TaskCompletionSource);
                    queuedMessage.TaskCompletionSource.SetResult("{\"status\":\"Accepted\"}");
                    await Task.CompletedTask;
                });
                var previousStatuses = ReplaceChargePointStatuses(new Dictionary<string, ChargePointStatus>
                {
                    ["CP-RACE"] = new ChargePointStatus
                    {
                        Id = "CP-RACE",
                        Protocol = "ocpp1.6",
                        WebSocket = fakeSocket,
                        OnlineConnectors = new Dictionary<int, OnlineConnectorStatus>
                        {
                            [1] = new OnlineConnectorStatus
                            {
                                Status = ConnectorStatusEnum.Preparing,
                                MeterValueDate = DateTimeOffset.UtcNow
                            }
                        }
                    }
                });

                try
                {
                    using var actionContext = CreateContext(databasePath);
                    var trackedReservation = actionContext.ChargePaymentReservations.Single(r => r.ReservationId == reservationId);

                    var startTask = InvokeTryStartChargingAsync(middleware, actionContext, trackedReservation, "Test");
                    var result = await startTask;
                    Assert.Equal(PaymentReservationStatus.Completed, result.Status);
                    Assert.Equal("AlreadyAdvanced", result.Reason);

                    using var verificationContext = CreateContext(databasePath);
                    var persisted = verificationContext.ChargePaymentReservations.Single(r => r.ReservationId == reservationId);

                    Assert.Equal(PaymentReservationStatus.Completed, persisted.Status);
                    Assert.Equal(987, persisted.TransactionId);
                    Assert.Equal(50, persisted.CapturedAmountCents);
                    Assert.Equal("Accepted", persisted.RemoteStartResult);
                    Assert.NotNull(persisted.RemoteStartSentAtUtc);
                }
                finally
                {
                    ReplaceChargePointStatuses(previousStatuses);
                }
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Fact]
        public async Task HandlePaymentStatusAsync_IncludesLatestInvoiceSummary()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-status-{Guid.NewGuid():N}.sqlite");
            Guid reservationId = Guid.NewGuid();

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    setupContext.ChargePaymentReservations.Add(new ChargePaymentReservation
                    {
                        ReservationId = reservationId,
                        ChargePointId = "CP-STATUS",
                        ConnectorId = 1,
                        ChargeTagId = "TAG-STATUS",
                        Currency = "eur",
                        Status = PaymentReservationStatus.Completed,
                        MaxAmountCents = 1000,
                        CapturedAmountCents = 450,
                        CreatedAtUtc = DateTime.UtcNow.AddMinutes(-15),
                        UpdatedAtUtc = DateTime.UtcNow
                    });
                    setupContext.InvoiceSubmissionLogs.Add(new InvoiceSubmissionLog
                    {
                        ReservationId = reservationId,
                        Provider = "ERacuni",
                        Mode = "Submit",
                        Status = "Submitted",
                        InvoiceKind = "R1",
                        ProviderOperation = "SalesInvoiceCreate",
                        ExternalDocumentId = "doc-77",
                        ExternalInvoiceNumber = "INV-2026-0077",
                        ExternalPdfUrl = "https://example.test/invoices/77.pdf",
                        CreatedAtUtc = DateTime.UtcNow.AddMinutes(-2),
                        CompletedAtUtc = DateTime.UtcNow.AddMinutes(-1)
                    });
                    setupContext.SaveChanges();
                }

                var middleware = CreateMiddleware();
                var httpContext = new DefaultHttpContext();
                httpContext.Request.Method = "GET";
                httpContext.Request.QueryString = new QueryString($"?reservationId={reservationId}");
                httpContext.Response.Body = new MemoryStream();

                using (var actionContext = CreateContext(databasePath))
                {
                    await InvokeHandlePaymentStatusAsync(middleware, httpContext, actionContext);
                }

                httpContext.Response.Body.Position = 0;
                using var reader = new StreamReader(httpContext.Response.Body);
                var json = await reader.ReadToEndAsync();
                var payload = JObject.Parse(json);

                Assert.Equal("Completed", payload["status"]?.Value<string>());
                Assert.Equal("Submitted", payload["invoice"]?["status"]?.Value<string>());
                Assert.Equal("INV-2026-0077", payload["invoice"]?["externalInvoiceNumber"]?.Value<string>());
                Assert.Equal("https://example.test/invoices/77.pdf", payload["invoice"]?["invoiceUrl"]?.Value<string>());
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Fact]
        public async Task HandlePaymentStatusAsync_ReportsReservationLockMetadata()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-status-lock-{Guid.NewGuid():N}.sqlite");
            Guid currentReservationId = Guid.NewGuid();

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    setupContext.ChargePaymentReservations.Add(new ChargePaymentReservation
                    {
                        ReservationId = currentReservationId,
                        ChargePointId = "CP-LOCK",
                        ConnectorId = 1,
                        ChargeTagId = "TAG-CURRENT",
                        Currency = "eur",
                        Status = PaymentReservationStatus.Completed,
                        CreatedAtUtc = DateTime.UtcNow.AddMinutes(-20),
                        UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-5)
                    });
                    setupContext.ChargePaymentReservations.Add(new ChargePaymentReservation
                    {
                        ReservationId = Guid.NewGuid(),
                        ChargePointId = "CP-LOCK",
                        ConnectorId = 1,
                        ChargeTagId = "TAG-PENDING",
                        Currency = "eur",
                        Status = PaymentReservationStatus.Pending,
                        CreatedAtUtc = DateTime.UtcNow.AddMinutes(-2),
                        UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-2)
                    });
                    setupContext.SaveChanges();
                }

                var middleware = CreateMiddleware();
                var httpContext = new DefaultHttpContext();
                httpContext.Request.Method = "GET";
                httpContext.Request.QueryString = new QueryString($"?reservationId={currentReservationId}");
                httpContext.Response.Body = new MemoryStream();

                using (var actionContext = CreateContext(databasePath))
                {
                    await InvokeHandlePaymentStatusAsync(middleware, httpContext, actionContext);
                }

                httpContext.Response.Body.Position = 0;
                using var reader = new StreamReader(httpContext.Response.Body);
                var json = await reader.ReadToEndAsync();
                var payload = JObject.Parse(json);

                Assert.Equal("Completed", payload["status"]?.Value<string>());
                Assert.False(payload["locksConnector"]?.Value<bool>() ?? true);
                Assert.True(payload["otherActiveReservation"]?.Value<bool>() ?? false);
                Assert.True(payload["activeReservation"]?.Value<bool>() ?? false);
                Assert.Equal("ActiveReservation", payload["blockingReason"]?.Value<string>());
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Fact]
        public async Task Invoke_StatusApi_IncludesRawOcppStatusFields()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-status-api-{Guid.NewGuid():N}.sqlite");
            DateTime now = DateTime.UtcNow;

            try
            {
                var previousStatuses = ReplaceChargePointStatuses(new Dictionary<string, ChargePointStatus>
                {
                    ["CP-STATUS-API"] = new ChargePointStatus
                    {
                        Id = "CP-STATUS-API",
                        Protocol = "ocpp1.6",
                        WebSocket = new FakeOpenWebSocket(() => Task.CompletedTask),
                        OnlineConnectors = new Dictionary<int, OnlineConnectorStatus>
                        {
                            [1] = new OnlineConnectorStatus
                            {
                                Status = ConnectorStatusEnum.Occupied,
                                OcppStatus = "SuspendedEV",
                                OcppStatusAtUtc = now,
                                MeterKWH = 14.2,
                                MeterValueDate = DateTimeOffset.UtcNow
                            }
                        }
                    }
                });

                try
                {
                    var middleware = CreateMiddleware();
                    var httpContext = new DefaultHttpContext();
                    httpContext.Request.Method = "GET";
                    httpContext.Request.Path = "/API/Status";
                    httpContext.Response.Body = new MemoryStream();

                    using (var actionContext = CreateContext(databasePath))
                    {
                        await middleware.Invoke(httpContext, actionContext);
                    }

                    httpContext.Response.Body.Position = 0;
                    using var reader = new StreamReader(httpContext.Response.Body);
                    var payload = JArray.Parse(await reader.ReadToEndAsync());
                    var connector = payload[0]?["OnlineConnectors"]?["1"];

                    Assert.Equal((int)ConnectorStatusEnum.Occupied, connector?["Status"]?.Value<int>());
                    Assert.Equal("SuspendedEV", connector?["OcppStatus"]?.Value<string>());
                    Assert.Equal(now, connector?["OcppStatusAtUtc"]?.Value<DateTime>().ToUniversalTime());
                }
                finally
                {
                    ReplaceChargePointStatuses(previousStatuses);
                }
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Fact]
        public async Task HandlePaymentCancelAsync_ReturnsActualStatus_WhenReservationCannotBeCancelled()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-cancel-{Guid.NewGuid():N}.sqlite");
            Guid reservationId = Guid.NewGuid();

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    setupContext.ChargePaymentReservations.Add(new ChargePaymentReservation
                    {
                        ReservationId = reservationId,
                        ChargePointId = "CP-CANCEL",
                        ConnectorId = 1,
                        ChargeTagId = "TAG-CANCEL",
                        Currency = "eur",
                        Status = PaymentReservationStatus.Completed,
                        CapturedAmountCents = 50,
                        CreatedAtUtc = DateTime.UtcNow.AddMinutes(-10),
                        UpdatedAtUtc = DateTime.UtcNow
                    });
                    setupContext.SaveChanges();
                }

                var middleware = CreateMiddleware();
                var httpContext = new DefaultHttpContext();
                httpContext.Request.Method = "POST";
                httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(
                    $"{{\"reservationId\":\"{reservationId}\",\"reason\":\"late_cancel\"}}"));
                httpContext.Response.Body = new MemoryStream();

                using (var actionContext = CreateContext(databasePath))
                {
                    await InvokeHandlePaymentCancelAsync(middleware, httpContext, actionContext);
                }

                httpContext.Response.Body.Position = 0;
                using var reader = new StreamReader(httpContext.Response.Body);
                var json = await reader.ReadToEndAsync();
                var payload = JObject.Parse(json);

                Assert.Equal("Completed", payload["status"]?.Value<string>());
                Assert.False(payload["cancellationApplied"]?.Value<bool>() ?? true);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Fact]
        public async Task HandlePaymentStatusAsync_UsesFallbackTransactionAndReportsLiveIdleFields()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-status-live-{Guid.NewGuid():N}.sqlite");
            Guid reservationId = Guid.NewGuid();
            DateTime now = DateTime.UtcNow;

            try
            {
                int transactionId;
                using (var setupContext = CreateContext(databasePath))
                {
                    setupContext.ChargePoints.Add(new ChargePoint
                    {
                        ChargePointId = "CP-LIVE",
                        Name = "CP-LIVE"
                    });

                    var transaction = new Transaction
                    {
                        ChargePointId = "CP-LIVE",
                        ConnectorId = 1,
                        StartTagId = "TAG-LIVE",
                        StartTime = now.AddMinutes(-20),
                        MeterStart = 10.0,
                        MeterStop = 12.0,
                        ChargingEndedAtUtc = now.AddMinutes(-6),
                        IdleUsageFeeMinutes = 2,
                        IdleUsageFeeAmount = 1.0m
                    };
                    setupContext.Transactions.Add(transaction);
                    setupContext.SaveChanges();
                    transactionId = transaction.TransactionId;

                    setupContext.ChargePaymentReservations.Add(new ChargePaymentReservation
                    {
                        ReservationId = reservationId,
                        ChargePointId = "CP-LIVE",
                        ConnectorId = 1,
                        ChargeTagId = "TAG-LIVE",
                        OcppIdTag = "TAG-LIVE",
                        Currency = "eur",
                        Status = PaymentReservationStatus.Charging,
                        UsageFeeAnchorMinutes = 1,
                        UsageFeePerMinute = 0.50m,
                        StartUsageFeeAfterMinutes = 0,
                        MaxUsageFeeMinutes = 120,
                        CreatedAtUtc = now.AddMinutes(-25),
                        UpdatedAtUtc = now
                    });
                    setupContext.SaveChanges();
                }

                var previousStatuses = ReplaceChargePointStatuses(new Dictionary<string, ChargePointStatus>
                {
                    ["CP-LIVE"] = new ChargePointStatus
                    {
                        Id = "CP-LIVE",
                        Protocol = "ocpp1.6",
                        WebSocket = new FakeOpenWebSocket(() => Task.CompletedTask),
                        OnlineConnectors = new Dictionary<int, OnlineConnectorStatus>
                        {
                            [1] = new OnlineConnectorStatus
                            {
                                Status = ConnectorStatusEnum.Occupied,
                                OcppStatus = "SuspendedEV",
                                OcppStatusAtUtc = now,
                                MeterKWH = 14.2,
                                MeterValueDate = DateTimeOffset.UtcNow
                            }
                        }
                    }
                });

                try
                {
                    var middleware = CreateMiddleware();
                    var httpContext = new DefaultHttpContext();
                    httpContext.Request.Method = "GET";
                    httpContext.Request.QueryString = new QueryString($"?reservationId={reservationId}");
                    httpContext.Response.Body = new MemoryStream();

                    using (var actionContext = CreateContext(databasePath))
                    {
                        await InvokeHandlePaymentStatusAsync(middleware, httpContext, actionContext);
                    }

                    httpContext.Response.Body.Position = 0;
                    using var reader = new StreamReader(httpContext.Response.Body);
                    var payload = JObject.Parse(await reader.ReadToEndAsync());

                    Assert.Equal("SuspendedEV", payload["liveOcppStatus"]?.Value<string>());
                    Assert.Equal(transactionId, payload["transactionId"]?.Value<int>());
                    Assert.Equal(10.0, payload["transactionMeterStart"]?.Value<double>());
                    Assert.True(payload["liveIdleFeeMinutes"]?.Value<int>() >= 2);
                    Assert.True(payload["liveIdleFeeAmount"]?.Value<decimal>() >= 1.0m);
                    Assert.NotNull(payload["suspendedEvSinceUtc"]?.Value<string>());
                    Assert.NotNull(payload["idleFeeStartsAtUtc"]?.Value<string>());
                }
                finally
                {
                    ReplaceChargePointStatuses(previousStatuses);
                }
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Fact]
        public async Task HandlePaymentStopAsync_CancelsAuthorizedReservationWithoutTransaction()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-stop-cancel-{Guid.NewGuid():N}.sqlite");
            Guid reservationId = Guid.NewGuid();

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    setupContext.ChargePaymentReservations.Add(new ChargePaymentReservation
                    {
                        ReservationId = reservationId,
                        ChargePointId = "CP-STOP",
                        ConnectorId = 1,
                        ChargeTagId = "TAG-STOP",
                        Currency = "eur",
                        Status = PaymentReservationStatus.Authorized,
                        CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5),
                        UpdatedAtUtc = DateTime.UtcNow
                    });
                    setupContext.SaveChanges();
                }

                var middleware = CreateMiddleware(new CancellingPaymentCoordinator());
                var httpContext = new DefaultHttpContext();
                httpContext.Request.Method = "POST";
                httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes($"{{\"reservationId\":\"{reservationId}\"}}"));
                httpContext.Response.Body = new MemoryStream();

                using (var actionContext = CreateContext(databasePath))
                {
                    await InvokeHandlePaymentStopAsync(middleware, httpContext, actionContext);
                }

                httpContext.Response.Body.Position = 0;
                using var reader = new StreamReader(httpContext.Response.Body);
                var payload = JObject.Parse(await reader.ReadToEndAsync());

                Assert.Equal("Cancelled", payload["status"]?.Value<string>());

                using var verificationContext = CreateContext(databasePath);
                Assert.Equal(PaymentReservationStatus.Cancelled, verificationContext.ChargePaymentReservations.Single(r => r.ReservationId == reservationId).Status);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Fact]
        public async Task HandlePaymentStopAsync_DelegatesToRemoteStopForActiveTransaction()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-stop-remote-{Guid.NewGuid():N}.sqlite");
            Guid reservationId = Guid.NewGuid();

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    setupContext.ChargePoints.Add(new ChargePoint
                    {
                        ChargePointId = "CP-REMOTE",
                        Name = "CP-REMOTE"
                    });

                    var transaction = new Transaction
                    {
                        ChargePointId = "CP-REMOTE",
                        ConnectorId = 1,
                        StartTagId = "TAG-REMOTE",
                        StartTime = DateTime.UtcNow.AddMinutes(-20),
                        MeterStart = 1.0
                    };

                    setupContext.Transactions.Add(transaction);
                    setupContext.SaveChanges();
                    setupContext.ChargePaymentReservations.Add(new ChargePaymentReservation
                    {
                        ReservationId = reservationId,
                        ChargePointId = "CP-REMOTE",
                        ConnectorId = 1,
                        ChargeTagId = "TAG-REMOTE",
                        OcppIdTag = "TAG-REMOTE",
                        Currency = "eur",
                        Status = PaymentReservationStatus.Charging,
                        TransactionId = transaction.TransactionId,
                        CreatedAtUtc = DateTime.UtcNow.AddMinutes(-25),
                        UpdatedAtUtc = DateTime.UtcNow
                    });
                    setupContext.SaveChanges();
                }

                var middleware = CreateMiddleware();
                var fakeSocket = new FakeOpenWebSocket(async () =>
                {
                    var queuedMessage = GetQueuedRequest(middleware);
                    Assert.NotNull(queuedMessage?.TaskCompletionSource);
                    queuedMessage.TaskCompletionSource.SetResult("{\"status\":\"Accepted\"}");
                    await Task.CompletedTask;
                });

                var previousStatuses = ReplaceChargePointStatuses(new Dictionary<string, ChargePointStatus>
                {
                    ["CP-REMOTE"] = new ChargePointStatus
                    {
                        Id = "CP-REMOTE",
                        Protocol = "ocpp1.6",
                        WebSocket = fakeSocket,
                        OnlineConnectors = new Dictionary<int, OnlineConnectorStatus>
                        {
                            [1] = new OnlineConnectorStatus
                            {
                                Status = ConnectorStatusEnum.Occupied,
                                OcppStatus = "Charging"
                            }
                        }
                    }
                });

                try
                {
                    var httpContext = new DefaultHttpContext();
                    httpContext.Request.Method = "POST";
                    httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes($"{{\"reservationId\":\"{reservationId}\"}}"));
                    httpContext.Response.Body = new MemoryStream();

                    using (var actionContext = CreateContext(databasePath))
                    {
                        await InvokeHandlePaymentStopAsync(middleware, httpContext, actionContext);
                    }

                    httpContext.Response.Body.Position = 0;
                    using var reader = new StreamReader(httpContext.Response.Body);
                    var payload = JObject.Parse(await reader.ReadToEndAsync());
                    Assert.Equal("Accepted", payload["status"]?.Value<string>());
                }
                finally
                {
                    ReplaceChargePointStatuses(previousStatuses);
                }
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Fact]
        public async Task HandlePaymentStopAsync_ReturnsNoOpenTransaction_WhenReservationHasNoActiveTransaction()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-stop-no-open-{Guid.NewGuid():N}.sqlite");
            Guid reservationId = Guid.NewGuid();

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    setupContext.ChargePaymentReservations.Add(new ChargePaymentReservation
                    {
                        ReservationId = reservationId,
                        ChargePointId = "CP-NO-TX",
                        ConnectorId = 1,
                        ChargeTagId = "TAG-NO-TX",
                        Currency = "eur",
                        Status = PaymentReservationStatus.Charging,
                        CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5),
                        UpdatedAtUtc = DateTime.UtcNow
                    });
                    setupContext.SaveChanges();
                }

                var middleware = CreateMiddleware();
                var httpContext = new DefaultHttpContext();
                httpContext.Request.Method = "POST";
                httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes($"{{\"reservationId\":\"{reservationId}\"}}"));
                httpContext.Response.Body = new MemoryStream();

                using (var actionContext = CreateContext(databasePath))
                {
                    await InvokeHandlePaymentStopAsync(middleware, httpContext, actionContext);
                }

                httpContext.Response.Body.Position = 0;
                using var reader = new StreamReader(httpContext.Response.Body);
                var payload = JObject.Parse(await reader.ReadToEndAsync());

                Assert.Equal("NoOpenTransaction", payload["status"]?.Value<string>());
                Assert.Equal("Charging", payload["reservationStatus"]?.Value<string>());
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Fact]
        public async Task HandlePaymentStopAsync_ReturnsChargerOffline_WhenTransactionIsActiveButSocketIsMissing()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-stop-offline-{Guid.NewGuid():N}.sqlite");
            Guid reservationId = Guid.NewGuid();

            try
            {
                int transactionId;
                using (var setupContext = CreateContext(databasePath))
                {
                    setupContext.ChargePoints.Add(new ChargePoint
                    {
                        ChargePointId = "CP-OFFLINE",
                        Name = "CP-OFFLINE"
                    });
                    var transaction = new Transaction
                    {
                        ChargePointId = "CP-OFFLINE",
                        ConnectorId = 1,
                        StartTagId = "TAG-OFFLINE",
                        StartTime = DateTime.UtcNow.AddMinutes(-10),
                        MeterStart = 1.0
                    };
                    setupContext.Transactions.Add(transaction);
                    setupContext.SaveChanges();
                    transactionId = transaction.TransactionId;

                    setupContext.ChargePaymentReservations.Add(new ChargePaymentReservation
                    {
                        ReservationId = reservationId,
                        ChargePointId = "CP-OFFLINE",
                        ConnectorId = 1,
                        ChargeTagId = "TAG-OFFLINE",
                        OcppIdTag = "TAG-OFFLINE",
                        Currency = "eur",
                        Status = PaymentReservationStatus.Charging,
                        TransactionId = transactionId,
                        CreatedAtUtc = DateTime.UtcNow.AddMinutes(-12),
                        UpdatedAtUtc = DateTime.UtcNow
                    });
                    setupContext.SaveChanges();
                }

                var middleware = CreateMiddleware();
                var previousStatuses = ReplaceChargePointStatuses(new Dictionary<string, ChargePointStatus>());

                try
                {
                    var httpContext = new DefaultHttpContext();
                    httpContext.Request.Method = "POST";
                    httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes($"{{\"reservationId\":\"{reservationId}\"}}"));
                    httpContext.Response.Body = new MemoryStream();

                    using (var actionContext = CreateContext(databasePath))
                    {
                        await InvokeHandlePaymentStopAsync(middleware, httpContext, actionContext);
                    }

                    httpContext.Response.Body.Position = 0;
                    using var reader = new StreamReader(httpContext.Response.Body);
                    var payload = JObject.Parse(await reader.ReadToEndAsync());

                    Assert.Equal("ChargerOffline", payload["status"]?.Value<string>());
                    Assert.Equal(transactionId, payload["transactionId"]?.Value<int>());
                }
                finally
                {
                    ReplaceChargePointStatuses(previousStatuses);
                }
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Fact]
        public async Task HandlePaymentStopAsync_ReturnsAlreadyStopped_ForTerminalReservation()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-stop-terminal-{Guid.NewGuid():N}.sqlite");
            Guid reservationId = Guid.NewGuid();

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    setupContext.ChargePaymentReservations.Add(new ChargePaymentReservation
                    {
                        ReservationId = reservationId,
                        ChargePointId = "CP-DONE",
                        ConnectorId = 1,
                        ChargeTagId = "TAG-DONE",
                        Currency = "eur",
                        Status = PaymentReservationStatus.Completed,
                        CapturedAmountCents = 125,
                        CreatedAtUtc = DateTime.UtcNow.AddMinutes(-20),
                        UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-1)
                    });
                    setupContext.SaveChanges();
                }

                var middleware = CreateMiddleware();
                var httpContext = new DefaultHttpContext();
                httpContext.Request.Method = "POST";
                httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes($"{{\"reservationId\":\"{reservationId}\"}}"));
                httpContext.Response.Body = new MemoryStream();

                using (var actionContext = CreateContext(databasePath))
                {
                    await InvokeHandlePaymentStopAsync(middleware, httpContext, actionContext);
                }

                httpContext.Response.Body.Position = 0;
                using var reader = new StreamReader(httpContext.Response.Body);
                var payload = JObject.Parse(await reader.ReadToEndAsync());

                Assert.Equal("AlreadyStopped", payload["status"]?.Value<string>());
                Assert.Equal("Completed", payload["reservationStatus"]?.Value<string>());
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Fact]
        public async Task TryAutoStopIdleTransactionAsync_RemoteStopsSuspendedTransaction_WhenThresholdReached()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-auto-stop-{Guid.NewGuid():N}.sqlite");

            try
            {
                int transactionId;
                using (var setupContext = CreateContext(databasePath))
                {
                    setupContext.ChargePoints.Add(new ChargePoint
                    {
                        ChargePointId = "CP-AUTO",
                        Name = "CP-AUTO"
                    });
                    var transaction = new Transaction
                    {
                        ChargePointId = "CP-AUTO",
                        ConnectorId = 1,
                        StartTagId = "TAG-AUTO",
                        StartTime = DateTime.UtcNow.AddMinutes(-30),
                        MeterStart = 2.0,
                        ChargingEndedAtUtc = DateTime.UtcNow.AddMinutes(-10)
                    };
                    setupContext.Transactions.Add(transaction);
                    setupContext.SaveChanges();
                    transactionId = transaction.TransactionId;
                }

                bool remoteStopAttempted = false;
                var middleware = CreateMiddleware(
                    configValues: new Dictionary<string, string?>
                    {
                        ["Payments:IdleAutoStopMinutes"] = "5"
                    },
                    serviceProvider: CreateServiceProvider(databasePath));

                var fakeSocket = new FakeOpenWebSocket(async () =>
                {
                    remoteStopAttempted = true;
                    var queuedMessage = GetQueuedRequest(middleware);
                    Assert.NotNull(queuedMessage?.TaskCompletionSource);
                    queuedMessage.TaskCompletionSource.SetResult("{\"status\":\"Accepted\"}");
                    await Task.CompletedTask;
                });

                var previousStatuses = ReplaceChargePointStatuses(new Dictionary<string, ChargePointStatus>
                {
                    ["CP-AUTO"] = new ChargePointStatus
                    {
                        Id = "CP-AUTO",
                        Protocol = "ocpp1.6",
                        WebSocket = fakeSocket,
                        OnlineConnectors = new Dictionary<int, OnlineConnectorStatus>
                        {
                            [1] = new OnlineConnectorStatus
                            {
                                Status = ConnectorStatusEnum.Occupied,
                                OcppStatus = "SuspendedEV"
                            }
                        }
                    }
                });

                try
                {
                    await InvokeTryAutoStopIdleTransactionAsync(middleware, "CP-AUTO", 1, transactionId, 5);
                    Assert.True(remoteStopAttempted);
                    Assert.Empty(GetIdleAutoStopTransactions(middleware));
                }
                finally
                {
                    ReplaceChargePointStatuses(previousStatuses);
                }
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Fact]
        public void NotifyConnectorOcppStatus_DoesNotTrackIdleAutoStop_WhenDisabled()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-auto-stop-disabled-{Guid.NewGuid():N}.sqlite");

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    setupContext.ChargePoints.Add(new ChargePoint
                    {
                        ChargePointId = "CP-AUTO-DISABLED",
                        Name = "CP-AUTO-DISABLED"
                    });
                    setupContext.Transactions.Add(new Transaction
                    {
                        ChargePointId = "CP-AUTO-DISABLED",
                        ConnectorId = 1,
                        StartTagId = "TAG-AUTO-DISABLED",
                        StartTime = DateTime.UtcNow.AddMinutes(-30),
                        MeterStart = 0.0,
                        ChargingEndedAtUtc = DateTime.UtcNow.AddMinutes(-10)
                    });
                    setupContext.SaveChanges();
                }

                using var actionContext = CreateContext(databasePath);
                var middleware = CreateMiddleware(
                    configValues: new Dictionary<string, string?>
                    {
                        ["Payments:IdleAutoStopMinutes"] = "0"
                    },
                    serviceProvider: CreateServiceProvider(databasePath));

                middleware.NotifyConnectorOcppStatus(
                    actionContext,
                    new ChargePointStatus
                    {
                        Id = "CP-AUTO-DISABLED",
                        Protocol = "ocpp1.6",
                        WebSocket = new FakeOpenWebSocket(() => Task.CompletedTask)
                    },
                    1,
                    "SuspendedEV");

                Assert.Empty(GetIdleAutoStopTransactions(middleware));
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Theory]
        [InlineData("Preparing", true, "Startable")]
        [InlineData("SuspendedEV", false, "Status:SuspendedEV")]
        [InlineData("SuspendedEVSE", false, "Status:SuspendedEVSE")]
        [InlineData("Finishing", false, "Status:Finishing")]
        [InlineData("Reserved", false, "Status:Reserved")]
        [InlineData("Unavailable", false, "Status:Unavailable")]
        [InlineData("Faulted", false, "Status:Faulted")]
        public void GetConnectorStartability_UsesRawOcppStatus(string rawStatus, bool expectedStartable, string expectedReason)
        {
            using var dbContext = CreateContext(Path.Combine(Path.GetTempPath(), $"ocpp-startability-{Guid.NewGuid():N}.sqlite"));
            var middleware = CreateMiddleware();
            var chargePointStatus = new ChargePointStatus
            {
                Id = "CP-STARTABLE",
                Protocol = "ocpp1.6",
                WebSocket = new FakeOpenWebSocket(() => Task.CompletedTask),
                OnlineConnectors = new Dictionary<int, OnlineConnectorStatus>
                {
                    [1] = new OnlineConnectorStatus
                    {
                        Status = rawStatus switch
                        {
                            "Preparing" => ConnectorStatusEnum.Preparing,
                            "Unavailable" => ConnectorStatusEnum.Unavailable,
                            "Faulted" => ConnectorStatusEnum.Faulted,
                            _ => ConnectorStatusEnum.Occupied
                        },
                        OcppStatus = rawStatus
                    }
                }
            };

            var result = InvokeGetConnectorStartability(middleware, dbContext, "CP-STARTABLE", 1, chargePointStatus, null);
            bool startable = Assert.IsType<bool>(result.GetType().GetProperty("Startable")!.GetValue(result));
            string reason = Assert.IsType<string>(result.GetType().GetProperty("Reason")!.GetValue(result));

            Assert.Equal(expectedStartable, startable);
            Assert.Equal(expectedReason, reason);
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

        private static OCPPMiddleware CreateMiddleware(
            IPaymentCoordinator? paymentCoordinator = null,
            IDictionary<string, string?>? configValues = null,
            IServiceProvider? serviceProvider = null)
        {
            var services = serviceProvider ?? new ServiceCollection().BuildServiceProvider();
            var mediator = new StartChargingMediator();
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(configValues ?? new Dictionary<string, string?>())
                .Build();

            return new OCPPMiddleware(
                _ => Task.CompletedTask,
                NullLoggerFactory.Instance,
                configuration,
                services.GetRequiredService<IServiceScopeFactory>(),
                paymentCoordinator ?? new NoopPaymentCoordinator(),
                mediator,
                new ReservationLinkService(mediator));
        }

        private static IServiceProvider CreateServiceProvider(string databasePath)
        {
            return new ServiceCollection()
                .AddDbContext<OCPPCoreContext>(options => options.UseSqlite($"Data Source={databasePath}"))
                .BuildServiceProvider();
        }

        private static async Task<(string Status, string Reason)> InvokeTryStartChargingAsync(
            OCPPMiddleware middleware,
            OCPPCoreContext dbContext,
            ChargePaymentReservation reservation,
            string caller)
        {
            var method = typeof(OCPPMiddleware)
                .GetMethod("TryStartChargingAsync", BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(method);

            var task = Assert.IsAssignableFrom<Task>(method.Invoke(middleware, new object[] { dbContext, reservation, caller }));
            await task;

            var result = task.GetType().GetProperty("Result")?.GetValue(task);
            Assert.NotNull(result);

            var item1 = result.GetType().GetField("Item1")?.GetValue(result) as string;
            var item2 = result.GetType().GetField("Item2")?.GetValue(result) as string;

            return (Assert.IsType<string>(item1), Assert.IsType<string>(item2));
        }

        private static async Task InvokeHandlePaymentStatusAsync(
            OCPPMiddleware middleware,
            HttpContext httpContext,
            OCPPCoreContext dbContext)
        {
            var method = typeof(OCPPMiddleware)
                .GetMethod("HandlePaymentStatusAsync", BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(method);

            var task = Assert.IsAssignableFrom<Task>(method.Invoke(middleware, new object[] { httpContext, dbContext }));
            await task;
        }

        private static async Task InvokeHandlePaymentStopAsync(
            OCPPMiddleware middleware,
            HttpContext httpContext,
            OCPPCoreContext dbContext)
        {
            var method = typeof(OCPPMiddleware)
                .GetMethod("HandlePaymentStopAsync", BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(method);

            var task = Assert.IsAssignableFrom<Task>(method.Invoke(middleware, new object[] { httpContext, dbContext }));
            await task;
        }

        private static async Task InvokeHandlePaymentCancelAsync(
            OCPPMiddleware middleware,
            HttpContext httpContext,
            OCPPCoreContext dbContext)
        {
            var method = typeof(OCPPMiddleware)
                .GetMethod("HandlePaymentCancelAsync", BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(method);

            var task = Assert.IsAssignableFrom<Task>(method.Invoke(middleware, new object[] { httpContext, dbContext }));
            await task;
        }

        private static async Task InvokeTryAutoStopIdleTransactionAsync(
            OCPPMiddleware middleware,
            string chargePointId,
            int connectorId,
            int transactionId,
            int idleAutoStopMinutes)
        {
            var method = typeof(OCPPMiddleware)
                .GetMethod("TryAutoStopIdleTransactionAsync", BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(method);

            var task = Assert.IsAssignableFrom<Task>(method.Invoke(middleware, new object[] { chargePointId, connectorId, transactionId, idleAutoStopMinutes }));
            await task;
        }

        private static OCPPMessage GetQueuedRequest(OCPPMiddleware middleware)
        {
            var field = typeof(OCPPMiddleware)
                .GetField("_requestQueue", BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(field);

            var queue = Assert.IsAssignableFrom<System.Collections.IDictionary>(field.GetValue(middleware));
            var message = queue.Values.Cast<object>().OfType<OCPPMessage>().FirstOrDefault();
            return Assert.IsType<OCPPMessage>(message);
        }

        private static object InvokeGetConnectorStartability(
            OCPPMiddleware middleware,
            OCPPCoreContext dbContext,
            string chargePointId,
            int connectorId,
            ChargePointStatus chargePointStatus,
            Guid? reservationToIgnore)
        {
            var method = typeof(OCPPMiddleware)
                .GetMethod("GetConnectorStartability", BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(method);

            return method.Invoke(middleware, new object?[] { dbContext, chargePointId, connectorId, chargePointStatus, reservationToIgnore })!;
        }

        private static Dictionary<string, ChargePointStatus> ReplaceChargePointStatuses(IDictionary<string, ChargePointStatus> nextValue)
        {
            var field = typeof(OCPPMiddleware)
                .GetField("_chargePointStatusDict", BindingFlags.Static | BindingFlags.NonPublic);

            Assert.NotNull(field);

            var existing = Assert.IsType<ConcurrentDictionary<string, ChargePointStatus>>(field.GetValue(null));
            var previous = existing.ToDictionary(entry => entry.Key, entry => entry.Value);

            foreach (var key in existing.Keys.ToList())
            {
                existing.TryRemove(key, out _);
            }

            foreach (var entry in nextValue)
            {
                existing[entry.Key] = entry.Value;
            }

            return previous;
        }

        private static ConcurrentDictionary<int, DateTime> GetIdleAutoStopTransactions(OCPPMiddleware middleware)
        {
            var field = typeof(OCPPMiddleware)
                .GetField("_idleAutoStopTransactions", BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(field);
            return Assert.IsType<ConcurrentDictionary<int, DateTime>>(field.GetValue(middleware));
        }

        private static void TryDelete(string databasePath)
        {
            try
            {
                File.Delete(databasePath);
            }
            catch
            {
                // Best effort cleanup for temp sqlite db.
            }
        }

        private sealed class FakeOpenWebSocket : WebSocket
        {
            private readonly Func<Task> _onSendAsync;

            public FakeOpenWebSocket(Func<Task> onSendAsync)
            {
                _onSendAsync = onSendAsync;
            }

            public override WebSocketCloseStatus? CloseStatus => null;
            public override string? CloseStatusDescription => null;
            public override WebSocketState State => WebSocketState.Open;
            public override string? SubProtocol => null;

            public override void Abort()
            {
            }

            public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            public override void Dispose()
            {
            }

            public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
            {
                throw new NotSupportedException("ReceiveAsync is not used in this regression test.");
            }

            public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            {
                _ = Encoding.UTF8.GetString(buffer.Array ?? Array.Empty<byte>(), buffer.Offset, buffer.Count);
                return _onSendAsync();
            }
        }

        private sealed class NoopPaymentCoordinator : IPaymentCoordinator
        {
            public bool IsEnabled => false;

            public PaymentSessionResult CreateCheckoutSession(OCPPCoreContext dbContext, PaymentSessionRequest request) => throw new NotSupportedException();
            public PaymentConfirmationResult ConfirmReservation(OCPPCoreContext dbContext, Guid reservationId, string checkoutSessionId) => throw new NotSupportedException();
            public PaymentResumeResult ResumeReservation(OCPPCoreContext dbContext, Guid reservationId) => throw new NotSupportedException();
            public PaymentR1InvoiceResult RequestR1Invoice(OCPPCoreContext dbContext, PaymentR1InvoiceRequest request) => throw new NotSupportedException();
            public void CancelReservation(OCPPCoreContext dbContext, Guid reservationId, string reason) { }
            public void CancelPaymentIntentIfCancelable(OCPPCoreContext dbContext, ChargePaymentReservation reservation, string reason) { }
            public void MarkTransactionStarted(OCPPCoreContext dbContext, string chargePointId, int connectorId, string chargeTagId, int transactionId) { }
            public void CompleteReservation(OCPPCoreContext dbContext, Transaction transaction) { }
            public void HandleWebhookEvent(OCPPCoreContext dbContext, string payload, string signatureHeader) { }
        }

        private sealed class CancellingPaymentCoordinator : IPaymentCoordinator
        {
            public bool IsEnabled => true;

            public PaymentSessionResult CreateCheckoutSession(OCPPCoreContext dbContext, PaymentSessionRequest request) => throw new NotSupportedException();
            public PaymentConfirmationResult ConfirmReservation(OCPPCoreContext dbContext, Guid reservationId, string checkoutSessionId) => throw new NotSupportedException();
            public PaymentResumeResult ResumeReservation(OCPPCoreContext dbContext, Guid reservationId) => throw new NotSupportedException();
            public PaymentR1InvoiceResult RequestR1Invoice(OCPPCoreContext dbContext, PaymentR1InvoiceRequest request) => throw new NotSupportedException();

            public void CancelReservation(OCPPCoreContext dbContext, Guid reservationId, string reason)
            {
                var reservation = dbContext.ChargePaymentReservations.SingleOrDefault(r => r.ReservationId == reservationId);
                if (reservation == null)
                {
                    return;
                }

                reservation.Status = PaymentReservationStatus.Cancelled;
                reservation.LastError = reason;
                reservation.UpdatedAtUtc = DateTime.UtcNow;
                dbContext.SaveChanges();
            }

            public void CancelPaymentIntentIfCancelable(OCPPCoreContext dbContext, ChargePaymentReservation reservation, string reason) { }
            public void MarkTransactionStarted(OCPPCoreContext dbContext, string chargePointId, int connectorId, string chargeTagId, int transactionId) { }
            public void CompleteReservation(OCPPCoreContext dbContext, Transaction transaction) { }
            public void HandleWebhookEvent(OCPPCoreContext dbContext, string payload, string signatureHeader) { }
        }
    }
}
