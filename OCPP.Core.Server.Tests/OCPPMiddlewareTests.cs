using System;
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

        private static OCPPCoreContext CreateContext(string databasePath)
        {
            var options = new DbContextOptionsBuilder<OCPPCoreContext>()
                .UseSqlite($"Data Source={databasePath}")
                .Options;

            var context = new OCPPCoreContext(options);
            context.Database.EnsureCreated();
            return context;
        }

        private static OCPPMiddleware CreateMiddleware()
        {
            var services = new ServiceCollection().BuildServiceProvider();
            var mediator = new StartChargingMediator();

            return new OCPPMiddleware(
                _ => Task.CompletedTask,
                NullLoggerFactory.Instance,
                new ConfigurationBuilder().Build(),
                services.GetRequiredService<IServiceScopeFactory>(),
                new NoopPaymentCoordinator(),
                mediator,
                new ReservationLinkService(mediator));
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

        private static OCPPMessage GetQueuedRequest(OCPPMiddleware middleware)
        {
            var field = typeof(OCPPMiddleware)
                .GetField("_requestQueue", BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(field);

            var queue = Assert.IsAssignableFrom<System.Collections.IDictionary>(field.GetValue(middleware));
            var message = queue.Values.Cast<object>().OfType<OCPPMessage>().FirstOrDefault();
            return Assert.IsType<OCPPMessage>(message);
        }

        private static Dictionary<string, ChargePointStatus> ReplaceChargePointStatuses(Dictionary<string, ChargePointStatus> nextValue)
        {
            var field = typeof(OCPPMiddleware)
                .GetField("_chargePointStatusDict", BindingFlags.Static | BindingFlags.NonPublic);

            Assert.NotNull(field);

            var previous = Assert.IsType<Dictionary<string, ChargePointStatus>>(field.GetValue(null));
            field.SetValue(null, nextValue);
            return previous;
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
            public void CancelReservation(OCPPCoreContext dbContext, Guid reservationId, string reason) { }
            public void CancelPaymentIntentIfCancelable(OCPPCoreContext dbContext, ChargePaymentReservation reservation, string reason) { }
            public void MarkTransactionStarted(OCPPCoreContext dbContext, string chargePointId, int connectorId, string chargeTagId, int transactionId) { }
            public void CompleteReservation(OCPPCoreContext dbContext, Transaction transaction) { }
            public void HandleWebhookEvent(OCPPCoreContext dbContext, string payload, string signatureHeader) { }
        }
    }
}
