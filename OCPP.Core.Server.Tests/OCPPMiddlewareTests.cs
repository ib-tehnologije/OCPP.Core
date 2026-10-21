using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using OCPP.Core.Database;
using OCPP.Core.Server.Payments;
using OCPP.Core.Server.Payments.Providers;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    public class OCPPMiddlewareTests
    {
        [Fact]
        public void ResetConnectionDbContext_RecoversConnectionAfterStaleBuyerConcurrencyFailure()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-middleware-{Guid.NewGuid():N}.sqlite");
            Guid reservationId = Guid.NewGuid();

            try
            {
                using var connectionContext = CreateContext(databasePath);
                connectionContext.ChargePaymentReservations.Add(new ChargePaymentReservation
                {
                    ReservationId = reservationId,
                    ChargePointId = "CP-STALE",
                    ConnectorId = 1,
                    ChargeTagId = "PAY-STALE",
                    Status = PaymentReservationStatus.Charging,
                    Currency = "eur",
                    CreatedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow
                });
                connectionContext.SaveChanges();

                // A charge point connection keeps its DbContext for hours; the portal confirms R1 buyer data meanwhile.
                var trackedReservation = connectionContext.ChargePaymentReservations.Single(r => r.ReservationId == reservationId);
                using (var portalContext = CreateContext(databasePath))
                {
                    var portalReservation = portalContext.ChargePaymentReservations.Single(r => r.ReservationId == reservationId);
                    portalReservation.InvoiceBuyerConfirmedAtUtc = DateTime.UtcNow;
                    portalContext.SaveChanges();
                }

                trackedReservation.Status = PaymentReservationStatus.WaitingForDisconnect;
                Assert.Throws<DbUpdateConcurrencyException>(() => connectionContext.SaveChanges());

                // Without the reset every later write on this connection (StartTransaction, message log, ...) replays the stale update.
                connectionContext.ChargePaymentReservations.Single(r => r.ReservationId == reservationId).UpdatedAtUtc = DateTime.UtcNow;
                Assert.Throws<DbUpdateConcurrencyException>(() => connectionContext.SaveChanges());

                OCPPMiddleware.ResetConnectionDbContext(connectionContext);

                var freshReservation = connectionContext.ChargePaymentReservations.Single(r => r.ReservationId == reservationId);
                Assert.NotNull(freshReservation.InvoiceBuyerConfirmedAtUtc);
                freshReservation.Status = PaymentReservationStatus.WaitingForDisconnect;
                connectionContext.SaveChanges();

                using var verifyContext = CreateContext(databasePath);
                Assert.Equal(
                    PaymentReservationStatus.WaitingForDisconnect,
                    verifyContext.ChargePaymentReservations.Single(r => r.ReservationId == reservationId).Status);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

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
                        InvoiceBuyerCountry = "HR",
                        InvoiceBuyerCompanyName = "Issued Company d.o.o.",
                        InvoiceBuyerTaxIdentifier = "12345678903",
                        InvoiceBuyerOriginalTaxIdentifier = "12345678903",
                        InvoiceBuyerConfirmedAtUtc = new DateTime(2026, 7, 28, 11, 45, 0, DateTimeKind.Utc),
                        InvoiceBuyerVatValidationStatus = "Valid",
                        InvoiceBuyerVatVerificationStatus = "Invalid",
                        InvoiceBuyerVatVerificationCheckedAtUtc =
                            new DateTime(2026, 7, 28, 12, 0, 0, DateTimeKind.Utc),
                        InvoiceBuyerVatVerificationReference = "private-audit-reference",
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
                Assert.Equal("Valid", payload["invoiceBuyerVatValidationStatus"]?.Value<string>());
                Assert.Equal("Invalid", payload["invoiceBuyerVatVerificationStatus"]?.Value<string>());
                Assert.Equal(
                    new DateTime(2026, 7, 28, 12, 0, 0, DateTimeKind.Utc),
                    payload["invoiceBuyerVatVerificationCheckedAtUtc"]?.Value<DateTime>());
                Assert.Null(payload["invoiceBuyerVatVerificationReference"]);
                Assert.False(payload["invoiceBuyer"]?["editable"]?.Value<bool>() ?? true);
                Assert.Equal("Issued Company d.o.o.", payload["invoiceBuyer"]?["companyName"]?.Value<string>());
                Assert.Equal("no-store", httpContext.Response.Headers.CacheControl.ToString());
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Fact]
        public async Task HandlePaymentStatusAsync_IncludesEditableConfirmedBuyerSnapshotBeforeInvoiceSubmission()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-status-editable-buyer-{Guid.NewGuid():N}.sqlite");
            Guid reservationId = Guid.NewGuid();
            var buyerVersion = new DateTime(2026, 8, 28, 8, 15, 0, DateTimeKind.Utc);

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
                        StripeCheckoutSessionId = "sess_editable_buyer",
                        Currency = "eur",
                        Status = PaymentReservationStatus.Authorized,
                        InvoiceBuyerCountry = "SK",
                        InvoiceBuyerCompanyName = "Editable s.r.o.",
                        InvoiceBuyerStreet = "Hlavná 1",
                        InvoiceBuyerPostalCode = "811 01",
                        InvoiceBuyerCity = "Bratislava",
                        InvoiceBuyerEmail = "billing@example.sk",
                        InvoiceBuyerTaxIdentifier = "SK2020102293",
                        InvoiceBuyerOriginalTaxIdentifier = "SK 2020102293",
                        InvoiceBuyerNormalizedVatIdentifier = "SK2020102293",
                        InvoiceBuyerRegistrationNumber = "12345678",
                        InvoiceBuyerIdentifierIsVatRegistration = true,
                        InvoiceBuyerConfirmedAtUtc = buyerVersion,
                        CreatedAtUtc = buyerVersion.AddHours(-1),
                        UpdatedAtUtc = buyerVersion
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
                var payload = JObject.Parse(await new StreamReader(httpContext.Response.Body).ReadToEndAsync());
                var buyer = payload["invoiceBuyer"];

                Assert.True(buyer?["editable"]?.Value<bool>());
                Assert.Equal(buyerVersion, buyer?["version"]?.Value<DateTime>());
                Assert.Equal("SK", buyer?["country"]?.Value<string>());
                Assert.Equal("Editable s.r.o.", buyer?["companyName"]?.Value<string>());
                Assert.Equal("Hlavná 1", buyer?["street"]?.Value<string>());
                Assert.Equal("SK 2020102293", buyer?["taxIdentifier"]?.Value<string>());
                Assert.Equal("12345678", buyer?["registrationNumber"]?.Value<string>());
                Assert.True(buyer?["identifierIsVatRegistration"]?.Value<bool>());
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Fact]
        public async Task HandlePaymentStatusAsync_IncludesPendingR1BuyerWithEmailOnly()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-status-pending-r1-{Guid.NewGuid():N}.sqlite");
            Guid reservationId = Guid.NewGuid();
            var requestedAt = new DateTime(2026, 10, 7, 16, 0, 0, DateTimeKind.Utc);

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
                        StripeCheckoutSessionId = "sess_pending_r1",
                        Currency = "eur",
                        Status = PaymentReservationStatus.Charging,
                        InvoiceR1RequestedAtUtc = requestedAt,
                        InvoiceBuyerEmail = "billing@example.hr",
                        CreatedAtUtc = requestedAt,
                        UpdatedAtUtc = requestedAt
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
                var payload = JObject.Parse(await new StreamReader(httpContext.Response.Body).ReadToEndAsync());
                var buyer = payload["invoiceBuyer"];

                Assert.NotNull(buyer);
                Assert.True(buyer?["editable"]?.Value<bool>());
                Assert.False(buyer?["confirmed"]?.Value<bool>());
                Assert.Equal(JTokenType.Null, buyer?["version"]?.Type);
                Assert.Equal("billing@example.hr", buyer?["email"]?.Value<string>());
                Assert.Equal(JTokenType.Null, buyer?["companyName"]?.Type);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Theory]
        [InlineData("Submitting")]
        [InlineData("ProviderUnknown")]
        public async Task HandlePaymentStatusAsync_LocksBuyerEditingForIndeterminateInvoiceState(string invoiceStatus)
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-status-locked-buyer-{Guid.NewGuid():N}.sqlite");
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
                        StripeCheckoutSessionId = "sess_locked_buyer",
                        Currency = "eur",
                        Status = PaymentReservationStatus.Completed,
                        InvoiceBuyerCountry = "HR",
                        InvoiceBuyerCompanyName = "Locked d.o.o.",
                        InvoiceBuyerStreet = "Ilica 1",
                        InvoiceBuyerPostalCode = "10000",
                        InvoiceBuyerCity = "Zagreb",
                        InvoiceBuyerEmail = "racuni@example.hr",
                        InvoiceBuyerTaxIdentifier = "12345678903",
                        InvoiceBuyerConfirmedAtUtc = DateTime.UtcNow.AddMinutes(-5),
                        CreatedAtUtc = DateTime.UtcNow.AddMinutes(-15),
                        UpdatedAtUtc = DateTime.UtcNow
                    });
                    setupContext.InvoiceSubmissionLogs.Add(new InvoiceSubmissionLog
                    {
                        ReservationId = reservationId,
                        Provider = "ERacuni",
                        Mode = "Submit",
                        Status = invoiceStatus,
                        CreatedAtUtc = DateTime.UtcNow.AddMinutes(-1)
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
                var payload = JObject.Parse(await new StreamReader(httpContext.Response.Body).ReadToEndAsync());
                Assert.False(payload["invoiceBuyer"]?["editable"]?.Value<bool>() ?? true);
                Assert.True(payload["invoice"]?["customerBuyerDataLocked"]?.Value<bool>());
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Fact]
        public async Task HandlePaymentStatusAsync_SurfacesSanitizedProviderValidationMessage()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-status-invoice-error-{Guid.NewGuid():N}.sqlite");
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
                        CreatedAtUtc = DateTime.UtcNow,
                        UpdatedAtUtc = DateTime.UtcNow
                    });
                    setupContext.InvoiceSubmissionLogs.Add(new InvoiceSubmissionLog
                    {
                        ReservationId = reservationId,
                        Provider = "ERacuni",
                        Mode = "Submit",
                        Status = "Failed",
                        HttpStatusCode = 422,
                        Error = "raw provider payload with secret-token and customer tax data",
                        CreatedAtUtc = DateTime.UtcNow
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
                var payload = JObject.Parse(await new StreamReader(httpContext.Response.Body).ReadToEndAsync());
                var message = payload["invoice"]?["customerMessage"]?.Value<string>();
                Assert.Contains("buyer", message, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("secret-token", message, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("tax data", message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Fact]
        public async Task HandlePaymentStatusAsync_IncludesPublicConnectorPresentationFields()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-status-public-connector-{Guid.NewGuid():N}.sqlite");
            Guid reservationId = Guid.NewGuid();

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    setupContext.ChargePoints.Add(new ChargePoint
                    {
                        ChargePointId = "CP-PUBLIC-CONNECTOR",
                        Name = "Public connector test",
                        PublicDisplayCode = "HR*TTK*052009*01"
                    });
                    setupContext.ConnectorStatuses.Add(new ConnectorStatus
                    {
                        ChargePointId = "CP-PUBLIC-CONNECTOR",
                        ConnectorId = 2,
                        ConnectorName = "Desni",
                        LastStatus = "Available",
                        LastStatusTime = DateTime.UtcNow
                    });
                    setupContext.ChargePaymentReservations.Add(new ChargePaymentReservation
                    {
                        ReservationId = reservationId,
                        ChargePointId = "CP-PUBLIC-CONNECTOR",
                        ConnectorId = 2,
                        ChargeTagId = "TAG-PUBLIC-CONNECTOR",
                        Currency = "eur",
                        Status = PaymentReservationStatus.Authorized,
                        CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5),
                        UpdatedAtUtc = DateTime.UtcNow
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

                Assert.Equal("Desni", payload["connectorName"]?.Value<string>());
                Assert.Equal("01*2", payload["publicConnectorShortCode"]?.Value<string>());
                Assert.Equal("HR*TTK*052009*01*2", payload["publicConnectorCode"]?.Value<string>());
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Fact]
        public async Task HandlePaymentStatusAsync_UsesCanonicalLifecycleAndLiveEnergyFields()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-status-canonical-{Guid.NewGuid():N}.sqlite");
            Guid reservationId = Guid.NewGuid();
            var now = new DateTime(2026, 3, 12, 19, 45, 0, DateTimeKind.Utc);

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    setupContext.ChargePoints.Add(new ChargePoint
                    {
                        ChargePointId = "CP-CANONICAL",
                        Name = "CP-CANONICAL"
                    });
                    setupContext.ChargePaymentReservations.Add(new ChargePaymentReservation
                    {
                        ReservationId = reservationId,
                        ChargePointId = "CP-CANONICAL",
                        ConnectorId = 1,
                        ChargeTagId = "TAG-CANONICAL",
                        Currency = "eur",
                        Status = PaymentReservationStatus.WaitingForDisconnect,
                        TransactionId = 771,
                        StartTransactionAtUtc = now.AddMinutes(-30),
                        StopTransactionAtUtc = now.AddMinutes(-12),
                        UsageFeeAnchorMinutes = 1,
                        UsageFeePerMinute = 0.25m,
                        StartUsageFeeAfterMinutes = 0,
                        MaxUsageFeeMinutes = 120,
                        CreatedAtUtc = now.AddMinutes(-35),
                        UpdatedAtUtc = now.AddMinutes(-1)
                    });
                    setupContext.Transactions.Add(new Transaction
                    {
                        TransactionId = 771,
                        ChargePointId = "CP-CANONICAL",
                        ConnectorId = 1,
                        StartTagId = "TAG-CANONICAL",
                        StartTime = now.AddMinutes(-30),
                        StopTime = now.AddMinutes(-12),
                        MeterStart = 5.0,
                        MeterStop = null,
                        EnergyKwh = 0
                    });
                    setupContext.SaveChanges();
                }

                var middleware = CreateMiddleware(configValues: new Dictionary<string, string?>
                {
                    ["Payments:IdleFeeExcludedWindow"] = "00:00-23:59",
                    ["Payments:IdleFeeExcludedTimeZoneId"] = "Europe/Zagreb"
                });

                var previousStatuses = ReplaceChargePointStatuses(new Dictionary<string, ChargePointStatus>
                {
                    ["CP-CANONICAL"] = new ChargePointStatus
                    {
                        Id = "CP-CANONICAL",
                        Protocol = "ocpp1.6",
                        WebSocket = new FakeOpenWebSocket(() => Task.CompletedTask),
                        OnlineConnectors = new Dictionary<int, OnlineConnectorStatus>
                        {
                            [1] = new OnlineConnectorStatus
                            {
                                Status = ConnectorStatusEnum.Occupied,
                                OcppStatus = "SuspendedEV",
                                MeterKWH = 9.7,
                                MeterValueDate = new DateTimeOffset(now)
                            }
                        }
                    }
                });

                try
                {
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

                    Assert.Equal("WaitingForDisconnect", payload["status"]?.Value<string>());
                    Assert.Equal("waitingForDisconnect", payload["sessionStage"]?.Value<string>());
                    Assert.True(Math.Abs((payload["liveMeterKwh"]?.Value<double>() ?? 0d) - 9.7) < 0.001);
                    Assert.True(Math.Abs((payload["liveSessionEnergyKwh"]?.Value<double>() ?? 0d) - 4.7) < 0.001);
                    Assert.True(payload["idleBillingPausedByWindow"]?.Value<bool>());
                    Assert.Equal("SuspendedEV", payload["liveOcppStatus"]?.Value<string>());
                    Assert.Equal(JTokenType.Null, payload["disconnectedAtUtc"]?.Type);
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
        public async Task HandlePaymentStatusAsync_UsesWaitingForDisconnectStageAndLiveSessionEnergy()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-status-disconnect-{Guid.NewGuid():N}.sqlite");
            Guid reservationId = Guid.NewGuid();
            DateTime now = DateTime.UtcNow;

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    setupContext.ChargePoints.Add(new ChargePoint
                    {
                        ChargePointId = "CP-WAIT",
                        Name = "CP-WAIT"
                    });

                    var transaction = new Transaction
                    {
                        TransactionId = 515,
                        ChargePointId = "CP-WAIT",
                        ConnectorId = 1,
                        StartTagId = "TAG-WAIT",
                        StartTime = now.AddMinutes(-25),
                        StopTime = now.AddMinutes(-1),
                        MeterStart = 10.0
                    };

                    setupContext.Transactions.Add(transaction);
                    setupContext.ChargePaymentReservations.Add(new ChargePaymentReservation
                    {
                        ReservationId = reservationId,
                        ChargePointId = "CP-WAIT",
                        ConnectorId = 1,
                        ChargeTagId = "TAG-WAIT",
                        OcppIdTag = "TAG-WAIT",
                        Currency = "eur",
                        Status = PaymentReservationStatus.WaitingForDisconnect,
                        TransactionId = transaction.TransactionId,
                        StartTransactionAtUtc = now.AddMinutes(-24),
                        StopTransactionAtUtc = now.AddMinutes(-1),
                        CreatedAtUtc = now.AddMinutes(-30),
                        UpdatedAtUtc = now
                    });
                    setupContext.SaveChanges();
                }

                var previousStatuses = ReplaceChargePointStatuses(new Dictionary<string, ChargePointStatus>
                {
                    ["CP-WAIT"] = new ChargePointStatus
                    {
                        Id = "CP-WAIT",
                        Protocol = "ocpp1.6",
                        WebSocket = new FakeOpenWebSocket(() => Task.CompletedTask),
                        OnlineConnectors = new Dictionary<int, OnlineConnectorStatus>
                        {
                            [1] = new OnlineConnectorStatus
                            {
                                Status = ConnectorStatusEnum.Occupied,
                                OcppStatus = "Finishing",
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

                    Assert.Equal("waitingForDisconnect", payload["sessionStage"]?.Value<string>());
                    Assert.Null(payload["disconnectedAtUtc"]?.Value<string>());
                    Assert.True(Math.Abs((payload["liveSessionEnergyKwh"]?.Value<double>() ?? 0d) - 4.2) < 0.001);
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
        public async Task HandlePaymentStatusAsync_UsesDatabaseQuietHoursForIdlePauseFlag()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-status-window-{Guid.NewGuid():N}.sqlite");
            Guid reservationId = Guid.NewGuid();
            DateTime now = DateTime.UtcNow;
            string window = $"{now.AddMinutes(-5):HH\\:mm}-{now.AddMinutes(5):HH\\:mm}";

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    setupContext.ChargePoints.Add(new ChargePoint
                    {
                        ChargePointId = "CP-WINDOW",
                        Name = "CP-WINDOW"
                    });
                    setupContext.PublicPortalSettings.Add(new PublicPortalSettings
                    {
                        CreatedAtUtc = now.AddMinutes(-10),
                        UpdatedAtUtc = now,
                        IdleFeeExcludedWindowEnabled = true,
                        IdleFeeExcludedWindow = window
                    });

                    var transaction = new Transaction
                    {
                        TransactionId = 516,
                        ChargePointId = "CP-WINDOW",
                        ConnectorId = 1,
                        StartTagId = "TAG-WINDOW",
                        StartTime = now.AddMinutes(-30),
                        MeterStart = 10.0,
                        ChargingEndedAtUtc = now.AddMinutes(-2)
                    };

                    setupContext.Transactions.Add(transaction);
                    setupContext.ChargePaymentReservations.Add(new ChargePaymentReservation
                    {
                        ReservationId = reservationId,
                        ChargePointId = "CP-WINDOW",
                        ConnectorId = 1,
                        ChargeTagId = "TAG-WINDOW",
                        OcppIdTag = "TAG-WINDOW",
                        Currency = "eur",
                        Status = PaymentReservationStatus.Charging,
                        TransactionId = transaction.TransactionId,
                        UsageFeeAnchorMinutes = 1,
                        UsageFeePerMinute = 0.50m,
                        StartUsageFeeAfterMinutes = 0,
                        MaxUsageFeeMinutes = 120,
                        CreatedAtUtc = now.AddMinutes(-35),
                        UpdatedAtUtc = now
                    });
                    setupContext.SaveChanges();
                }

                var previousStatuses = ReplaceChargePointStatuses(new Dictionary<string, ChargePointStatus>
                {
                    ["CP-WINDOW"] = new ChargePointStatus
                    {
                        Id = "CP-WINDOW",
                        Protocol = "ocpp1.6",
                        WebSocket = new FakeOpenWebSocket(() => Task.CompletedTask),
                        OnlineConnectors = new Dictionary<int, OnlineConnectorStatus>
                        {
                            [1] = new OnlineConnectorStatus
                            {
                                Status = ConnectorStatusEnum.Occupied,
                                OcppStatus = "SuspendedEV",
                                OcppStatusAtUtc = now,
                                MeterKWH = 11.0,
                                MeterValueDate = DateTimeOffset.UtcNow
                            }
                        }
                    }
                });

                try
                {
                    var middleware = CreateMiddleware(configValues: new Dictionary<string, string?>
                    {
                        ["Payments:IdleFeeExcludedTimeZoneId"] = "UTC"
                    });
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

                    Assert.True(payload["idleBillingPausedByWindow"]?.Value<bool>() ?? false);
                    Assert.Equal("charging", payload["sessionStage"]?.Value<string>());
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
        public void NotifyTransactionMeterUpdated_SnapshotsChargePointLimit_AndDeduplicatesOverCapTicks()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-max-energy-track-{Guid.NewGuid():N}.sqlite");

            try
            {
                int transactionId;
                using (var setupContext = CreateContext(databasePath))
                {
                    setupContext.ChargePoints.Add(new ChargePoint
                    {
                        ChargePointId = "CP-MAX-TRACK",
                        Name = "CP-MAX-TRACK",
                        MaxSessionKwh = 80
                    });
                    var transaction = new Transaction
                    {
                        ChargePointId = "CP-MAX-TRACK",
                        ConnectorId = 1,
                        StartTagId = "TAG-MAX-TRACK",
                        StartTime = DateTime.UtcNow.AddMinutes(-20),
                        MeterStart = 100
                    };
                    setupContext.Transactions.Add(transaction);
                    setupContext.SaveChanges();
                    transactionId = transaction.TransactionId;
                }

                using var actionContext = CreateContext(databasePath);
                var middleware = CreateMiddleware(serviceProvider: CreateServiceProvider(databasePath));
                var chargePointStatus = new ChargePointStatus
                {
                    Id = "CP-MAX-TRACK",
                    Protocol = "ocpp1.6"
                };

                middleware.NotifyTransactionMeterUpdated(actionContext, chargePointStatus, 1, transactionId, 179.9, "BelowCap");
                Assert.Empty(GetMaxEnergyAutoStopTransactions(middleware));

                actionContext.Entry(actionContext.Transactions.Single(t => t.TransactionId == transactionId)).Reload();
                Assert.Equal(80, actionContext.Transactions.Single(t => t.TransactionId == transactionId).MaxEnergyKwh);

                middleware.NotifyTransactionMeterUpdated(actionContext, chargePointStatus, 1, transactionId, 180.0, "AtCap");
                var tracked = GetMaxEnergyAutoStopTransactions(middleware);
                Assert.True(tracked.ContainsKey(transactionId));
                var firstRequestedAt = tracked[transactionId];

                middleware.NotifyTransactionMeterUpdated(actionContext, chargePointStatus, 1, transactionId, 181.4, "AboveCapAgain");
                Assert.Single(tracked);
                Assert.Equal(firstRequestedAt, tracked[transactionId]);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Fact]
        public async Task TryAutoStopMaxEnergyTransactionAsync_RemoteStopsReachedTransaction()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-max-energy-stop-{Guid.NewGuid():N}.sqlite");

            try
            {
                int transactionId;
                using (var setupContext = CreateContext(databasePath))
                {
                    setupContext.ChargePoints.Add(new ChargePoint
                    {
                        ChargePointId = "CP-MAX-STOP",
                        Name = "CP-MAX-STOP"
                    });
                    var transaction = new Transaction
                    {
                        ChargePointId = "CP-MAX-STOP",
                        ConnectorId = 1,
                        StartTagId = "TAG-MAX-STOP",
                        StartTime = DateTime.UtcNow.AddMinutes(-25),
                        MeterStart = 5.0,
                        MeterStop = 85.2,
                        MaxEnergyKwh = 80
                    };
                    setupContext.Transactions.Add(transaction);
                    setupContext.SaveChanges();
                    transactionId = transaction.TransactionId;
                }

                bool remoteStopAttempted = false;
                var middleware = CreateMiddleware(serviceProvider: CreateServiceProvider(databasePath));
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
                    ["CP-MAX-STOP"] = new ChargePointStatus
                    {
                        Id = "CP-MAX-STOP",
                        Protocol = "ocpp1.6",
                        WebSocket = fakeSocket,
                        OnlineConnectors = new Dictionary<int, OnlineConnectorStatus>
                        {
                            [1] = new OnlineConnectorStatus
                            {
                                Status = ConnectorStatusEnum.Occupied,
                                MeterKWH = 85.2
                            }
                        }
                    }
                });

                try
                {
                    await InvokeTryAutoStopMaxEnergyTransactionAsync(middleware, "CP-MAX-STOP", 1, transactionId, "UnitTest");
                    Assert.True(remoteStopAttempted);
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
        public async Task HandlePaymentStatusAsync_IncludesMaxEnergyMetadata()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-status-max-energy-{Guid.NewGuid():N}.sqlite");
            Guid reservationId = Guid.NewGuid();
            DateTime requestedAtUtc = DateTime.UtcNow.AddSeconds(-30);

            try
            {
                int transactionId;
                using (var setupContext = CreateContext(databasePath))
                {
                    setupContext.ChargePoints.Add(new ChargePoint
                    {
                        ChargePointId = "CP-STATUS-MAX",
                        Name = "CP-STATUS-MAX",
                        MaxSessionKwh = 80
                    });
                    setupContext.ConnectorStatuses.Add(new ConnectorStatus
                    {
                        ChargePointId = "CP-STATUS-MAX",
                        ConnectorId = 1,
                        ConnectorName = "Connector A",
                        LastStatus = "Charging",
                        LastStatusTime = DateTime.UtcNow
                    });
                    var transaction = new Transaction
                    {
                        ChargePointId = "CP-STATUS-MAX",
                        ConnectorId = 1,
                        StartTagId = "TAG-STATUS-MAX",
                        StartTime = DateTime.UtcNow.AddMinutes(-15),
                        StopTime = DateTime.UtcNow.AddMinutes(-1),
                        MeterStart = 10,
                        MeterStop = 85.4,
                        MaxEnergyKwh = 75
                    };
                    setupContext.Transactions.Add(transaction);
                    setupContext.SaveChanges();
                    transactionId = transaction.TransactionId;

                    setupContext.ChargePaymentReservations.Add(new ChargePaymentReservation
                    {
                        ReservationId = reservationId,
                        ChargePointId = "CP-STATUS-MAX",
                        ConnectorId = 1,
                        ChargeTagId = "TAG-STATUS-MAX",
                        Currency = "eur",
                        Status = PaymentReservationStatus.Completed,
                        TransactionId = transactionId,
                        MaxEnergyKwh = 80,
                        CreatedAtUtc = DateTime.UtcNow.AddMinutes(-20),
                        UpdatedAtUtc = DateTime.UtcNow
                    });
                    setupContext.SaveChanges();
                }

                var middleware = CreateMiddleware(serviceProvider: CreateServiceProvider(databasePath));
                GetMaxEnergyAutoStopTransactions(middleware)[transactionId] = requestedAtUtc;

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

                Assert.Equal(75, payload["maxEnergyKwh"]?.Value<double>());
                Assert.Equal(75, payload["transactionMaxEnergyKwh"]?.Value<double>());
                Assert.True(payload["serverMaxEnergyStopRequested"]?.Value<bool>());
                Assert.True(payload["serverMaxEnergyLimitReached"]?.Value<bool>());
                Assert.NotNull(payload["serverMaxEnergyStopRequestedAtUtc"]?.Value<DateTime>());
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

        [Fact]
        public void NotifyConnectorOcppStatus_ClosesOpenTransaction_WhenConnectorReportsAvailable()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-available-recovery-{Guid.NewGuid():N}.sqlite");
            Guid reservationId = Guid.NewGuid();
            var availableAt = new DateTimeOffset(2026, 5, 9, 15, 51, 17, TimeSpan.Zero);

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    setupContext.ChargePoints.Add(new ChargePoint
                    {
                        ChargePointId = "CP-AVAILABLE",
                        Name = "CP-AVAILABLE"
                    });
                    setupContext.ConnectorStatuses.Add(new ConnectorStatus
                    {
                        ChargePointId = "CP-AVAILABLE",
                        ConnectorId = 2,
                        LastStatus = "Available",
                        LastStatusTime = availableAt.UtcDateTime,
                        LastMeter = 53.921875,
                        LastMeterTime = availableAt.UtcDateTime.AddMinutes(-1)
                    });
                    setupContext.Transactions.Add(new Transaction
                    {
                        TransactionId = 4376,
                        ChargePointId = "CP-AVAILABLE",
                        ConnectorId = 2,
                        StartTagId = "PAY-AVAILABLE",
                        StartTime = availableAt.UtcDateTime.AddHours(-7),
                        MeterStart = 50.0,
                        TrustedMaximumPowerKw = 22,
                        TrustedMaximumPowerToleranceKw = 0.5,
                        TrustedMaximumPowerAtUtc = availableAt.UtcDateTime.AddHours(-7),
                        TrustedMaximumPowerSource = "OCPP1.6:MeterValues:Power.Offered"
                    });
                    setupContext.ChargePaymentReservations.Add(new ChargePaymentReservation
                    {
                        ReservationId = reservationId,
                        ChargePointId = "CP-AVAILABLE",
                        ConnectorId = 2,
                        ChargeTagId = "PAY-AVAILABLE",
                        OcppIdTag = "PAY-AVAILABLE",
                        Currency = "eur",
                        Status = PaymentReservationStatus.Charging,
                        TransactionId = 4376,
                        CreatedAtUtc = availableAt.UtcDateTime.AddHours(-7),
                        UpdatedAtUtc = availableAt.UtcDateTime.AddHours(-7)
                    });
                    setupContext.SaveChanges();
                }

                var coordinator = new CompletingPaymentCoordinator();
                var middleware = CreateMiddleware(
                    paymentCoordinator: coordinator,
                    serviceProvider: CreateServiceProvider(databasePath));

                using (var actionContext = CreateContext(databasePath))
                {
                    middleware.NotifyConnectorOcppStatus(
                        actionContext,
                        new ChargePointStatus
                        {
                            Id = "CP-AVAILABLE",
                            Protocol = "ocpp1.6",
                            OnlineConnectors = new Dictionary<int, OnlineConnectorStatus>
                            {
                                [2] = new OnlineConnectorStatus
                                {
                                    MeterKWH = 53.921875
                                }
                            }
                        },
                        2,
                        "Available",
                        availableAt);
                }

                using (var verificationContext = CreateContext(databasePath))
                {
                    var transaction = verificationContext.Transactions.Single(t => t.TransactionId == 4376);
                    var reservation = verificationContext.ChargePaymentReservations.Single(r => r.ReservationId == reservationId);

                    Assert.Equal(availableAt.UtcDateTime, transaction.StopTime);
                    Assert.Equal(53.921875, transaction.MeterStop);
                    Assert.Equal("ConnectorAvailableWithoutStopTransaction", transaction.StopReason);
                    Assert.Equal(availableAt.UtcDateTime, transaction.ChargingEndedAtUtc);
                    Assert.Equal(PaymentReservationStatus.Completed, reservation.Status);
                    Assert.Equal(availableAt.UtcDateTime, reservation.StopTransactionAtUtc);
                    Assert.Equal(availableAt.UtcDateTime, reservation.DisconnectedAtUtc);
                }

                Assert.Equal(new[] { 4376 }, coordinator.CompletedTransactionIds);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Fact]
        public async Task NotifyConnectorPreparing_AutoReassignsSingleAwaitingPlugReservationToPreparingConnector()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-misplug-remap-{Guid.NewGuid():N}.sqlite");
            Guid reservationId = Guid.NewGuid();
            var remoteStartSent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    setupContext.ChargePaymentReservations.Add(new ChargePaymentReservation
                    {
                        ReservationId = reservationId,
                        ChargePointId = "CP-MISPLUG",
                        ConnectorId = 1,
                        ChargeTagId = "WEB-MISPLUG",
                        OcppIdTag = "MISPLUG001",
                        Currency = "eur",
                        Status = PaymentReservationStatus.Authorized,
                        AwaitingPlug = true,
                        CreatedAtUtc = DateTime.UtcNow.AddMinutes(-1),
                        UpdatedAtUtc = DateTime.UtcNow,
                        StartDeadlineAtUtc = DateTime.UtcNow.AddMinutes(10)
                    });
                    setupContext.SaveChanges();
                }

                var middleware = CreateMiddleware(serviceProvider: CreateServiceProvider(databasePath));
                var fakeSocket = new FakeOpenWebSocket(async () =>
                {
                    var queuedMessage = GetQueuedRequest(middleware);
                    Assert.NotNull(queuedMessage?.TaskCompletionSource);
                    queuedMessage.TaskCompletionSource.SetResult("{\"status\":\"Accepted\"}");
                    remoteStartSent.TrySetResult(true);
                    await Task.CompletedTask;
                });

                var previousStatuses = ReplaceChargePointStatuses(new Dictionary<string, ChargePointStatus>
                {
                    ["CP-MISPLUG"] = new ChargePointStatus
                    {
                        Id = "CP-MISPLUG",
                        Protocol = "ocpp1.6",
                        WebSocket = fakeSocket,
                        OnlineConnectors = new Dictionary<int, OnlineConnectorStatus>
                        {
                            [1] = new OnlineConnectorStatus
                            {
                                Status = ConnectorStatusEnum.Available,
                                OcppStatus = "Available"
                            },
                            [2] = new OnlineConnectorStatus
                            {
                                Status = ConnectorStatusEnum.Preparing,
                                OcppStatus = "Preparing"
                            }
                        }
                    }
                });

                try
                {
                    middleware.NotifyConnectorPreparing("CP-MISPLUG", 2);

                    var sentTask = await Task.WhenAny(remoteStartSent.Task, Task.Delay(3000));
                    Assert.Same(remoteStartSent.Task, sentTask);

                    await WaitUntilAsync(() =>
                    {
                        using var verificationContext = CreateContext(databasePath);
                        var reservation = verificationContext.ChargePaymentReservations.Single(r => r.ReservationId == reservationId);
                        return reservation.ConnectorId == 2 &&
                               reservation.Status == PaymentReservationStatus.StartRequested &&
                               reservation.AwaitingPlug == false &&
                               reservation.RemoteStartResult == "Accepted";
                    });

                    using var finalContext = CreateContext(databasePath);
                    var persisted = finalContext.ChargePaymentReservations.Single(r => r.ReservationId == reservationId);

                    Assert.Equal(2, persisted.ConnectorId);
                    Assert.Equal(PaymentReservationStatus.StartRequested, persisted.Status);
                    Assert.False(persisted.AwaitingPlug ?? true);
                    Assert.Equal("Accepted", persisted.RemoteStartResult);
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
        public async Task NotifyConnectorPreparing_DoesNotAutoReassignWhenMultipleAwaitingReservationsExist()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-misplug-ambiguous-{Guid.NewGuid():N}.sqlite");
            int sendCount = 0;

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    setupContext.ChargePaymentReservations.AddRange(
                        new ChargePaymentReservation
                        {
                            ReservationId = Guid.NewGuid(),
                            ChargePointId = "CP-AMBIG",
                            ConnectorId = 1,
                            ChargeTagId = "WEB-AMBIG-1",
                            OcppIdTag = "AMBIG001",
                            Currency = "eur",
                            Status = PaymentReservationStatus.Authorized,
                            AwaitingPlug = true,
                            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-2),
                            UpdatedAtUtc = DateTime.UtcNow,
                            StartDeadlineAtUtc = DateTime.UtcNow.AddMinutes(10)
                        },
                        new ChargePaymentReservation
                        {
                            ReservationId = Guid.NewGuid(),
                            ChargePointId = "CP-AMBIG",
                            ConnectorId = 3,
                            ChargeTagId = "WEB-AMBIG-3",
                            OcppIdTag = "AMBIG003",
                            Currency = "eur",
                            Status = PaymentReservationStatus.Authorized,
                            AwaitingPlug = true,
                            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-1),
                            UpdatedAtUtc = DateTime.UtcNow,
                            StartDeadlineAtUtc = DateTime.UtcNow.AddMinutes(10)
                        });
                    setupContext.SaveChanges();
                }

                var middleware = CreateMiddleware(serviceProvider: CreateServiceProvider(databasePath));
                var fakeSocket = new FakeOpenWebSocket(async () =>
                {
                    Interlocked.Increment(ref sendCount);
                    var queuedMessage = GetQueuedRequest(middleware);
                    queuedMessage?.TaskCompletionSource?.SetResult("{\"status\":\"Accepted\"}");
                    await Task.CompletedTask;
                });

                var previousStatuses = ReplaceChargePointStatuses(new Dictionary<string, ChargePointStatus>
                {
                    ["CP-AMBIG"] = new ChargePointStatus
                    {
                        Id = "CP-AMBIG",
                        Protocol = "ocpp1.6",
                        WebSocket = fakeSocket,
                        OnlineConnectors = new Dictionary<int, OnlineConnectorStatus>
                        {
                            [2] = new OnlineConnectorStatus
                            {
                                Status = ConnectorStatusEnum.Preparing,
                                OcppStatus = "Preparing"
                            }
                        }
                    }
                });

                try
                {
                    middleware.NotifyConnectorPreparing("CP-AMBIG", 2);
                    await Task.Delay(500);

                    Assert.Equal(0, Volatile.Read(ref sendCount));

                    using var verificationContext = CreateContext(databasePath);
                    var reservations = verificationContext.ChargePaymentReservations
                        .Where(r => r.ChargePointId == "CP-AMBIG")
                        .OrderBy(r => r.ConnectorId)
                        .ToList();

                    Assert.Collection(
                        reservations,
                        reservation =>
                        {
                            Assert.Equal(1, reservation.ConnectorId);
                            Assert.Equal(PaymentReservationStatus.Authorized, reservation.Status);
                            Assert.True(reservation.AwaitingPlug);
                        },
                        reservation =>
                        {
                            Assert.Equal(3, reservation.ConnectorId);
                            Assert.Equal(PaymentReservationStatus.Authorized, reservation.Status);
                            Assert.True(reservation.AwaitingPlug);
                        });
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

        [Theory]
        [InlineData(10, false)]
        [InlineData(600, true)]
        public async Task NotifyConnectorPreparing_LeavesAReservationInsideTheRemoteStartWindowAwaitingThePlug(int secondsSinceRemoteStart, bool expectStart)
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-preparing-window-{Guid.NewGuid():N}.sqlite");
            int sendCount = 0;
            var reservationId = Guid.NewGuid();

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    setupContext.ChargePaymentReservations.Add(new ChargePaymentReservation
                    {
                        ReservationId = reservationId,
                        ChargePointId = "CP-WINDOW",
                        ConnectorId = 1,
                        ChargeTagId = "WEB-WINDOW",
                        OcppIdTag = "WINDOW01",
                        Currency = "eur",
                        Status = PaymentReservationStatus.Authorized,
                        AwaitingPlug = true,
                        // An earlier remote start ended with Timeout; the charger may still accept it late.
                        RemoteStartSentAtUtc = DateTime.UtcNow.AddSeconds(-secondsSinceRemoteStart),
                        RemoteStartResult = "Timeout",
                        CreatedAtUtc = DateTime.UtcNow.AddMinutes(-15),
                        UpdatedAtUtc = DateTime.UtcNow,
                        StartDeadlineAtUtc = DateTime.UtcNow.AddMinutes(10)
                    });
                    setupContext.SaveChanges();
                }

                var middleware = CreateMiddleware(serviceProvider: CreateServiceProvider(databasePath));
                var fakeSocket = new FakeOpenWebSocket(async () =>
                {
                    Interlocked.Increment(ref sendCount);
                    var queuedMessage = GetQueuedRequest(middleware);
                    queuedMessage?.TaskCompletionSource?.SetResult("{\"status\":\"Accepted\"}");
                    await Task.CompletedTask;
                });

                var previousStatuses = ReplaceChargePointStatuses(new Dictionary<string, ChargePointStatus>
                {
                    ["CP-WINDOW"] = new ChargePointStatus
                    {
                        Id = "CP-WINDOW",
                        Protocol = "ocpp1.6",
                        WebSocket = fakeSocket,
                        OnlineConnectors = new Dictionary<int, OnlineConnectorStatus>
                        {
                            [1] = new OnlineConnectorStatus
                            {
                                Status = ConnectorStatusEnum.Preparing,
                                OcppStatus = "Preparing"
                            }
                        }
                    }
                });

                try
                {
                    middleware.NotifyConnectorPreparing("CP-WINDOW", 1);
                    await Task.Delay(500);

                    using var verificationContext = CreateContext(databasePath);
                    var reservation = verificationContext.ChargePaymentReservations.Single(r => r.ReservationId == reservationId);
                    if (expectStart)
                    {
                        Assert.Equal(1, Volatile.Read(ref sendCount));
                        Assert.False(reservation.AwaitingPlug ?? false);
                    }
                    else
                    {
                        // Inside the window nothing is sent and the flag survives for a later Preparing notification.
                        Assert.Equal(0, Volatile.Read(ref sendCount));
                        Assert.True(reservation.AwaitingPlug);
                        Assert.Equal(PaymentReservationStatus.Authorized, reservation.Status);
                    }
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

        [Fact]
        public void GetConnectorStartability_IgnoresMeterOnlyUndefinedLiveStatus()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-startability-meter-only-{Guid.NewGuid():N}.sqlite");

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    setupContext.ChargePoints.Add(new ChargePoint
                    {
                        ChargePointId = "CP-METER-ONLY",
                        Name = "Meter-only startability test"
                    });
                    setupContext.ConnectorStatuses.Add(new ConnectorStatus
                    {
                        ChargePointId = "CP-METER-ONLY",
                        ConnectorId = 1,
                        LastStatus = "Available",
                        LastStatusTime = DateTime.UtcNow
                    });
                    setupContext.SaveChanges();
                }

                using var dbContext = CreateContext(databasePath);
                var middleware = CreateMiddleware();
                var chargePointStatus = new ChargePointStatus
                {
                    Id = "CP-METER-ONLY",
                    Protocol = "ocpp1.6",
                    WebSocket = new FakeOpenWebSocket(() => Task.CompletedTask),
                    OnlineConnectors = new Dictionary<int, OnlineConnectorStatus>
                    {
                        [1] = new OnlineConnectorStatus
                        {
                            Status = ConnectorStatusEnum.Undefined,
                            MeterKWH = 1534.933,
                            MeterValueDate = DateTimeOffset.UtcNow
                        }
                    }
                };

                var result = InvokeGetConnectorStartability(middleware, dbContext, "CP-METER-ONLY", 1, chargePointStatus, null);
                bool startable = Assert.IsType<bool>(result.GetType().GetProperty("Startable")!.GetValue(result));
                string reason = Assert.IsType<string>(result.GetType().GetProperty("Reason")!.GetValue(result));
                string persistedStatus = Assert.IsType<string>(result.GetType().GetProperty("PersistedStatus")!.GetValue(result));

                Assert.True(startable);
                Assert.Equal("Startable", reason);
                Assert.Equal("Available", persistedStatus);
                Assert.Null(result.GetType().GetProperty("LiveStatus")!.GetValue(result));
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Fact]
        public void IsConnectorBusy_IgnoresMeterOnlyUndefinedLiveStatus()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-busy-meter-only-{Guid.NewGuid():N}.sqlite");

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    setupContext.ChargePoints.Add(new ChargePoint
                    {
                        ChargePointId = "CP-BUSY-METER-ONLY",
                        Name = "Meter-only busy test"
                    });
                    setupContext.ConnectorStatuses.Add(new ConnectorStatus
                    {
                        ChargePointId = "CP-BUSY-METER-ONLY",
                        ConnectorId = 1,
                        LastStatus = "Available",
                        LastStatusTime = DateTime.UtcNow
                    });
                    setupContext.SaveChanges();
                }

                using var dbContext = CreateContext(databasePath);
                var middleware = CreateMiddleware();
                var chargePointStatus = new ChargePointStatus
                {
                    Id = "CP-BUSY-METER-ONLY",
                    Protocol = "ocpp1.6",
                    WebSocket = new FakeOpenWebSocket(() => Task.CompletedTask),
                    OnlineConnectors = new Dictionary<int, OnlineConnectorStatus>
                    {
                        [1] = new OnlineConnectorStatus
                        {
                            Status = ConnectorStatusEnum.Undefined,
                            MeterKWH = 1534.933,
                            MeterValueDate = DateTimeOffset.UtcNow
                        }
                    }
                };

                bool isBusy = InvokeIsConnectorBusy(middleware, dbContext, "CP-BUSY-METER-ONLY", 1, chargePointStatus, null, out var reason);

                Assert.False(isBusy);
                Assert.Equal("Available", reason);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Fact]
        public async Task HandlePaymentCreateAsync_UsesCaseInsensitiveChargePointLookup()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-create-case-{Guid.NewGuid():N}.sqlite");

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    setupContext.ChargePoints.Add(new ChargePoint
                    {
                        ChargePointId = "ns2571148979",
                        Name = "Huawei payment create",
                        FreeChargingEnabled = true
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
                    ["NS2571148979"] = new ChargePointStatus
                    {
                        Id = "NS2571148979",
                        Protocol = "ocpp1.6",
                        WebSocket = fakeSocket,
                        OnlineConnectors = new Dictionary<int, OnlineConnectorStatus>
                        {
                            [1] = new OnlineConnectorStatus
                            {
                                Status = ConnectorStatusEnum.Available,
                                OcppStatus = "Available"
                            }
                        }
                    }
                });

                try
                {
                    var httpContext = new DefaultHttpContext();
                    httpContext.Request.Method = "POST";
                    httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"chargePointId\":\"ns2571148979\",\"connectorId\":1,\"chargeTagId\":\"TAG-CASE\"}"));
                    httpContext.Response.Body = new MemoryStream();

                    using (var actionContext = CreateContext(databasePath))
                    {
                        await InvokeHandlePaymentCreateAsync(middleware, httpContext, actionContext);
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
        public async Task HandlePaymentCreateAsync_ReturnsBoundedBuyerValidationError()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-create-r1-validation-{Guid.NewGuid():N}.sqlite");

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    setupContext.ChargePoints.Add(new ChargePoint
                    {
                        ChargePointId = "CP-R1-VALIDATION",
                        Name = "R1 validation test",
                        MaxSessionKwh = 1,
                        PricePerKwh = 1m
                    });
                    setupContext.SaveChanges();
                }

                var middleware = CreateMiddleware(new BuyerValidationPaymentCoordinator());
                var previousStatuses = ReplaceChargePointStatuses(new Dictionary<string, ChargePointStatus>
                {
                    ["CP-R1-VALIDATION"] = new ChargePointStatus
                    {
                        Id = "CP-R1-VALIDATION",
                        Protocol = "ocpp1.6",
                        WebSocket = new FakeOpenWebSocket(() => Task.CompletedTask),
                        OnlineConnectors = new Dictionary<int, OnlineConnectorStatus>
                        {
                            [1] = new OnlineConnectorStatus
                            {
                                Status = ConnectorStatusEnum.Available,
                                OcppStatus = "Available"
                            }
                        }
                    }
                });

                try
                {
                    var httpContext = new DefaultHttpContext();
                    httpContext.Request.Method = "POST";
                    httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(
                        "{\"chargePointId\":\"CP-R1-VALIDATION\",\"connectorId\":1,\"chargeTagId\":\"TAG-R1\",\"requestR1Invoice\":true}"));
                    httpContext.Response.Body = new MemoryStream();

                    using (var actionContext = CreateContext(databasePath))
                    {
                        await InvokeHandlePaymentCreateAsync(middleware, httpContext, actionContext);
                    }

                    httpContext.Response.Body.Position = 0;
                    using var reader = new StreamReader(httpContext.Response.Body);
                    var payload = JObject.Parse(await reader.ReadToEndAsync());

                    Assert.Equal(StatusCodes.Status400BadRequest, httpContext.Response.StatusCode);
                    Assert.Equal("ConfirmationRequired", payload["status"]?.Value<string>());
                    Assert.Equal("BuyerDataConfirmed", payload["field"]?.Value<string>());
                    Assert.Equal("Confirm that invoice data is correct before saving.", payload["message"]?.Value<string>());
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
        public async Task HandlePaymentStopAsync_UsesCaseInsensitiveChargePointLookup()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-stop-case-{Guid.NewGuid():N}.sqlite");
            Guid reservationId = Guid.NewGuid();

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    setupContext.ChargePoints.Add(new ChargePoint
                    {
                        ChargePointId = "ns2571148979",
                        Name = "Huawei payment stop"
                    });

                    var transaction = new Transaction
                    {
                        ChargePointId = "ns2571148979",
                        ConnectorId = 1,
                        StartTagId = "TAG-STOP",
                        StartTime = DateTime.UtcNow.AddMinutes(-20),
                        MeterStart = 1.0
                    };

                    setupContext.Transactions.Add(transaction);
                    setupContext.SaveChanges();

                    setupContext.ChargePaymentReservations.Add(new ChargePaymentReservation
                    {
                        ReservationId = reservationId,
                        ChargePointId = "ns2571148979",
                        ConnectorId = 1,
                        ChargeTagId = "TAG-STOP",
                        OcppIdTag = "TAG-STOP",
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
                    ["NS2571148979"] = new ChargePointStatus
                    {
                        Id = "NS2571148979",
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
        public async Task Invoke_StartTransactionApi_UsesCaseInsensitiveChargePointLookup()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-start-api-case-{Guid.NewGuid():N}.sqlite");

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    setupContext.ChargeTags.Add(new ChargeTag
                    {
                        TagId = "TAG-API-START",
                        TagName = "API start",
                        Blocked = false
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
                    ["NS2571148979"] = new ChargePointStatus
                    {
                        Id = "NS2571148979",
                        Protocol = "ocpp1.6",
                        WebSocket = fakeSocket
                    }
                });

                try
                {
                    var httpContext = new DefaultHttpContext();
                    httpContext.Request.Method = "GET";
                    httpContext.Request.Path = "/API/StartTransaction/ns2571148979/1/TAG-API-START";
                    httpContext.Response.Body = new MemoryStream();

                    using (var actionContext = CreateContext(databasePath))
                    {
                        await middleware.Invoke(httpContext, actionContext);
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
        public async Task Invoke_StopTransactionApi_UsesCaseInsensitiveChargePointLookup()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-stop-api-case-{Guid.NewGuid():N}.sqlite");

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    setupContext.ChargePoints.Add(new ChargePoint
                    {
                        ChargePointId = "ns2571148979",
                        Name = "Huawei stop api"
                    });
                    setupContext.Transactions.Add(new Transaction
                    {
                        ChargePointId = "ns2571148979",
                        ConnectorId = 1,
                        StartTagId = "TAG-API-STOP",
                        StartTime = DateTime.UtcNow.AddMinutes(-20),
                        MeterStart = 1.0
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
                    ["NS2571148979"] = new ChargePointStatus
                    {
                        Id = "NS2571148979",
                        Protocol = "ocpp1.6",
                        WebSocket = fakeSocket
                    }
                });

                try
                {
                    var httpContext = new DefaultHttpContext();
                    httpContext.Request.Method = "GET";
                    httpContext.Request.Path = "/API/StopTransaction/ns2571148979/1";
                    httpContext.Response.Body = new MemoryStream();

                    using (var actionContext = CreateContext(databasePath))
                    {
                        await middleware.Invoke(httpContext, actionContext);
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

        [Theory]
        [InlineData("ocpp1.6", "/API/Reset/CP-RESET", "Soft")]
        [InlineData("ocpp1.6", "/API/Reset/CP-RESET/Soft", "Soft")]
        [InlineData("ocpp1.6", "/API/Reset/CP-RESET/Hard", "Hard")]
        [InlineData("ocpp1.6", "/API/Reset/CP-RESET/soft", "Soft")]
        [InlineData("ocpp1.6", "/API/Reset/CP-RESET/hArD", "Hard")]
        [InlineData("ocpp1.6", "/API/Reset/CP-RESET/   ", "Soft")]
        [InlineData("ocpp2.0.1", "/API/Reset/CP-RESET", "OnIdle")]
        [InlineData("ocpp2.0.1", "/API/Reset/CP-RESET/Soft", "OnIdle")]
        [InlineData("ocpp2.0.1", "/API/Reset/CP-RESET/sOfT", "OnIdle")]
        [InlineData("ocpp2.0.1", "/API/Reset/CP-RESET/Hard", "Immediate")]
        [InlineData("ocpp2.0.1", "/API/Reset/CP-RESET/haRD", "Immediate")]
        [InlineData("ocpp2.0.1", "/API/Reset/CP-RESET/   ", "OnIdle")]
        [InlineData("ocpp2.1", "/API/Reset/CP-RESET", "OnIdle")]
        [InlineData("ocpp2.1", "/API/Reset/CP-RESET/Soft", "OnIdle")]
        [InlineData("ocpp2.1", "/API/Reset/CP-RESET/soFT", "OnIdle")]
        [InlineData("ocpp2.1", "/API/Reset/CP-RESET/Hard", "Immediate")]
        [InlineData("ocpp2.1", "/API/Reset/CP-RESET/HarD", "Immediate")]
        [InlineData("ocpp2.1", "/API/Reset/CP-RESET/   ", "OnIdle")]
        public async Task Invoke_ResetApi_MapsRequestedModeToProtocolPayload(
            string protocol,
            string requestPath,
            string expectedResetType)
        {
            // Production mutation caught: wrong protocol enum, lost no-mode default, or lost explicit Soft handling.
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-reset-api-mode-{Guid.NewGuid():N}.sqlite");

            try
            {
                var middleware = CreateMiddleware();
                string? resetPayload = null;
                var fakeSocket = new FakeOpenWebSocket(async () =>
                {
                    var queuedMessage = GetQueuedRequest(middleware);
                    resetPayload = queuedMessage.JsonPayload;
                    Assert.NotNull(queuedMessage.TaskCompletionSource);
                    queuedMessage.TaskCompletionSource.SetResult("{\"status\":\"Accepted\"}");
                    await Task.CompletedTask;
                });

                var previousStatuses = ReplaceChargePointStatuses(new Dictionary<string, ChargePointStatus>
                {
                    ["CP-RESET"] = new ChargePointStatus
                    {
                        Id = "CP-RESET",
                        Protocol = protocol,
                        WebSocket = fakeSocket
                    }
                });

                try
                {
                    var httpContext = new DefaultHttpContext();
                    httpContext.Request.Method = "GET";
                    httpContext.Request.Path = requestPath;
                    httpContext.Response.Body = new MemoryStream();

                    using (var actionContext = CreateContext(databasePath))
                    {
                        await middleware.Invoke(httpContext, actionContext);
                    }

                    httpContext.Response.Body.Position = 0;
                    using var reader = new StreamReader(httpContext.Response.Body);
                    var payload = JObject.Parse(await reader.ReadToEndAsync());

                    Assert.Equal("Accepted", payload["status"]?.Value<string>());
                    Assert.NotNull(resetPayload);
                    Assert.Equal(expectedResetType, JObject.Parse(resetPayload!)["type"]?.Value<string>());
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
        public async Task Invoke_ResetApi_WithMalformedMode_ReturnsBadRequestWithoutSending()
        {
            // Production mutation caught: accepting an unknown reset mode or queueing after validation fails.
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-reset-api-invalid-{Guid.NewGuid():N}.sqlite");

            try
            {
                var middleware = CreateMiddleware();
                int sendCount = 0;
                var fakeSocket = new FakeOpenWebSocket(() =>
                {
                    Interlocked.Increment(ref sendCount);
                    var queuedMessage = GetQueuedRequest(middleware);
                    Assert.NotNull(queuedMessage.TaskCompletionSource);
                    queuedMessage.TaskCompletionSource.SetResult("{\"status\":\"Accepted\"}");
                    return Task.CompletedTask;
                });

                var previousStatuses = ReplaceChargePointStatuses(new Dictionary<string, ChargePointStatus>
                {
                    ["CP-RESET"] = new ChargePointStatus
                    {
                        Id = "CP-RESET",
                        Protocol = "ocpp1.6",
                        WebSocket = fakeSocket
                    }
                });

                try
                {
                    var httpContext = new DefaultHttpContext();
                    httpContext.Request.Method = "GET";
                    httpContext.Request.Path = "/API/Reset/CP-RESET/RestartNow";
                    httpContext.Response.Body = new MemoryStream();

                    using (var actionContext = CreateContext(databasePath))
                    {
                        await middleware.Invoke(httpContext, actionContext);
                    }

                    httpContext.Response.Body.Position = 0;
                    using var reader = new StreamReader(httpContext.Response.Body);
                    var responseBody = await reader.ReadToEndAsync();

                    Assert.Equal(StatusCodes.Status400BadRequest, httpContext.Response.StatusCode);
                    Assert.Equal("{\"status\":\"InvalidResetMode\"}", responseBody);
                    Assert.Equal(0, sendCount);
                    Assert.Equal(0, GetQueuedRequestCount(middleware));
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
        public async Task ExecuteRequestStartTransaction20_UsesConfiguredIdTokenType()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-start20-token-{Guid.NewGuid():N}.sqlite");

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    setupContext.ChargeTags.Add(new ChargeTag
                    {
                        TagId = "TAG20",
                        TagName = "TAG20",
                        Blocked = false
                    });
                    setupContext.SaveChanges();
                }

                var middleware = CreateMiddleware(configValues: new Dictionary<string, string?>
                {
                    ["Payments:RemoteStartIdTokenType"] = "Central"
                });

                var chargePointStatus = new ChargePointStatus
                {
                    Id = "CP20",
                    Protocol = "ocpp2.0.1",
                    WebSocket = new FakeOpenWebSocket(async () =>
                    {
                        var queuedMessage = GetQueuedRequest(middleware);
                        var payload = JObject.Parse(queuedMessage.JsonPayload);
                        Assert.Equal("Central", payload["idToken"]?["type"]?.Value<string>());
                        queuedMessage.TaskCompletionSource.SetResult("{\"status\":\"Accepted\"}");
                        await Task.CompletedTask;
                    })
                };

                using var actionContext = CreateContext(databasePath);
                string result = await InvokeExecuteRequestStartTransaction20Async(middleware, chargePointStatus, actionContext, "1", "TAG20");

                Assert.Equal("Accepted", JObject.Parse(result)["status"]?.Value<string>());
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Fact]
        public async Task ExecuteRequestStartTransaction21_UsesConfiguredIdTokenType()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-start21-token-{Guid.NewGuid():N}.sqlite");

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    setupContext.ChargeTags.Add(new ChargeTag
                    {
                        TagId = "TAG21",
                        TagName = "TAG21",
                        Blocked = false
                    });
                    setupContext.SaveChanges();
                }

                var middleware = CreateMiddleware(configValues: new Dictionary<string, string?>
                {
                    ["Payments:RemoteStartIdTokenType"] = "Local"
                });

                var chargePointStatus = new ChargePointStatus
                {
                    Id = "CP21",
                    Protocol = "ocpp2.1",
                    WebSocket = new FakeOpenWebSocket(async () =>
                    {
                        var queuedMessage = GetQueuedRequest(middleware);
                        var payload = JObject.Parse(queuedMessage.JsonPayload);
                        Assert.Equal("Local", payload["idToken"]?["type"]?.Value<string>());
                        queuedMessage.TaskCompletionSource.SetResult("{\"status\":\"Accepted\"}");
                        await Task.CompletedTask;
                    })
                };

                using var actionContext = CreateContext(databasePath);
                string result = await InvokeExecuteRequestStartTransaction21Async(middleware, chargePointStatus, actionContext, "1", "TAG21");

                Assert.Equal("Accepted", JObject.Parse(result)["status"]?.Value<string>());
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Fact]
        public void TryRemoveStatusIfSameInstance_RemovesCaseInsensitiveCurrentSession()
        {
            var candidateStatus = new ChargePointStatus
            {
                Id = "ns2571148979"
            };

            var statusDict = new ConcurrentDictionary<string, ChargePointStatus>(StringComparer.OrdinalIgnoreCase);
            statusDict["NS2571148979"] = candidateStatus;

            bool removed = InvokeTryRemoveStatusIfSameInstance(statusDict, candidateStatus);

            Assert.True(removed);
            Assert.Empty(statusDict);
        }

        [Fact]
        public async Task Invoke_PaymentWebhook_LegacyRoutePassesRawStripeRequestWithoutApiKey()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-webhook-legacy-{Guid.NewGuid():N}.sqlite");
            const string rawBody = "{\"id\":\"evt_test_legacy\",\"type\":\"checkout.session.completed\"}";

            try
            {
                var coordinator = new WebhookRecordingPaymentCoordinator();
                var middleware = CreateMiddleware(coordinator, new Dictionary<string, string?>
                {
                    ["ApiKey"] = "test-api-key"
                });

                var httpContext = CreateApiHttpContext("POST", "/API/Payments/Webhook", body: rawBody);
                httpContext.Request.Headers["Stripe-Signature"] = "t=1700000000,v1=test-signature";

                using (var actionContext = CreateContext(databasePath))
                {
                    await middleware.Invoke(httpContext, actionContext);
                }

                Assert.Equal(200, httpContext.Response.StatusCode);
                Assert.Equal(string.Empty, await ReadResponseBodyAsync(httpContext));

                var request = Assert.Single(coordinator.WebhookRequests);
                Assert.Null(request.Provider);
                Assert.Equal("POST", request.Method);
                Assert.Equal(rawBody, request.Body);
                Assert.True(request.Headers.TryGetValue("stripe-signature", out var signature));
                Assert.Equal("t=1700000000,v1=test-signature", signature);
                Assert.False(request.Headers.ContainsKey("X-API-Key"));

                // The API key is enforced for the other endpoints of the same API.
                var statusContext = CreateApiHttpContext("GET", "/API/Status");
                using (var actionContext = CreateContext(databasePath))
                {
                    await middleware.Invoke(statusContext, actionContext);
                }

                Assert.Equal(401, statusContext.Response.StatusCode);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Theory]
        [InlineData("GET")]
        [InlineData("POST")]
        public async Task Invoke_PaymentWebhook_SaferpayRoutePassesProviderMethodAndQuery(string method)
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-webhook-saferpay-{Guid.NewGuid():N}.sqlite");
            Guid reservationId = Guid.NewGuid();

            try
            {
                var coordinator = new WebhookRecordingPaymentCoordinator();
                var middleware = CreateMiddleware(coordinator, new Dictionary<string, string?>
                {
                    ["ApiKey"] = "test-api-key"
                });

                var httpContext = CreateApiHttpContext(
                    method,
                    "/API/Payments/Webhook/saferpay",
                    query: $"?reservationId={reservationId}&Outcome=success&sig=abc123&sig=ignored");

                using (var actionContext = CreateContext(databasePath))
                {
                    await middleware.Invoke(httpContext, actionContext);
                }

                Assert.Equal(200, httpContext.Response.StatusCode);

                var request = Assert.Single(coordinator.WebhookRequests);
                Assert.Equal("saferpay", request.Provider);
                Assert.Equal(method, request.Method);
                Assert.Equal(string.Empty, request.Body);
                Assert.Equal(3, request.Query.Count);
                Assert.Equal(reservationId.ToString(), request.Query["RESERVATIONID"]);
                Assert.Equal("success", request.Query["outcome"]);
                Assert.Equal("abc123", request.Query["sig"]);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Theory]
        [InlineData("/API/Payments/Webhook/", "POST", null)]
        [InlineData("/API/Payments/Webhook/stripe", "POST", "stripe")]
        [InlineData("/API/Payments/Webhook/Saferpay", "GET", "Saferpay")]
        [InlineData("/API/Payments/Webhook/saferpay/", "GET", "saferpay")]
        [InlineData("/API/Payments/Webhook/unknown-psp", "POST", "unknown-psp")]
        public async Task Invoke_PaymentWebhook_PassesProviderSegmentAsIs(string path, string method, string? expectedProvider)
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-webhook-segment-{Guid.NewGuid():N}.sqlite");

            try
            {
                var coordinator = new WebhookRecordingPaymentCoordinator();
                var middleware = CreateMiddleware(coordinator);
                var httpContext = CreateApiHttpContext(method, path, body: "{}");

                using (var actionContext = CreateContext(databasePath))
                {
                    await middleware.Invoke(httpContext, actionContext);
                }

                Assert.Equal(200, httpContext.Response.StatusCode);
                var request = Assert.Single(coordinator.WebhookRequests);
                Assert.Equal(expectedProvider, request.Provider);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Theory]
        [InlineData("/API/Payments/Webhook", "GET", 405)]
        [InlineData("/API/Payments/Webhook", "PUT", 405)]
        [InlineData("/API/Payments/Webhook/stripe", "GET", 405)]
        [InlineData("/API/Payments/Webhook/saferpay", "PUT", 405)]
        [InlineData("/API/Payments/Webhook/saferpay", "DELETE", 405)]
        [InlineData("/API/Payments/Webhook/saferpay/extra", "GET", 404)]
        public async Task Invoke_PaymentWebhook_RejectsUnsupportedMethodsAndPaths(string path, string method, int expectedStatusCode)
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-webhook-method-{Guid.NewGuid():N}.sqlite");

            try
            {
                var coordinator = new WebhookRecordingPaymentCoordinator();
                var middleware = CreateMiddleware(coordinator);
                var httpContext = CreateApiHttpContext(method, path, body: "{}");

                using (var actionContext = CreateContext(databasePath))
                {
                    await middleware.Invoke(httpContext, actionContext);
                }

                Assert.Equal(expectedStatusCode, httpContext.Response.StatusCode);
                Assert.Empty(coordinator.WebhookRequests);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Theory]
        [InlineData(400)]
        [InlineData(404)]
        [InlineData(503)]
        public async Task Invoke_PaymentWebhook_AnswersWithCoordinatorStatusCode(int statusCode)
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-webhook-status-{Guid.NewGuid():N}.sqlite");

            try
            {
                var coordinator = new WebhookRecordingPaymentCoordinator((_, _) => new PaymentWebhookResult
                {
                    StatusCode = statusCode
                });
                var middleware = CreateMiddleware(coordinator);
                var httpContext = CreateApiHttpContext("GET", "/API/Payments/Webhook/saferpay");

                using (var actionContext = CreateContext(databasePath))
                {
                    await middleware.Invoke(httpContext, actionContext);
                }

                Assert.Equal(statusCode, httpContext.Response.StatusCode);
                Assert.Equal(string.Empty, await ReadResponseBodyAsync(httpContext));
                Assert.Single(coordinator.WebhookRequests);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Fact]
        public async Task Invoke_PaymentWebhook_AnswersServerErrorWhenCoordinatorThrows()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-webhook-throw-{Guid.NewGuid():N}.sqlite");

            try
            {
                var coordinator = new WebhookRecordingPaymentCoordinator((_, _) =>
                    throw new InvalidOperationException("Simulated payment provider outage."));
                var middleware = CreateMiddleware(coordinator);
                var httpContext = CreateApiHttpContext("POST", "/API/Payments/Webhook", body: "{}");

                using (var actionContext = CreateContext(databasePath))
                {
                    await middleware.Invoke(httpContext, actionContext);
                }

                Assert.Equal(500, httpContext.Response.StatusCode);
                Assert.Equal(string.Empty, await ReadResponseBodyAsync(httpContext));
                Assert.Single(coordinator.WebhookRequests);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Fact]
        public async Task Invoke_PaymentWebhook_AutoStartsOnlyVerifiedAuthorizedReservations()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-webhook-autostart-{Guid.NewGuid():N}.sqlite");
            const string chargePointId = "CP-WEBHOOK-START";
            Guid authorizedId = Guid.NewGuid();
            Guid pendingId = Guid.NewGuid();
            Guid cancelledId = Guid.NewGuid();
            Guid missingId = Guid.NewGuid();

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    AddWebhookReservation(setupContext, authorizedId, chargePointId, 1, PaymentReservationStatus.Authorized, "PAYWHAUTH");
                    AddWebhookReservation(setupContext, pendingId, chargePointId, 2, PaymentReservationStatus.Pending, "PAYWHPEND");
                    AddWebhookReservation(setupContext, cancelledId, chargePointId, 3, PaymentReservationStatus.Cancelled, "PAYWHCANC");
                    setupContext.SaveChanges();
                }

                var coordinator = new WebhookRecordingPaymentCoordinator((_, _) => new PaymentWebhookResult
                {
                    Provider = "Saferpay",
                    AuthorizedReservationIds = new[] { authorizedId, pendingId, cancelledId, missingId, authorizedId }
                });
                var middleware = CreateMiddleware(coordinator);
                int sendCount = 0;
                var previousStatuses = ReplaceChargePointStatuses(new Dictionary<string, ChargePointStatus>
                {
                    [chargePointId] = CreatePreparingChargePoint(chargePointId, () =>
                    {
                        Interlocked.Increment(ref sendCount);
                        CompleteQueuedRequests(middleware, "{\"status\":\"Accepted\"}");
                        return Task.CompletedTask;
                    }, 1, 2, 3)
                });

                try
                {
                    var httpContext = CreateApiHttpContext("GET", "/API/Payments/Webhook/saferpay", query: $"?reservationId={authorizedId}");
                    using (var actionContext = CreateContext(databasePath))
                    {
                        await middleware.Invoke(httpContext, actionContext);
                    }

                    Assert.Equal(200, httpContext.Response.StatusCode);
                    Assert.Equal(1, sendCount);

                    using var verificationContext = CreateContext(databasePath);
                    var authorized = verificationContext.ChargePaymentReservations.Single(r => r.ReservationId == authorizedId);
                    Assert.Equal(PaymentReservationStatus.StartRequested, authorized.Status);
                    Assert.Equal("Accepted", authorized.RemoteStartResult);
                    Assert.NotNull(authorized.RemoteStartSentAtUtc);

                    var pending = verificationContext.ChargePaymentReservations.Single(r => r.ReservationId == pendingId);
                    Assert.Equal(PaymentReservationStatus.Pending, pending.Status);
                    Assert.Null(pending.RemoteStartSentAtUtc);
                    Assert.Null(pending.AwaitingPlug);

                    var cancelled = verificationContext.ChargePaymentReservations.Single(r => r.ReservationId == cancelledId);
                    Assert.Equal(PaymentReservationStatus.Cancelled, cancelled.Status);
                    Assert.Null(cancelled.RemoteStartSentAtUtc);
                    Assert.Null(cancelled.AwaitingPlug);
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
        public async Task Invoke_PaymentWebhook_ReloadsReservationStatusBeforeAutoStart()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-webhook-reload-{Guid.NewGuid():N}.sqlite");
            const string chargePointId = "CP-WEBHOOK-RELOAD";
            Guid cancelledMeanwhileId = Guid.NewGuid();
            Guid authorizedMeanwhileId = Guid.NewGuid();

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    AddWebhookReservation(setupContext, cancelledMeanwhileId, chargePointId, 1, PaymentReservationStatus.Authorized, "PAYWHRLD1");
                    AddWebhookReservation(setupContext, authorizedMeanwhileId, chargePointId, 2, PaymentReservationStatus.Pending, "PAYWHRLD2");
                    setupContext.SaveChanges();
                }

                var coordinator = new WebhookRecordingPaymentCoordinator((dbContext, _) =>
                {
                    // Leave stale tracked copies in the request context, then change both rows elsewhere.
                    Assert.Equal(PaymentReservationStatus.Authorized, dbContext.ChargePaymentReservations.Single(r => r.ReservationId == cancelledMeanwhileId).Status);
                    Assert.Equal(PaymentReservationStatus.Pending, dbContext.ChargePaymentReservations.Single(r => r.ReservationId == authorizedMeanwhileId).Status);

                    using (var otherContext = CreateContext(databasePath))
                    {
                        var cancelled = otherContext.ChargePaymentReservations.Single(r => r.ReservationId == cancelledMeanwhileId);
                        cancelled.Status = PaymentReservationStatus.Cancelled;
                        cancelled.UpdatedAtUtc = DateTime.UtcNow;

                        var authorized = otherContext.ChargePaymentReservations.Single(r => r.ReservationId == authorizedMeanwhileId);
                        authorized.Status = PaymentReservationStatus.Authorized;
                        authorized.AuthorizedAtUtc = DateTime.UtcNow;
                        authorized.UpdatedAtUtc = DateTime.UtcNow;
                        otherContext.SaveChanges();
                    }

                    return new PaymentWebhookResult
                    {
                        AuthorizedReservationIds = new[] { cancelledMeanwhileId, authorizedMeanwhileId }
                    };
                });
                var middleware = CreateMiddleware(coordinator);
                int sendCount = 0;
                var previousStatuses = ReplaceChargePointStatuses(new Dictionary<string, ChargePointStatus>
                {
                    [chargePointId] = CreatePreparingChargePoint(chargePointId, () =>
                    {
                        Interlocked.Increment(ref sendCount);
                        CompleteQueuedRequests(middleware, "{\"status\":\"Accepted\"}");
                        return Task.CompletedTask;
                    }, 1, 2)
                });

                try
                {
                    var httpContext = CreateApiHttpContext("POST", "/API/Payments/Webhook", body: "{}");
                    using (var actionContext = CreateContext(databasePath))
                    {
                        await middleware.Invoke(httpContext, actionContext);
                    }

                    Assert.Equal(200, httpContext.Response.StatusCode);
                    Assert.Equal(1, sendCount);

                    using var verificationContext = CreateContext(databasePath);
                    var cancelledReservation = verificationContext.ChargePaymentReservations.Single(r => r.ReservationId == cancelledMeanwhileId);
                    Assert.Equal(PaymentReservationStatus.Cancelled, cancelledReservation.Status);
                    Assert.Null(cancelledReservation.RemoteStartSentAtUtc);

                    var authorizedReservation = verificationContext.ChargePaymentReservations.Single(r => r.ReservationId == authorizedMeanwhileId);
                    Assert.Equal(PaymentReservationStatus.StartRequested, authorizedReservation.Status);
                    Assert.NotNull(authorizedReservation.RemoteStartSentAtUtc);
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
        public async Task Invoke_PaymentWebhook_ForgedStripePayloadDoesNotStartPendingReservation()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-webhook-forged-{Guid.NewGuid():N}.sqlite");
            const string chargePointId = "CP-WEBHOOK-FORGED";
            Guid pendingId = Guid.NewGuid();

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    // OCPP 1.6 reservations get their idTag only after authorization.
                    AddWebhookReservation(setupContext, pendingId, chargePointId, 1, PaymentReservationStatus.Pending, null);
                    setupContext.SaveChanges();
                }

                // Unsigned payload shaped like a Stripe checkout completion for someone else's pending checkout.
                string forgedPayload = new JObject
                {
                    ["id"] = "evt_forged",
                    ["type"] = "checkout.session.completed",
                    ["data"] = new JObject
                    {
                        ["object"] = new JObject
                        {
                            ["payment_status"] = "paid",
                            ["metadata"] = new JObject
                            {
                                ["reservation_id"] = pendingId.ToString()
                            }
                        }
                    }
                }.ToString();

                // Does not override HandleWebhookAsync: the default member bridges to HandleWebhookEvent and returns no ids.
                var coordinator = new LegacyWebhookPaymentCoordinator();
                var middleware = CreateMiddleware(coordinator);
                int sendCount = 0;
                var previousStatuses = ReplaceChargePointStatuses(new Dictionary<string, ChargePointStatus>
                {
                    [chargePointId] = CreatePreparingChargePoint(chargePointId, () =>
                    {
                        Interlocked.Increment(ref sendCount);
                        CompleteQueuedRequests(middleware, "{\"status\":\"Accepted\"}");
                        return Task.CompletedTask;
                    }, 1)
                });

                try
                {
                    var httpContext = CreateApiHttpContext("POST", "/API/Payments/Webhook", body: forgedPayload);
                    httpContext.Request.Headers["Stripe-Signature"] = "t=1700000000,v1=forged";

                    using (var actionContext = CreateContext(databasePath))
                    {
                        await middleware.Invoke(httpContext, actionContext);
                    }

                    Assert.Equal(200, httpContext.Response.StatusCode);
                    var webhookEvent = Assert.Single(coordinator.WebhookEvents);
                    Assert.Equal(forgedPayload, webhookEvent.Payload);
                    Assert.Equal("t=1700000000,v1=forged", webhookEvent.Signature);
                    Assert.Equal(0, sendCount);

                    using var verificationContext = CreateContext(databasePath);
                    var pending = verificationContext.ChargePaymentReservations.Single(r => r.ReservationId == pendingId);
                    Assert.Equal(PaymentReservationStatus.Pending, pending.Status);
                    Assert.Null(pending.AwaitingPlug);
                    Assert.Null(pending.RemoteStartSentAtUtc);
                    Assert.Null(pending.FailureCode);
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

        [Theory]
        [InlineData(null)]
        [InlineData("cs_test_confirm")]
        public async Task Invoke_PaymentConfirm_PassesOptionalCheckoutSessionIdToCoordinator(string? checkoutSessionId)
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-confirm-optional-{Guid.NewGuid():N}.sqlite");
            Guid reservationId = Guid.NewGuid();

            try
            {
                var coordinator = new WebhookRecordingPaymentCoordinator();
                var middleware = CreateMiddleware(coordinator);
                var body = new JObject { ["reservationId"] = reservationId.ToString() };
                if (checkoutSessionId != null)
                {
                    body["checkoutSessionId"] = checkoutSessionId;
                }

                var httpContext = CreateApiHttpContext("POST", "/API/Payments/Confirm", body: body.ToString());
                using (var actionContext = CreateContext(databasePath))
                {
                    await middleware.Invoke(httpContext, actionContext);
                }

                var call = Assert.Single(coordinator.ConfirmCalls);
                Assert.Equal(reservationId, call.ReservationId);
                Assert.Equal(checkoutSessionId, call.CheckoutSessionId);

                Assert.Equal(400, httpContext.Response.StatusCode);
                var payload = JObject.Parse(await ReadResponseBodyAsync(httpContext));
                Assert.Equal("Rejected", payload["status"]?.Value<string>());
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Fact]
        public async Task Invoke_PaymentConfirm_StillRequiresReservationId()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-confirm-missing-{Guid.NewGuid():N}.sqlite");

            try
            {
                var coordinator = new WebhookRecordingPaymentCoordinator();
                var middleware = CreateMiddleware(coordinator);
                var httpContext = CreateApiHttpContext("POST", "/API/Payments/Confirm", body: "{\"checkoutSessionId\":\"cs_test_confirm\"}");

                using (var actionContext = CreateContext(databasePath))
                {
                    await middleware.Invoke(httpContext, actionContext);
                }

                Assert.Equal(400, httpContext.Response.StatusCode);
                Assert.Empty(coordinator.ConfirmCalls);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Theory]
        [InlineData("Confirm", "POST", "Confirm")]
        [InlineData("Resume", "POST", "Resume")]
        [InlineData("Cancel", "POST", "Cancel")]
        [InlineData("RequestR1", "POST", "RequestR1")]
        [InlineData("Stop", "POST", null)]
        [InlineData("Status", "GET", null)]
        public async Task Invoke_Payments_ExistingReservationActionsGateOnIsOperational(string action, string method, string? expectedCall)
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-payments-operational-{Guid.NewGuid():N}.sqlite");
            Guid reservationId = Guid.NewGuid();

            try
            {
                // The active provider is not configured (no new checkouts), but a previous provider still is.
                var coordinator = new GatedPaymentCoordinator(isEnabled: false, isOperational: true);
                var middleware = CreateMiddleware(coordinator);
                var httpContext = CreateApiHttpContext(
                    method,
                    $"/API/Payments/{action}",
                    query: HttpMethods.IsGet(method) ? $"?reservationId={reservationId}" : null,
                    body: HttpMethods.IsGet(method) ? null : new JObject { ["reservationId"] = reservationId.ToString() }.ToString());

                using (var actionContext = CreateContext(databasePath))
                {
                    await middleware.Invoke(httpContext, actionContext);
                }

                var payload = JObject.Parse(await ReadResponseBodyAsync(httpContext));
                Assert.NotEqual("Disabled", payload["status"]?.Value<string>());
                if (expectedCall == null)
                {
                    // Stop and Status read the reservation themselves; the unknown id answers NotFound.
                    Assert.Empty(coordinator.Calls);
                    Assert.Equal(404, httpContext.Response.StatusCode);
                    Assert.Equal("NotFound", payload["status"]?.Value<string>());
                }
                else
                {
                    Assert.Equal(new[] { expectedCall }, coordinator.Calls);
                }
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Theory]
        [InlineData("Create", false, true)]
        [InlineData("Create", false, false)]
        [InlineData("Confirm", false, false)]
        [InlineData("Resume", false, false)]
        [InlineData("Cancel", false, false)]
        [InlineData("Stop", false, false)]
        [InlineData("RequestR1", false, false)]
        public async Task Invoke_Payments_AnswersDisabledWhenTheGateIsClosed(string action, bool isEnabled, bool isOperational)
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-payments-disabled-{Guid.NewGuid():N}.sqlite");

            try
            {
                var coordinator = new GatedPaymentCoordinator(isEnabled, isOperational);
                var middleware = CreateMiddleware(coordinator);
                var httpContext = CreateApiHttpContext(
                    "POST",
                    $"/API/Payments/{action}",
                    body: new JObject
                    {
                        ["reservationId"] = Guid.NewGuid().ToString(),
                        ["chargePointId"] = "CP-DISABLED",
                        ["chargeTagId"] = "TAG-DISABLED"
                    }.ToString());

                using (var actionContext = CreateContext(databasePath))
                {
                    await middleware.Invoke(httpContext, actionContext);
                }

                Assert.Equal(400, httpContext.Response.StatusCode);
                var payload = JObject.Parse(await ReadResponseBodyAsync(httpContext));
                Assert.Equal("Disabled", payload["status"]?.Value<string>());
                Assert.Empty(coordinator.Calls);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Theory]
        [InlineData("/API/Payments/Webhook/prod")]
        [InlineData("/API/Payments/Webhook/stripe/v1")]
        [InlineData("/API/Payments/Webhook/prod/extra/")]
        public async Task Invoke_PaymentWebhook_SignedStripeDeliveryToDeprecatedPathUsesLegacyRoute(string path)
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-webhook-deprecated-{Guid.NewGuid():N}.sqlite");
            const string rawBody = "{\"id\":\"evt_test_deprecated\",\"type\":\"checkout.session.completed\"}";

            try
            {
                var logs = new RecordingLoggerProvider();
                var coordinator = new WebhookRecordingPaymentCoordinator();
                var middleware = CreateMiddleware(
                    coordinator,
                    new Dictionary<string, string?> { ["ApiKey"] = "test-api-key" },
                    loggerFactory: new LoggerFactory(new[] { logs }));

                for (int delivery = 0; delivery < 2; delivery++)
                {
                    var httpContext = CreateApiHttpContext("POST", path, body: rawBody);
                    httpContext.Request.Headers["Stripe-Signature"] = "t=1700000000,v1=test-signature";

                    using (var actionContext = CreateContext(databasePath))
                    {
                        await middleware.Invoke(httpContext, actionContext);
                    }

                    Assert.Equal(200, httpContext.Response.StatusCode);
                }

                Assert.Equal(2, coordinator.WebhookRequests.Count);
                Assert.All(coordinator.WebhookRequests, request =>
                {
                    Assert.Null(request.Provider);
                    Assert.Equal("POST", request.Method);
                    Assert.Equal(rawBody, request.Body);
                    Assert.Equal("t=1700000000,v1=test-signature", request.Headers["Stripe-Signature"]);
                });

                // One deprecation warning per process, naming the path; later deliveries do not repeat it.
                var warning = Assert.Single(logs.Entries, entry =>
                    entry.Level == LogLevel.Warning &&
                    entry.Message.Contains("Deprecated Stripe webhook path", StringComparison.Ordinal));
                Assert.Contains(path, warning.Message);
                Assert.Contains("/API/Payments/Webhook/Stripe", warning.Message);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Theory]
        [InlineData("/API/Payments/Webhook/prod/extra", "POST", false)]
        [InlineData("/API/Payments/Webhook/saferpay/extra", "GET", true)]
        [InlineData("/API/Payments/Webhook/stripe/v1", "PUT", true)]
        public async Task Invoke_PaymentWebhook_DeeperPathWithoutSignedStripePostStaysNotFound(string path, string method, bool withStripeSignature)
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-webhook-deeper-{Guid.NewGuid():N}.sqlite");

            try
            {
                var coordinator = new WebhookRecordingPaymentCoordinator();
                var middleware = CreateMiddleware(coordinator);
                var httpContext = CreateApiHttpContext(method, path, body: "{}");
                if (withStripeSignature)
                {
                    httpContext.Request.Headers["Stripe-Signature"] = "t=1700000000,v1=test-signature";
                }

                using (var actionContext = CreateContext(databasePath))
                {
                    await middleware.Invoke(httpContext, actionContext);
                }

                Assert.Equal(404, httpContext.Response.StatusCode);
                Assert.Empty(coordinator.WebhookRequests);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Theory]
        [InlineData("/API/Payments/Webhook/Saferpay", "Saferpay")]
        [InlineData("/API/Payments/Webhook/stripe", "stripe")]
        public async Task Invoke_PaymentWebhook_StripeSignatureDoesNotRerouteKnownProviderSegments(string path, string expectedProvider)
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-webhook-known-{Guid.NewGuid():N}.sqlite");

            try
            {
                var coordinator = new WebhookRecordingPaymentCoordinator();
                var middleware = CreateMiddleware(coordinator);
                var httpContext = CreateApiHttpContext("POST", path, body: "{}");
                httpContext.Request.Headers["Stripe-Signature"] = "t=1700000000,v1=test-signature";

                using (var actionContext = CreateContext(databasePath))
                {
                    await middleware.Invoke(httpContext, actionContext);
                }

                Assert.Equal(200, httpContext.Response.StatusCode);
                var request = Assert.Single(coordinator.WebhookRequests);
                Assert.Equal(expectedProvider, request.Provider);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Fact]
        public async Task Invoke_PaymentWebhook_AcknowledgesProviderBeforeAutoStart()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-webhook-ack-{Guid.NewGuid():N}.sqlite");
            const string chargePointId = "CP-WEBHOOK-ACK";
            Guid authorizedId = Guid.NewGuid();

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    AddWebhookReservation(setupContext, authorizedId, chargePointId, 1, PaymentReservationStatus.Authorized, "PAYWHACK");
                    setupContext.SaveChanges();
                }

                // A non-default status shows that the answer was set before the charger was contacted.
                var coordinator = new WebhookRecordingPaymentCoordinator((_, _) => new PaymentWebhookResult
                {
                    StatusCode = 202,
                    Provider = "Saferpay",
                    AuthorizedReservationIds = new[] { authorizedId }
                });
                var middleware = CreateMiddleware(coordinator);
                var httpContext = CreateApiHttpContext("GET", "/API/Payments/Webhook/saferpay", query: $"?reservationId={authorizedId}");
                var responseBody = new CompletionRecordingResponseBodyFeature(httpContext.Response.Body);
                httpContext.Features.Set<IHttpResponseBodyFeature>(responseBody);

                int sendCount = 0;
                bool? completedAtRemoteStart = null;
                int? statusAtRemoteStart = null;
                var previousStatuses = ReplaceChargePointStatuses(new Dictionary<string, ChargePointStatus>
                {
                    [chargePointId] = CreatePreparingChargePoint(chargePointId, () =>
                    {
                        Interlocked.Increment(ref sendCount);
                        completedAtRemoteStart = responseBody.Completed;
                        statusAtRemoteStart = httpContext.Response.StatusCode;
                        CompleteQueuedRequests(middleware, "{\"status\":\"Accepted\"}");
                        return Task.CompletedTask;
                    }, 1)
                });

                try
                {
                    using (var actionContext = CreateContext(databasePath))
                    {
                        await middleware.Invoke(httpContext, actionContext);
                    }

                    Assert.Equal(1, sendCount);
                    Assert.True(completedAtRemoteStart);
                    Assert.Equal(202, statusAtRemoteStart);
                    Assert.Equal(202, httpContext.Response.StatusCode);

                    using var verificationContext = CreateContext(databasePath);
                    var reservation = verificationContext.ChargePaymentReservations.Single(r => r.ReservationId == authorizedId);
                    Assert.Equal(PaymentReservationStatus.StartRequested, reservation.Status);
                    Assert.Equal("Accepted", reservation.RemoteStartResult);
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
        public async Task Invoke_PaymentResume_StartsReservationThatResumeConfirmed()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-resume-start-{Guid.NewGuid():N}.sqlite");
            const string chargePointId = "CP-RESUME-START";
            Guid reservationId = Guid.NewGuid();

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    AddWebhookReservation(setupContext, reservationId, chargePointId, 1, PaymentReservationStatus.Pending, "PAYRESUME1");
                    setupContext.SaveChanges();
                }

                // The browser return was lost and no notification arrived: Resume finds the page paid and confirms it.
                var coordinator = new StartTestPaymentCoordinator
                {
                    Resume = (dbContext, id) =>
                    {
                        var reservation = dbContext.ChargePaymentReservations.Single(r => r.ReservationId == id);
                        reservation.Status = PaymentReservationStatus.Authorized;
                        reservation.AuthorizedAtUtc = DateTime.UtcNow;
                        reservation.StartDeadlineAtUtc = DateTime.UtcNow.AddMinutes(5);
                        reservation.UpdatedAtUtc = DateTime.UtcNow;
                        dbContext.SaveChanges();
                        return new PaymentResumeResult { Success = true, Status = "Status", Reservation = reservation };
                    }
                };
                var middleware = CreateMiddleware(coordinator);
                var httpContext = CreateApiHttpContext("POST", "/API/Payments/Resume", body: new JObject { ["reservationId"] = reservationId.ToString() }.ToString());
                var responseBody = new CompletionRecordingResponseBodyFeature(httpContext.Response.Body);
                httpContext.Features.Set<IHttpResponseBodyFeature>(responseBody);

                int sendCount = 0;
                bool? completedAtRemoteStart = null;
                var previousStatuses = ReplaceChargePointStatuses(new Dictionary<string, ChargePointStatus>
                {
                    [chargePointId] = CreatePreparingChargePoint(chargePointId, () =>
                    {
                        Interlocked.Increment(ref sendCount);
                        completedAtRemoteStart = responseBody.Completed;
                        CompleteQueuedRequests(middleware, "{\"status\":\"Accepted\"}");
                        return Task.CompletedTask;
                    }, 1)
                });

                try
                {
                    using (var actionContext = CreateContext(databasePath))
                    {
                        await middleware.Invoke(httpContext, actionContext);
                    }

                    // The answer keeps its shape and is sent before the charger is contacted.
                    Assert.Equal(200, httpContext.Response.StatusCode);
                    var payload = JObject.Parse(await ReadResponseBodyAsync(httpContext));
                    Assert.Equal("Status", payload["status"]?.Value<string>());
                    Assert.Equal(PaymentReservationStatus.Authorized, payload["reservationStatus"]?.Value<string>());
                    Assert.Equal(1, sendCount);
                    Assert.True(completedAtRemoteStart);

                    using var verificationContext = CreateContext(databasePath);
                    var persisted = verificationContext.ChargePaymentReservations.Single(r => r.ReservationId == reservationId);
                    Assert.Equal(PaymentReservationStatus.StartRequested, persisted.Status);
                    Assert.Equal("Accepted", persisted.RemoteStartResult);
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
        public async Task Invoke_PaymentResume_StartsAPaidSaferpayCheckoutThroughRealServices()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-resume-saferpay-{Guid.NewGuid():N}.sqlite");
            const string chargePointId = "CP-RESUME-SAFERPAY";
            var settings = new Dictionary<string, string?>
            {
                ["ConnectionStrings:SQLite"] = $"Filename={databasePath}",
                ["Notifications:EnableCustomerEmails"] = "false",
                ["Invoices:Enabled"] = "false",
                ["Payments:Provider"] = "Saferpay",
                ["Payments:ReturnBaseUrl"] = "https://return.example.com",
                ["Saferpay:Enabled"] = "true",
                ["Saferpay:UseMockServices"] = "true"
            };
            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            var services = new ServiceCollection();
            services.AddSingleton(configuration);
            new Startup(configuration).ConfigureServices(services);
            var provider = services.BuildServiceProvider();

            try
            {
                var coordinator = provider.GetRequiredService<IPaymentCoordinator>();
                Guid reservationId;
                using (var setupScope = provider.CreateScope())
                {
                    var db = setupScope.ServiceProvider.GetRequiredService<OCPPCoreContext>();
                    db.Database.EnsureCreated();
                    db.ChargePoints.Add(new ChargePoint
                    {
                        ChargePointId = chargePointId,
                        Name = "Resume charger",
                        MaxSessionKwh = 10,
                        PricePerKwh = 0.40m,
                        UserSessionFee = 0.50m
                    });
                    db.ChargeTags.Add(new ChargeTag { TagId = "TAG-RESUME-SAFERPAY", TagName = "Resume tag" });
                    db.SaveChanges();

                    // The payer pays the mock payment page, but neither the browser return nor a notification arrives.
                    var created = await coordinator.CreateCheckoutSessionAsync(
                        db,
                        new PaymentSessionRequest { ChargePointId = chargePointId, ConnectorId = 1, ChargeTagId = "TAG-RESUME-SAFERPAY", Origin = "public" },
                        CancellationToken.None);
                    reservationId = created.Reservation.ReservationId;
                    Assert.Equal(PaymentReservationStatus.Pending, created.Reservation.Status);
                }

                var middleware = CreateMiddleware(coordinator, serviceProvider: provider);
                int sendCount = 0;
                var previousStatuses = ReplaceChargePointStatuses(new Dictionary<string, ChargePointStatus>
                {
                    [chargePointId] = CreatePreparingChargePoint(chargePointId, () =>
                    {
                        Interlocked.Increment(ref sendCount);
                        CompleteQueuedRequests(middleware, "{\"status\":\"Accepted\"}");
                        return Task.CompletedTask;
                    }, 1)
                });

                try
                {
                    // The payer reopens the start page, which resumes the reservation from its recovery cookie.
                    var httpContext = CreateApiHttpContext("POST", "/API/Payments/Resume", body: new JObject { ["reservationId"] = reservationId.ToString() }.ToString());
                    using (var requestScope = provider.CreateScope())
                    {
                        await middleware.Invoke(httpContext, requestScope.ServiceProvider.GetRequiredService<OCPPCoreContext>());
                    }

                    var payload = JObject.Parse(await ReadResponseBodyAsync(httpContext));
                    Assert.Equal("Status", payload["status"]?.Value<string>());
                    Assert.Equal(1, sendCount);

                    using var verificationScope = provider.CreateScope();
                    var verificationDb = verificationScope.ServiceProvider.GetRequiredService<OCPPCoreContext>();
                    var persisted = verificationDb.ChargePaymentReservations.AsNoTracking().Single(r => r.ReservationId == reservationId);
                    Assert.Equal(PaymentReservationStatus.StartRequested, persisted.Status);
                    Assert.Equal(PaymentProviderNames.Saferpay, persisted.PaymentProvider);
                    Assert.False(string.IsNullOrWhiteSpace(persisted.OcppIdTag));
                    Assert.Equal("Accepted", persisted.RemoteStartResult);
                }
                finally
                {
                    ReplaceChargePointStatuses(previousStatuses);
                }
            }
            finally
            {
                provider.Dispose();
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                TryDelete(databasePath);
                TryDelete(databasePath + "-wal");
                TryDelete(databasePath + "-shm");
            }
        }

        [Theory]
        [InlineData("Redirect", PaymentReservationStatus.Authorized)]
        [InlineData("ResumeUnavailable", PaymentReservationStatus.Authorized)]
        [InlineData("Status", PaymentReservationStatus.Pending)]
        [InlineData("Status", PaymentReservationStatus.Cancelled)]
        public async Task Invoke_PaymentResume_StartsOnlyAnAuthorizedReservationOnStatusAnswer(string resumeStatus, string storedStatus)
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-resume-nostart-{Guid.NewGuid():N}.sqlite");
            const string chargePointId = "CP-RESUME-NOSTART";
            Guid reservationId = Guid.NewGuid();

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    AddWebhookReservation(setupContext, reservationId, chargePointId, 1, storedStatus, "PAYRESUME2");
                    setupContext.SaveChanges();
                }

                var coordinator = new StartTestPaymentCoordinator
                {
                    Resume = (dbContext, id) => new PaymentResumeResult
                    {
                        Success = true,
                        Status = resumeStatus,
                        Reservation = dbContext.ChargePaymentReservations.Single(r => r.ReservationId == id)
                    }
                };
                var middleware = CreateMiddleware(coordinator);
                int sendCount = 0;
                var previousStatuses = ReplaceChargePointStatuses(new Dictionary<string, ChargePointStatus>
                {
                    [chargePointId] = CreatePreparingChargePoint(chargePointId, () =>
                    {
                        Interlocked.Increment(ref sendCount);
                        CompleteQueuedRequests(middleware, "{\"status\":\"Accepted\"}");
                        return Task.CompletedTask;
                    }, 1)
                });

                try
                {
                    var httpContext = CreateApiHttpContext("POST", "/API/Payments/Resume", body: new JObject { ["reservationId"] = reservationId.ToString() }.ToString());
                    using (var actionContext = CreateContext(databasePath))
                    {
                        await middleware.Invoke(httpContext, actionContext);
                    }

                    var payload = JObject.Parse(await ReadResponseBodyAsync(httpContext));
                    Assert.Equal(resumeStatus, payload["status"]?.Value<string>());
                    Assert.Equal(0, sendCount);

                    using var verificationContext = CreateContext(databasePath);
                    var persisted = verificationContext.ChargePaymentReservations.Single(r => r.ReservationId == reservationId);
                    Assert.Equal(storedStatus, persisted.Status);
                    Assert.Null(persisted.RemoteStartSentAtUtc);
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

        [Theory]
        [InlineData(5, 0)]
        [InlineData(120, 1)]
        public async Task Invoke_PaymentConfirm_SkipsStartWithinChargerTimeoutOfLastRemoteStart(int secondsSinceLastStart, int expectedSends)
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-confirm-recent-start-{Guid.NewGuid():N}.sqlite");
            const string chargePointId = "CP-CONFIRM-RECENT";
            Guid reservationId = Guid.NewGuid();

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    // The previous remote start got no answer in time (Timeout keeps the reservation Authorized).
                    AddWebhookReservation(setupContext, reservationId, chargePointId, 1, PaymentReservationStatus.Authorized, "PAYRECENT1");
                    var reservation = setupContext.ChargePaymentReservations.Local.Single(r => r.ReservationId == reservationId);
                    reservation.RemoteStartSentAtUtc = DateTime.UtcNow.AddSeconds(-secondsSinceLastStart);
                    reservation.RemoteStartResult = "Timeout";
                    reservation.AwaitingPlug = false;
                    setupContext.SaveChanges();
                }

                var coordinator = new StartTestPaymentCoordinator { Confirm = ConfirmStoredReservation };
                // Payments:ChargerResponseTimeoutMs is not set, so the default 60-second charger timeout applies.
                var middleware = CreateMiddleware(coordinator);
                int sendCount = 0;
                var previousStatuses = ReplaceChargePointStatuses(new Dictionary<string, ChargePointStatus>
                {
                    [chargePointId] = CreatePreparingChargePoint(chargePointId, () =>
                    {
                        Interlocked.Increment(ref sendCount);
                        CompleteQueuedRequests(middleware, "{\"status\":\"Accepted\"}");
                        return Task.CompletedTask;
                    }, 1)
                });

                try
                {
                    var httpContext = CreateApiHttpContext("POST", "/API/Payments/Confirm", body: new JObject { ["reservationId"] = reservationId.ToString() }.ToString());
                    using (var actionContext = CreateContext(databasePath))
                    {
                        await middleware.Invoke(httpContext, actionContext);
                    }

                    Assert.Equal(200, httpContext.Response.StatusCode);
                    Assert.Equal(expectedSends, sendCount);
                    var payload = JObject.Parse(await ReadResponseBodyAsync(httpContext));

                    using var verificationContext = CreateContext(databasePath);
                    var persisted = verificationContext.ChargePaymentReservations.Single(r => r.ReservationId == reservationId);
                    if (expectedSends == 0)
                    {
                        Assert.Equal(PaymentReservationStatus.Authorized, payload["status"]?.Value<string>());
                        Assert.Equal("RecentRemoteStart", payload["reason"]?.Value<string>());
                        Assert.Equal(PaymentReservationStatus.Authorized, persisted.Status);
                        Assert.Equal("Timeout", persisted.RemoteStartResult);
                    }
                    else
                    {
                        Assert.Equal(PaymentReservationStatus.StartRequested, payload["status"]?.Value<string>());
                        Assert.Equal(PaymentReservationStatus.StartRequested, persisted.Status);
                        Assert.Equal("Accepted", persisted.RemoteStartResult);
                    }
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
        public async Task Invoke_PaymentConfirm_ParallelConfirmsSendOneRemoteStart()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-confirm-parallel-{Guid.NewGuid():N}.sqlite");
            const string chargePointId = "CP-CONFIRM-PARALLEL";
            Guid reservationId = Guid.NewGuid();

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    AddWebhookReservation(setupContext, reservationId, chargePointId, 1, PaymentReservationStatus.Authorized, "PAYPARALLEL");
                    setupContext.SaveChanges();
                }

                var coordinator = new StartTestPaymentCoordinator { Confirm = ConfirmStoredReservation };
                var middleware = CreateMiddleware(coordinator);
                string body = new JObject { ["reservationId"] = reservationId.ToString() }.ToString();
                int sendCount = 0;
                JObject? secondPayload = null;
                var previousStatuses = ReplaceChargePointStatuses(new Dictionary<string, ChargePointStatus>
                {
                    [chargePointId] = CreatePreparingChargePoint(chargePointId, async () =>
                    {
                        if (Interlocked.Increment(ref sendCount) == 1)
                        {
                            // A second return (another tab, or a replayed Confirm) arrives while the charger has not
                            // answered the first remote start yet.
                            var secondContext = CreateApiHttpContext("POST", "/API/Payments/Confirm", body: body);
                            using (var secondDbContext = CreateContext(databasePath))
                            {
                                await middleware.Invoke(secondContext, secondDbContext);
                            }

                            secondPayload = JObject.Parse(await ReadResponseBodyAsync(secondContext));
                        }

                        CompleteQueuedRequests(middleware, "{\"status\":\"Accepted\"}");
                    }, 1)
                });

                try
                {
                    var httpContext = CreateApiHttpContext("POST", "/API/Payments/Confirm", body: body);
                    using (var actionContext = CreateContext(databasePath))
                    {
                        await middleware.Invoke(httpContext, actionContext);
                    }

                    Assert.Equal(1, sendCount);
                    Assert.NotNull(secondPayload);
                    Assert.Equal("StartInProgress", secondPayload!["reason"]?.Value<string>());
                    var payload = JObject.Parse(await ReadResponseBodyAsync(httpContext));
                    Assert.Equal(PaymentReservationStatus.StartRequested, payload["status"]?.Value<string>());
                    Assert.Empty(coordinator.CancelReasons);

                    using var verificationContext = CreateContext(databasePath);
                    var persisted = verificationContext.ChargePaymentReservations.Single(r => r.ReservationId == reservationId);
                    Assert.Equal(PaymentReservationStatus.StartRequested, persisted.Status);
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

        [Theory]
        [InlineData(PaymentReservationStatus.StartRequested, "Accepted", 5, null, "AlreadyRequested")]
        [InlineData(PaymentReservationStatus.Authorized, "Timeout", 5, null, "RecentRemoteStart")]
        [InlineData(PaymentReservationStatus.Authorized, null, null, 4711, "AlreadyAdvanced")]
        public async Task TryStartChargingAsync_RereadsTheReservationBeforeSending_WhenAnotherStartFinishedMeanwhile(
            string committedStatus,
            string? committedRemoteStartResult,
            int? secondsSinceCommittedRemoteStart,
            int? committedTransactionId,
            string expectedReason)
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-start-reread-{Guid.NewGuid():N}.sqlite");
            const string chargePointId = "CP-START-REREAD";
            Guid reservationId = Guid.NewGuid();

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    AddWebhookReservation(setupContext, reservationId, chargePointId, 1, PaymentReservationStatus.Authorized, "PAYREREAD1");
                    setupContext.ChargePaymentReservations.Local.Single(r => r.ReservationId == reservationId).AwaitingPlug = false;
                    setupContext.SaveChanges();
                }

                var middleware = CreateMiddleware(new StartTestPaymentCoordinator());
                int sendCount = 0;
                var previousStatuses = ReplaceChargePointStatuses(new Dictionary<string, ChargePointStatus>
                {
                    [chargePointId] = CreatePreparingChargePoint(chargePointId, () =>
                    {
                        Interlocked.Increment(ref sendCount);
                        CompleteQueuedRequests(middleware, "{\"status\":\"Accepted\"}");
                        return Task.CompletedTask;
                    }, 1)
                });

                try
                {
                    using var actionContext = CreateContext(databasePath);
                    // Trigger B read the reservation (Authorized, no remote start yet) and passed its checks...
                    var staleReservation = actionContext.ChargePaymentReservations.Single(r => r.ReservationId == reservationId);

                    // ...while trigger A's remote start finished and released the in-flight guard.
                    using (var otherTriggerContext = CreateContext(databasePath))
                    {
                        var committed = otherTriggerContext.ChargePaymentReservations.Single(r => r.ReservationId == reservationId);
                        committed.Status = committedStatus;
                        committed.RemoteStartResult = committedRemoteStartResult;
                        committed.RemoteStartSentAtUtc = secondsSinceCommittedRemoteStart.HasValue
                            ? DateTime.UtcNow.AddSeconds(-secondsSinceCommittedRemoteStart.Value)
                            : null;
                        committed.TransactionId = committedTransactionId;
                        committed.UpdatedAtUtc = DateTime.UtcNow;
                        otherTriggerContext.SaveChanges();
                    }

                    var result = await InvokeTryStartChargingAsync(middleware, actionContext, staleReservation, "Test");

                    Assert.Equal(0, sendCount);
                    Assert.Equal(committedStatus, result.Status);
                    Assert.Equal(expectedReason, result.Reason);

                    using var verificationContext = CreateContext(databasePath);
                    var persisted = verificationContext.ChargePaymentReservations.Single(r => r.ReservationId == reservationId);
                    Assert.Equal(committedStatus, persisted.Status);
                    Assert.Equal(committedRemoteStartResult, persisted.RemoteStartResult);
                    Assert.Equal(committedTransactionId, persisted.TransactionId);
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

        [Theory]
        [InlineData(PaymentReservationStatus.StartRequested, false, ConnectorStatusEnum.Preparing, 0, "AlreadyRequested")]
        [InlineData(PaymentReservationStatus.Charging, false, ConnectorStatusEnum.Preparing, 0, "AlreadyRequested")]
        [InlineData(PaymentReservationStatus.Authorized, true, ConnectorStatusEnum.Available, 0, "AwaitingPlug")]
        [InlineData(PaymentReservationStatus.Authorized, true, ConnectorStatusEnum.Preparing, 1, "Accepted")]
        public async Task Invoke_PaymentConfirm_StartsOnlyAnAuthorizedReservationWithoutAStartUnderWay(
            string storedStatus,
            bool awaitingPlug,
            ConnectorStatusEnum connectorStatus,
            int expectedSends,
            string expectedReason)
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-confirm-guard-{Guid.NewGuid():N}.sqlite");
            const string chargePointId = "CP-CONFIRM-GUARD";
            Guid reservationId = Guid.NewGuid();

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    AddWebhookReservation(setupContext, reservationId, chargePointId, 1, storedStatus, "PAYGUARD1");
                    setupContext.ChargePaymentReservations.Local.Single(r => r.ReservationId == reservationId).AwaitingPlug = awaitingPlug;
                    setupContext.SaveChanges();
                }

                var coordinator = new StartTestPaymentCoordinator { Confirm = ConfirmStoredReservation };
                var middleware = CreateMiddleware(coordinator);
                int sendCount = 0;
                var chargePoint = CreatePreparingChargePoint(chargePointId, () =>
                {
                    Interlocked.Increment(ref sendCount);
                    CompleteQueuedRequests(middleware, "{\"status\":\"Accepted\"}");
                    return Task.CompletedTask;
                }, 1);
                chargePoint.OnlineConnectors[1].Status = connectorStatus;
                var previousStatuses = ReplaceChargePointStatuses(new Dictionary<string, ChargePointStatus>
                {
                    [chargePointId] = chargePoint
                });

                try
                {
                    var httpContext = CreateApiHttpContext("POST", "/API/Payments/Confirm", body: new JObject { ["reservationId"] = reservationId.ToString() }.ToString());
                    using (var actionContext = CreateContext(databasePath))
                    {
                        await middleware.Invoke(httpContext, actionContext);
                    }

                    Assert.Equal(200, httpContext.Response.StatusCode);
                    Assert.Equal(expectedSends, sendCount);
                    var payload = JObject.Parse(await ReadResponseBodyAsync(httpContext));
                    Assert.Equal(expectedReason, payload["reason"]?.Value<string>());

                    using var verificationContext = CreateContext(databasePath);
                    var persisted = verificationContext.ChargePaymentReservations.Single(r => r.ReservationId == reservationId);
                    if (expectedSends == 0)
                    {
                        // Nothing is touched: the Preparing trigger still owns an awaiting-plug start.
                        Assert.Equal(storedStatus, persisted.Status);
                        Assert.Equal(awaitingPlug, persisted.AwaitingPlug);
                        Assert.Null(persisted.RemoteStartSentAtUtc);
                    }
                    else
                    {
                        Assert.Equal(PaymentReservationStatus.StartRequested, persisted.Status);
                        Assert.False(persisted.AwaitingPlug);
                    }
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

        [Theory]
        [InlineData(PaymentReservationStatus.Failed, "captured_at_checkout", "Payment was captured immediately by the payment method; refund required.")]
        [InlineData(PaymentReservationStatus.Completed, null, null)]
        public async Task HandlePaymentCancelAsync_ReturnsTheFailureMessageWhenTheCancelIsNotApplied(string status, string? failureCode, string? failureMessage)
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-cancel-message-{Guid.NewGuid():N}.sqlite");
            Guid reservationId = Guid.NewGuid();

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    AddWebhookReservation(setupContext, reservationId, "CP-CANCEL-MESSAGE", 1, status, null);
                    var reservation = setupContext.ChargePaymentReservations.Local.Single(r => r.ReservationId == reservationId);
                    reservation.FailureCode = failureCode;
                    reservation.FailureMessage = failureMessage;
                    setupContext.SaveChanges();
                }

                var middleware = CreateMiddleware();
                var httpContext = CreateApiHttpContext("POST", "/API/Payments/Cancel", body: new JObject { ["reservationId"] = reservationId.ToString() }.ToString());
                using (var actionContext = CreateContext(databasePath))
                {
                    await InvokeHandlePaymentCancelAsync(middleware, httpContext, actionContext);
                }

                var payload = JObject.Parse(await ReadResponseBodyAsync(httpContext));
                Assert.Equal(status, payload["status"]?.Value<string>());
                Assert.False(payload["cancellationApplied"]?.Value<bool>() ?? true);
                Assert.True(payload.ContainsKey("message"));
                Assert.Equal(failureMessage, payload["message"]?.Value<string>());
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        [Fact]
        public async Task HandlePaymentCancelAsync_AppliedCancelHasNoMessage()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"ocpp-cancel-applied-{Guid.NewGuid():N}.sqlite");
            Guid reservationId = Guid.NewGuid();

            try
            {
                using (var setupContext = CreateContext(databasePath))
                {
                    AddWebhookReservation(setupContext, reservationId, "CP-CANCEL-APPLIED", 1, PaymentReservationStatus.Pending, null);
                    setupContext.ChargePaymentReservations.Local.Single(r => r.ReservationId == reservationId).FailureMessage = "Earlier failure.";
                    setupContext.SaveChanges();
                }

                var middleware = CreateMiddleware(new CancellingPaymentCoordinator());
                var httpContext = CreateApiHttpContext("POST", "/API/Payments/Cancel", body: new JObject { ["reservationId"] = reservationId.ToString() }.ToString());
                using (var actionContext = CreateContext(databasePath))
                {
                    await InvokeHandlePaymentCancelAsync(middleware, httpContext, actionContext);
                }

                var payload = JObject.Parse(await ReadResponseBodyAsync(httpContext));
                Assert.Equal(PaymentReservationStatus.Cancelled, payload["status"]?.Value<string>());
                Assert.True(payload["cancellationApplied"]?.Value<bool>());
                Assert.Equal(JTokenType.Null, payload["message"]?.Type);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        private static PaymentConfirmationResult ConfirmStoredReservation(OCPPCoreContext dbContext, Guid reservationId)
        {
            var reservation = dbContext.ChargePaymentReservations.Single(r => r.ReservationId == reservationId);
            return new PaymentConfirmationResult { Success = true, Status = reservation.Status, Reservation = reservation };
        }

        private static DefaultHttpContext CreateApiHttpContext(string method, string path, string? query = null, string? body = null)
        {
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Method = method;
            httpContext.Request.Path = path;
            if (!string.IsNullOrEmpty(query))
            {
                httpContext.Request.QueryString = new QueryString(query);
            }

            httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body ?? string.Empty));
            httpContext.Response.Body = new MemoryStream();
            return httpContext;
        }

        private static async Task<string> ReadResponseBodyAsync(HttpContext httpContext)
        {
            httpContext.Response.Body.Position = 0;
            using var reader = new StreamReader(httpContext.Response.Body, leaveOpen: true);
            return await reader.ReadToEndAsync();
        }

        private static void AddWebhookReservation(
            OCPPCoreContext dbContext,
            Guid reservationId,
            string chargePointId,
            int connectorId,
            string status,
            string? ocppIdTag)
        {
            dbContext.ChargePaymentReservations.Add(new ChargePaymentReservation
            {
                ReservationId = reservationId,
                ChargePointId = chargePointId,
                ConnectorId = connectorId,
                ChargeTagId = $"PAY-{reservationId:N}",
                OcppIdTag = ocppIdTag,
                Currency = "eur",
                MaxAmountCents = 2000,
                Status = status,
                AuthorizedAtUtc = status == PaymentReservationStatus.Authorized ? DateTime.UtcNow : null,
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-2),
                UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-1)
            });
        }

        private static ChargePointStatus CreatePreparingChargePoint(string chargePointId, Func<Task> onSendAsync, params int[] connectorIds)
        {
            var connectors = new Dictionary<int, OnlineConnectorStatus>();
            foreach (int connectorId in connectorIds)
            {
                connectors[connectorId] = new OnlineConnectorStatus
                {
                    Status = ConnectorStatusEnum.Preparing,
                    MeterValueDate = DateTimeOffset.UtcNow
                };
            }

            return new ChargePointStatus
            {
                Id = chargePointId,
                Protocol = "ocpp1.6",
                WebSocket = new FakeOpenWebSocket(onSendAsync),
                OnlineConnectors = connectors
            };
        }

        private static void CompleteQueuedRequests(OCPPMiddleware middleware, string result)
        {
            var field = typeof(OCPPMiddleware)
                .GetField("_requestQueue", BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(field);

            var queue = Assert.IsAssignableFrom<System.Collections.IDictionary>(field.GetValue(middleware));
            foreach (var message in queue.Values.Cast<object>().OfType<OCPPMessage>())
            {
                message.TaskCompletionSource?.TrySetResult(result);
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

        private static OCPPMiddleware CreateMiddleware(
            IPaymentCoordinator? paymentCoordinator = null,
            IDictionary<string, string?>? configValues = null,
            IServiceProvider? serviceProvider = null,
            ILoggerFactory? loggerFactory = null)
        {
            var services = serviceProvider ?? new ServiceCollection().BuildServiceProvider();
            var mediator = new StartChargingMediator();
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(configValues ?? new Dictionary<string, string?>())
                .Build();

            return new OCPPMiddleware(
                _ => Task.CompletedTask,
                loggerFactory ?? NullLoggerFactory.Instance,
                configuration,
                services.GetRequiredService<IServiceScopeFactory>(),
                paymentCoordinator ?? new NoopPaymentCoordinator(),
                mediator,
                new ReservationLinkService(mediator),
                new OcppMessageDumpService(
                    configuration,
                    NullLogger<OcppMessageDumpService>.Instance));
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

        private static async Task InvokeHandlePaymentCreateAsync(
            OCPPMiddleware middleware,
            HttpContext httpContext,
            OCPPCoreContext dbContext)
        {
            var method = typeof(OCPPMiddleware)
                .GetMethod("HandlePaymentCreateAsync", BindingFlags.Instance | BindingFlags.NonPublic);

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

        private static async Task InvokeTryAutoStopMaxEnergyTransactionAsync(
            OCPPMiddleware middleware,
            string chargePointId,
            int connectorId,
            int transactionId,
            string source)
        {
            var method = typeof(OCPPMiddleware)
                .GetMethod("TryAutoStopMaxEnergyTransactionAsync", BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(method);

            var task = Assert.IsAssignableFrom<Task>(method.Invoke(middleware, new object[] { chargePointId, connectorId, transactionId, source }));
            await task;
        }

        private static async Task<string> InvokeExecuteRequestStartTransaction20Async(
            OCPPMiddleware middleware,
            ChargePointStatus chargePointStatus,
            OCPPCoreContext dbContext,
            string connectorId,
            string idTag)
        {
            var method = typeof(OCPPMiddleware)
                .GetMethod("ExecuteRequestStartTransaction20", BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(method);

            var task = Assert.IsAssignableFrom<Task<string>>(method.Invoke(middleware, new object[] { chargePointStatus, dbContext, connectorId, idTag }));
            return await task;
        }

        private static async Task<string> InvokeExecuteRequestStartTransaction21Async(
            OCPPMiddleware middleware,
            ChargePointStatus chargePointStatus,
            OCPPCoreContext dbContext,
            string connectorId,
            string idTag)
        {
            var method = typeof(OCPPMiddleware)
                .GetMethod("ExecuteRequestStartTransaction21", BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(method);

            var task = Assert.IsAssignableFrom<Task<string>>(method.Invoke(middleware, new object[] { chargePointStatus, dbContext, connectorId, idTag }));
            return await task;
        }

        private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs = 3000, int pollDelayMs = 25)
        {
            var startedAt = DateTime.UtcNow;
            while ((DateTime.UtcNow - startedAt).TotalMilliseconds < timeoutMs)
            {
                if (predicate())
                {
                    return;
                }

                await Task.Delay(pollDelayMs);
            }

            Assert.True(predicate(), $"Condition was not met within {timeoutMs} ms.");
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

        private static int GetQueuedRequestCount(OCPPMiddleware middleware)
        {
            var field = typeof(OCPPMiddleware)
                .GetField("_requestQueue", BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(field);

            var queue = Assert.IsAssignableFrom<System.Collections.IDictionary>(field.GetValue(middleware));
            return queue.Count;
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

        private static bool InvokeIsConnectorBusy(
            OCPPMiddleware middleware,
            OCPPCoreContext dbContext,
            string chargePointId,
            int connectorId,
            ChargePointStatus chargePointStatus,
            Guid? reservationToIgnore,
            out string? busyReason)
        {
            var method = typeof(OCPPMiddleware)
                .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(m => m.Name == "IsConnectorBusy" && m.GetParameters().Length == 6);

            object?[] args =
            {
                dbContext,
                chargePointId,
                connectorId,
                chargePointStatus,
                reservationToIgnore,
                null
            };

            var result = method.Invoke(middleware, args);
            busyReason = args[5] as string;
            return Assert.IsType<bool>(result);
        }

        private static bool InvokeTryRemoveStatusIfSameInstance(
            ConcurrentDictionary<string, ChargePointStatus> statusDict,
            ChargePointStatus candidateStatus)
        {
            var method = typeof(OCPPMiddleware)
                .GetMethod("TryRemoveStatusIfSameInstance", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

            Assert.NotNull(method);

            return Assert.IsType<bool>(method.Invoke(null, new object[] { statusDict, candidateStatus }));
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

        private static ConcurrentDictionary<int, DateTime> GetMaxEnergyAutoStopTransactions(OCPPMiddleware middleware)
        {
            var field = typeof(OCPPMiddleware)
                .GetField("_maxEnergyAutoStopTransactions", BindingFlags.Instance | BindingFlags.NonPublic);

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
            public void HandleConnectorAvailable(OCPPCoreContext dbContext, string chargePointId, int connectorId, DateTime disconnectedAtUtc) { }
            public void HandleWebhookEvent(OCPPCoreContext dbContext, string payload, string signatureHeader) { }
        }

        private sealed class BuyerValidationPaymentCoordinator : IPaymentCoordinator
        {
            public bool IsEnabled => true;

            public PaymentSessionResult CreateCheckoutSession(OCPPCoreContext dbContext, PaymentSessionRequest request) =>
                throw new InvoiceBuyerValidationException(
                    "ConfirmationRequired",
                    "BuyerDataConfirmed",
                    "Confirm that invoice data is correct before saving.");

            public PaymentConfirmationResult ConfirmReservation(OCPPCoreContext dbContext, Guid reservationId, string checkoutSessionId) => throw new NotSupportedException();
            public PaymentResumeResult ResumeReservation(OCPPCoreContext dbContext, Guid reservationId) => throw new NotSupportedException();
            public PaymentR1InvoiceResult RequestR1Invoice(OCPPCoreContext dbContext, PaymentR1InvoiceRequest request) => throw new NotSupportedException();
            public void CancelReservation(OCPPCoreContext dbContext, Guid reservationId, string reason) { }
            public void CancelPaymentIntentIfCancelable(OCPPCoreContext dbContext, ChargePaymentReservation reservation, string reason) { }
            public void MarkTransactionStarted(OCPPCoreContext dbContext, string chargePointId, int connectorId, string chargeTagId, int transactionId) { }
            public void CompleteReservation(OCPPCoreContext dbContext, Transaction transaction) { }
            public void HandleConnectorAvailable(OCPPCoreContext dbContext, string chargePointId, int connectorId, DateTime disconnectedAtUtc) { }
            public void HandleWebhookEvent(OCPPCoreContext dbContext, string payload, string signatureHeader) { }
        }

        private sealed class CompletingPaymentCoordinator : IPaymentCoordinator
        {
            public bool IsEnabled => true;
            public List<int> CompletedTransactionIds { get; } = new();

            public PaymentSessionResult CreateCheckoutSession(OCPPCoreContext dbContext, PaymentSessionRequest request) => throw new NotSupportedException();
            public PaymentConfirmationResult ConfirmReservation(OCPPCoreContext dbContext, Guid reservationId, string checkoutSessionId) => throw new NotSupportedException();
            public PaymentResumeResult ResumeReservation(OCPPCoreContext dbContext, Guid reservationId) => throw new NotSupportedException();
            public PaymentR1InvoiceResult RequestR1Invoice(OCPPCoreContext dbContext, PaymentR1InvoiceRequest request) => throw new NotSupportedException();
            public void CancelReservation(OCPPCoreContext dbContext, Guid reservationId, string reason) { }
            public void CancelPaymentIntentIfCancelable(OCPPCoreContext dbContext, ChargePaymentReservation reservation, string reason) { }
            public void MarkTransactionStarted(OCPPCoreContext dbContext, string chargePointId, int connectorId, string chargeTagId, int transactionId) { }

            public void CompleteReservation(OCPPCoreContext dbContext, Transaction transaction)
            {
                CompletedTransactionIds.Add(transaction.TransactionId);

                var reservation = dbContext.ChargePaymentReservations.SingleOrDefault(r => r.TransactionId == transaction.TransactionId);
                if (reservation == null)
                {
                    return;
                }

                reservation.Status = PaymentReservationStatus.Completed;
                reservation.StopTransactionAtUtc = transaction.StopTime;
                reservation.DisconnectedAtUtc = transaction.StopTime;
                reservation.UpdatedAtUtc = transaction.StopTime ?? DateTime.UtcNow;
                dbContext.SaveChanges();
            }

            public void HandleConnectorAvailable(OCPPCoreContext dbContext, string chargePointId, int connectorId, DateTime disconnectedAtUtc) { }
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
            public void HandleConnectorAvailable(OCPPCoreContext dbContext, string chargePointId, int connectorId, DateTime disconnectedAtUtc) { }
            public void HandleWebhookEvent(OCPPCoreContext dbContext, string payload, string signatureHeader) { }
        }

        private sealed class WebhookRecordingPaymentCoordinator : IPaymentCoordinator
        {
            private readonly Func<OCPPCoreContext, PaymentWebhookRequest, PaymentWebhookResult> _handleWebhook;

            public WebhookRecordingPaymentCoordinator(Func<OCPPCoreContext, PaymentWebhookRequest, PaymentWebhookResult>? handleWebhook = null)
            {
                _handleWebhook = handleWebhook ?? ((_, _) => new PaymentWebhookResult());
            }

            public bool IsEnabled => true;
            public List<PaymentWebhookRequest> WebhookRequests { get; } = new();
            public List<(Guid ReservationId, string? CheckoutSessionId)> ConfirmCalls { get; } = new();

            public Task<PaymentWebhookResult> HandleWebhookAsync(OCPPCoreContext dbContext, PaymentWebhookRequest request, CancellationToken cancellationToken)
            {
                WebhookRequests.Add(request);
                return Task.FromResult(_handleWebhook(dbContext, request));
            }

            public PaymentConfirmationResult ConfirmReservation(OCPPCoreContext dbContext, Guid reservationId, string checkoutSessionId)
            {
                ConfirmCalls.Add((reservationId, checkoutSessionId));
                return new PaymentConfirmationResult
                {
                    Success = false,
                    Status = "Rejected",
                    Error = "Rejected by test coordinator."
                };
            }

            public PaymentSessionResult CreateCheckoutSession(OCPPCoreContext dbContext, PaymentSessionRequest request) => throw new NotSupportedException();
            public PaymentResumeResult ResumeReservation(OCPPCoreContext dbContext, Guid reservationId) => throw new NotSupportedException();
            public PaymentR1InvoiceResult RequestR1Invoice(OCPPCoreContext dbContext, PaymentR1InvoiceRequest request) => throw new NotSupportedException();
            public void CancelReservation(OCPPCoreContext dbContext, Guid reservationId, string reason) { }
            public void CancelPaymentIntentIfCancelable(OCPPCoreContext dbContext, ChargePaymentReservation reservation, string reason) { }
            public void MarkTransactionStarted(OCPPCoreContext dbContext, string chargePointId, int connectorId, string chargeTagId, int transactionId) { }
            public void CompleteReservation(OCPPCoreContext dbContext, Transaction transaction) { }
            public void HandleConnectorAvailable(OCPPCoreContext dbContext, string chargePointId, int connectorId, DateTime disconnectedAtUtc) { }
            public void HandleWebhookEvent(OCPPCoreContext dbContext, string payload, string signatureHeader) =>
                throw new InvalidOperationException("The middleware must use HandleWebhookAsync.");
        }

        /// <summary>Coordinator with independent gates, recording which existing-reservation operations reached it.</summary>
        private sealed class GatedPaymentCoordinator : IPaymentCoordinator
        {
            public GatedPaymentCoordinator(bool isEnabled, bool isOperational)
            {
                IsEnabled = isEnabled;
                IsOperational = isOperational;
            }

            public bool IsEnabled { get; }
            public bool IsOperational { get; }
            public List<string> Calls { get; } = new();

            public PaymentSessionResult CreateCheckoutSession(OCPPCoreContext dbContext, PaymentSessionRequest request)
            {
                Calls.Add("Create");
                throw new NotSupportedException();
            }

            public PaymentConfirmationResult ConfirmReservation(OCPPCoreContext dbContext, Guid reservationId, string checkoutSessionId)
            {
                Calls.Add("Confirm");
                return new PaymentConfirmationResult { Success = false, Status = "Rejected", Error = "Rejected by test coordinator." };
            }

            public PaymentResumeResult ResumeReservation(OCPPCoreContext dbContext, Guid reservationId)
            {
                Calls.Add("Resume");
                return new PaymentResumeResult { Success = true, Status = "Status" };
            }

            public PaymentR1InvoiceResult RequestR1Invoice(OCPPCoreContext dbContext, PaymentR1InvoiceRequest request)
            {
                Calls.Add("RequestR1");
                return new PaymentR1InvoiceResult { Success = false, Status = "NotFound", Error = "Reservation not found." };
            }

            public void CancelReservation(OCPPCoreContext dbContext, Guid reservationId, string reason) => Calls.Add("Cancel");
            public void CancelPaymentIntentIfCancelable(OCPPCoreContext dbContext, ChargePaymentReservation reservation, string reason) => Calls.Add("CancelPaymentIntent");
            public void MarkTransactionStarted(OCPPCoreContext dbContext, string chargePointId, int connectorId, string chargeTagId, int transactionId) { }
            public void CompleteReservation(OCPPCoreContext dbContext, Transaction transaction) { }
            public void HandleConnectorAvailable(OCPPCoreContext dbContext, string chargePointId, int connectorId, DateTime disconnectedAtUtc) { }
            public void HandleWebhookEvent(OCPPCoreContext dbContext, string payload, string signatureHeader) { }
        }

        /// <summary>Coordinator whose confirm and resume answers come from the test; records cancelled payment intents.</summary>
        private sealed class StartTestPaymentCoordinator : IPaymentCoordinator
        {
            public bool IsEnabled => true;
            public Func<OCPPCoreContext, Guid, PaymentConfirmationResult>? Confirm { get; init; }
            public Func<OCPPCoreContext, Guid, PaymentResumeResult>? Resume { get; init; }
            public ConcurrentQueue<string> CancelReasons { get; } = new();

            public PaymentConfirmationResult ConfirmReservation(OCPPCoreContext dbContext, Guid reservationId, string checkoutSessionId) =>
                (Confirm ?? throw new NotSupportedException())(dbContext, reservationId);

            public PaymentResumeResult ResumeReservation(OCPPCoreContext dbContext, Guid reservationId) =>
                (Resume ?? throw new NotSupportedException())(dbContext, reservationId);

            public void CancelPaymentIntentIfCancelable(OCPPCoreContext dbContext, ChargePaymentReservation reservation, string reason) =>
                CancelReasons.Enqueue(reason);

            public PaymentSessionResult CreateCheckoutSession(OCPPCoreContext dbContext, PaymentSessionRequest request) => throw new NotSupportedException();
            public PaymentR1InvoiceResult RequestR1Invoice(OCPPCoreContext dbContext, PaymentR1InvoiceRequest request) => throw new NotSupportedException();
            public void CancelReservation(OCPPCoreContext dbContext, Guid reservationId, string reason) { }
            public void MarkTransactionStarted(OCPPCoreContext dbContext, string chargePointId, int connectorId, string chargeTagId, int transactionId) { }
            public void CompleteReservation(OCPPCoreContext dbContext, Transaction transaction) { }
            public void HandleConnectorAvailable(OCPPCoreContext dbContext, string chargePointId, int connectorId, DateTime disconnectedAtUtc) { }
            public void HandleWebhookEvent(OCPPCoreContext dbContext, string payload, string signatureHeader) { }
        }

        /// <summary>Response body feature that records whether the response was completed (flushed to the client).</summary>
        private sealed class CompletionRecordingResponseBodyFeature : IHttpResponseBodyFeature
        {
            private readonly StreamResponseBodyFeature _inner;

            public CompletionRecordingResponseBodyFeature(Stream stream)
            {
                _inner = new StreamResponseBodyFeature(stream);
            }

            public bool Completed { get; private set; }
            public Stream Stream => _inner.Stream;
            public PipeWriter Writer => _inner.Writer;

            public void DisableBuffering() => _inner.DisableBuffering();
            public Task StartAsync(CancellationToken cancellationToken = default) => _inner.StartAsync(cancellationToken);

            public Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default) =>
                _inner.SendFileAsync(path, offset, count, cancellationToken);

            public Task CompleteAsync()
            {
                Completed = true;
                return _inner.CompleteAsync();
            }
        }

        private sealed class RecordingLoggerProvider : ILoggerProvider
        {
            public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

            public ILogger CreateLogger(string categoryName) => new RecordingLogger(Entries);

            public void Dispose()
            {
            }

            private sealed class RecordingLogger : ILogger
            {
                private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries;

                public RecordingLogger(ConcurrentQueue<(LogLevel Level, string Message)> entries)
                {
                    _entries = entries;
                }

                public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

                public bool IsEnabled(LogLevel logLevel) => true;

                public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                    _entries.Enqueue((logLevel, formatter(state, exception)));
            }
        }

        private sealed class LegacyWebhookPaymentCoordinator : IPaymentCoordinator
        {
            public bool IsEnabled => true;
            public List<(string? Payload, string? Signature)> WebhookEvents { get; } = new();

            public PaymentSessionResult CreateCheckoutSession(OCPPCoreContext dbContext, PaymentSessionRequest request) => throw new NotSupportedException();
            public PaymentConfirmationResult ConfirmReservation(OCPPCoreContext dbContext, Guid reservationId, string checkoutSessionId) => throw new NotSupportedException();
            public PaymentResumeResult ResumeReservation(OCPPCoreContext dbContext, Guid reservationId) => throw new NotSupportedException();
            public PaymentR1InvoiceResult RequestR1Invoice(OCPPCoreContext dbContext, PaymentR1InvoiceRequest request) => throw new NotSupportedException();
            public void CancelReservation(OCPPCoreContext dbContext, Guid reservationId, string reason) { }
            public void CancelPaymentIntentIfCancelable(OCPPCoreContext dbContext, ChargePaymentReservation reservation, string reason) { }
            public void MarkTransactionStarted(OCPPCoreContext dbContext, string chargePointId, int connectorId, string chargeTagId, int transactionId) { }
            public void CompleteReservation(OCPPCoreContext dbContext, Transaction transaction) { }
            public void HandleConnectorAvailable(OCPPCoreContext dbContext, string chargePointId, int connectorId, DateTime disconnectedAtUtc) { }

            public void HandleWebhookEvent(OCPPCoreContext dbContext, string payload, string signatureHeader)
            {
                WebhookEvents.Add((payload, signatureHeader));
            }
        }
    }
}
