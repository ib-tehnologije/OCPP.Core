using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OCPP.Core.Database;
using OCPP.Core.Server.Payments.Invoices;
using OCPP.Core.Server.Payments.Invoices.ERacuni;
using Stripe.Checkout;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    public class InvoiceIntegrationServiceTests
    {
        [Fact]
        public void HandleCompletedReservation_DoesNotSubmit_WhenModeIsLogOnly()
        {
            var draft = CreateDraft();
            var draftBuilder = new StubInvoiceDraftBuilder(draft);
            var requestFactory = new StubERacuniInvoiceRequestFactory();
            var apiClient = new StubERacuniApiClient();
            var service = CreateService("LogOnly", draftBuilder, requestFactory, apiClient);

            using var dbContext = CreateContext();
            service.HandleCompletedReservation(dbContext, new ChargePaymentReservation(), new Transaction(), new Session());

            Assert.Equal(1, draftBuilder.BuildCount);
            Assert.Equal(1, requestFactory.BuildCount);
            Assert.Equal(0, apiClient.CreateCount);

            var audit = Assert.Single(dbContext.InvoiceSubmissionLogs);
            Assert.Equal("LogOnly", audit.Mode);
            Assert.Equal("LoggedOnly", audit.Status);
            Assert.Equal("SalesInvoiceCreate", audit.ProviderOperation);
            Assert.Equal(draft.ReservationId, audit.ReservationId);
            Assert.Equal(draft.TransactionId, audit.TransactionId);
            Assert.Equal(draft.StripePaymentIntentId, audit.StripePaymentIntentId);
            Assert.Contains("\"apiTransactionId\"", audit.RequestPayloadJson);
            Assert.NotNull(audit.CompletedAtUtc);
        }

        [Fact]
        public void RecoverCompletedReservation_RejectsLogOnlyMode()
        {
            var service = CreateService(
                "LogOnly",
                new StubInvoiceDraftBuilder(CreateDraft()),
                new StubERacuniInvoiceRequestFactory(),
                new StubERacuniApiClient());

            using var dbContext = CreateContext();

            var error = Assert.Throws<InvalidOperationException>(() =>
                service.RecoverCompletedReservation(
                    dbContext,
                    new ChargePaymentReservation(),
                    new Transaction(),
                    new Session()));

            Assert.Contains("submit mode", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(dbContext.InvoiceSubmissionLogs);
        }

        [Fact]
        public void HandleCompletedReservation_Submits_WhenModeIsSubmit()
        {
            var draft = CreateDraft();
            var draftBuilder = new StubInvoiceDraftBuilder(draft);
            var requestFactory = new StubERacuniInvoiceRequestFactory();
            var apiClient = new StubERacuniApiClient();
            var service = CreateService("Submit", draftBuilder, requestFactory, apiClient);

            using var dbContext = CreateContext();
            service.HandleCompletedReservation(dbContext, new ChargePaymentReservation(), new Transaction(), new Session());

            Assert.Equal(1, draftBuilder.BuildCount);
            Assert.Equal(1, requestFactory.BuildCount);
            Assert.Equal(1, apiClient.CreateCount);

            var audit = Assert.Single(dbContext.InvoiceSubmissionLogs);
            Assert.Equal("Submitted", audit.Status);
            Assert.Equal(200, audit.HttpStatusCode);
            Assert.Equal("doc-42", audit.ExternalDocumentId);
            Assert.Equal("INV-2026-0042", audit.ExternalInvoiceNumber);
            Assert.Equal("https://example.test/public/42", audit.ExternalPublicUrl);
            Assert.Equal("https://example.test/pdf/42", audit.ExternalPdfUrl);
            Assert.Equal("ok", audit.ProviderResponseStatus);
            Assert.Equal("{\"status\":\"ok\",\"result\":{\"documentId\":\"doc-42\",\"number\":\"INV-2026-0042\",\"publicURL\":\"https://example.test/public/42\",\"pdfURL\":\"https://example.test/pdf/42\"}}", audit.ResponseBody);
            Assert.Equal($"ERacuni:{draft.ReservationId:N}", audit.SubmissionKey);
        }

        [Fact]
        public void HandleCompletedReservation_DoesNotCreateAgain_WhenLocalSubmissionAlreadySucceeded()
        {
            var draft = CreateDraft();
            var apiClient = new StubERacuniApiClient();
            var service = CreateService(
                "Submit",
                new StubInvoiceDraftBuilder(draft),
                new StubERacuniInvoiceRequestFactory(),
                apiClient);

            using var dbContext = CreateContext();
            var reservation = new ChargePaymentReservation();
            var transaction = new Transaction();
            var session = new Session();

            service.HandleCompletedReservation(dbContext, reservation, transaction, session);
            service.HandleCompletedReservation(dbContext, reservation, transaction, session);

            Assert.Equal(1, apiClient.CreateCount);
            Assert.Single(dbContext.InvoiceSubmissionLogs);
            Assert.Equal("Submitted", dbContext.InvoiceSubmissionLogs.Single().Status);
        }

        [Fact]
        public void HandleCompletedReservation_DoesNotCreate_WhenHistoricalNullKeySubmissionAlreadySucceeded()
        {
            var draft = CreateDraft();
            var apiClient = new StubERacuniApiClient();
            var service = CreateService(
                "Submit",
                new StubInvoiceDraftBuilder(draft),
                new StubERacuniInvoiceRequestFactory(),
                apiClient);

            using var dbContext = CreateContext();
            dbContext.InvoiceSubmissionLogs.Add(new InvoiceSubmissionLog
            {
                ReservationId = draft.ReservationId,
                TransactionId = draft.TransactionId,
                Provider = "ERacuni",
                Mode = "Submit",
                Status = "Submitted",
                ApiTransactionId = draft.ReservationId.ToString("N"),
                ExternalDocumentId = "historical-doc",
                CreatedAtUtc = DateTime.UtcNow.AddDays(-1)
            });
            dbContext.SaveChanges();

            service.HandleCompletedReservation(dbContext, new ChargePaymentReservation(), new Transaction(), new Session());

            Assert.Equal(0, apiClient.LookupCount);
            Assert.Equal(0, apiClient.CreateCount);
            Assert.Single(dbContext.InvoiceSubmissionLogs);
        }

        [Fact]
        public async Task HandleCompletedReservation_AllowsOnlyOneProviderCreateAcrossRelationalContexts()
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"invoice-lineage-{Guid.NewGuid():N}.sqlite");
            var connectionString = $"Data Source={databasePath}";
            try
            {
                var setupOptions = new DbContextOptionsBuilder<OCPPCoreContext>()
                    .UseSqlite(connectionString)
                    .Options;
                using (var setupContext = new OCPPCoreContext(setupOptions))
                {
                    setupContext.Database.EnsureCreated();
                }

                var draft = CreateDraft();
                var apiClient = new StubERacuniApiClient
                {
                    LookupResultToReturn = ERacuniInvoiceLookupResult.NotFound(),
                    BlockFirstCreate = true
                };
                var firstService = CreateService(
                    "Submit",
                    new StubInvoiceDraftBuilder(draft),
                    new StubERacuniInvoiceRequestFactory(),
                    apiClient);
                var secondService = CreateService(
                    "Submit",
                    new StubInvoiceDraftBuilder(draft),
                    new StubERacuniInvoiceRequestFactory(),
                    apiClient);

                var firstTask = Task.Run(() =>
                {
                    using var firstContext = new OCPPCoreContext(setupOptions);
                    firstService.HandleCompletedReservation(firstContext, new ChargePaymentReservation(), new Transaction(), new Session());
                });

                Assert.True(apiClient.FirstCreateEntered.Wait(TimeSpan.FromSeconds(10)), "First provider create was not reached.");

                Exception secondError;
                try
                {
                    using var secondContext = new OCPPCoreContext(setupOptions);
                    secondError = Record.Exception(() =>
                        secondService.HandleCompletedReservation(secondContext, new ChargePaymentReservation(), new Transaction(), new Session()));
                }
                finally
                {
                    apiClient.ReleaseFirstCreate.Set();
                }

                await firstTask;

                var inProgress = Assert.IsAssignableFrom<InvalidOperationException>(secondError);
                Assert.Contains("in progress", inProgress.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(1, apiClient.CreateCount);
            }
            finally
            {
                if (File.Exists(databasePath)) File.Delete(databasePath);
                if (File.Exists(databasePath + "-shm")) File.Delete(databasePath + "-shm");
                if (File.Exists(databasePath + "-wal")) File.Delete(databasePath + "-wal");
            }
        }

        [Fact]
        public void HandleCompletedReservation_UsesProviderLookupBeforeRetryingUnknownAttempt()
        {
            var draft = CreateDraft();
            var apiClient = new StubERacuniApiClient
            {
                LookupResultToReturn = ERacuniInvoiceLookupResult.Found(new ERacuniApiResult
                {
                    StatusCode = HttpStatusCode.OK,
                    Body = "{\"documentId\":\"recovered-doc\",\"number\":\"INV-RECOVERED\"}",
                    ParsedBody = Newtonsoft.Json.Linq.JToken.Parse("{\"documentId\":\"recovered-doc\",\"number\":\"INV-RECOVERED\"}")
                })
            };
            var service = CreateService(
                "Submit",
                new StubInvoiceDraftBuilder(draft),
                new StubERacuniInvoiceRequestFactory(),
                apiClient);

            using var dbContext = CreateContext();
            dbContext.InvoiceSubmissionLogs.Add(new InvoiceSubmissionLog
            {
                ReservationId = draft.ReservationId,
                TransactionId = draft.TransactionId,
                Provider = "ERacuni",
                Mode = "Submit",
                Status = "ProviderUnknown",
                SubmissionKey = $"ERacuni:{draft.ReservationId:N}",
                ApiTransactionId = draft.ReservationId.ToString("N"),
                CreatedAtUtc = DateTime.UtcNow
            });
            dbContext.SaveChanges();

            service.HandleCompletedReservation(dbContext, new ChargePaymentReservation(), new Transaction(), new Session());

            Assert.Equal(1, apiClient.LookupCount);
            Assert.Equal(0, apiClient.CreateCount);
            var audit = Assert.Single(dbContext.InvoiceSubmissionLogs);
            Assert.Equal("Submitted", audit.Status);
            Assert.Equal("recovered-doc", audit.ExternalDocumentId);
            Assert.Equal("INV-RECOVERED", audit.ExternalInvoiceNumber);
        }

        [Fact]
        public void RecoverCompletedReservation_FailsClosed_WhenInitialProviderLookupIsUnknown()
        {
            var draft = CreateDraft();
            var apiClient = new StubERacuniApiClient
            {
                LookupResultToReturn = ERacuniInvoiceLookupResult.Unknown("synthetic ambiguous response")
            };
            var service = CreateService(
                "Submit",
                new StubInvoiceDraftBuilder(draft),
                new StubERacuniInvoiceRequestFactory(),
                apiClient);

            using var dbContext = CreateContext();
            var error = Assert.Throws<InvalidOperationException>(() =>
                service.RecoverCompletedReservation(
                    dbContext,
                    new ChargePaymentReservation(),
                    new Transaction(),
                    new Session()));

            Assert.Contains("unknown", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, apiClient.LookupCount);
            Assert.Equal(0, apiClient.CreateCount);
            Assert.Equal("ProviderUnknown", Assert.Single(dbContext.InvoiceSubmissionLogs).Status);
        }

        [Fact]
        public void RecoverCompletedReservation_CreatesOnlyAfterDefinitiveInitialNotFound()
        {
            var draft = CreateDraft();
            var apiClient = new StubERacuniApiClient
            {
                LookupResultToReturn = ERacuniInvoiceLookupResult.NotFound()
            };
            var service = CreateService(
                "Submit",
                new StubInvoiceDraftBuilder(draft),
                new StubERacuniInvoiceRequestFactory(),
                apiClient);

            using var dbContext = CreateContext();
            service.RecoverCompletedReservation(
                dbContext,
                new ChargePaymentReservation(),
                new Transaction(),
                new Session());

            Assert.Equal(1, apiClient.LookupCount);
            Assert.Equal(1, apiClient.CreateCount);
            Assert.Equal("Submitted", Assert.Single(dbContext.InvoiceSubmissionLogs).Status);
        }

        [Fact]
        public void HandleCompletedReservation_MarksProviderUnknown_WhenCreateThrows()
        {
            var draft = CreateDraft();
            var apiClient = new StubERacuniApiClient
            {
                CreateException = new HttpRequestException("synthetic timeout")
            };
            var service = CreateService(
                "Submit",
                new StubInvoiceDraftBuilder(draft),
                new StubERacuniInvoiceRequestFactory(),
                apiClient);

            using var dbContext = CreateContext();
            Assert.Throws<HttpRequestException>(() =>
                service.HandleCompletedReservation(dbContext, new ChargePaymentReservation(), new Transaction(), new Session()));

            var audit = Assert.Single(dbContext.InvoiceSubmissionLogs);
            Assert.Equal("ProviderUnknown", audit.Status);
            Assert.Contains("synthetic timeout", audit.Error);
        }

        [Fact]
        public void HandleCompletedReservation_PersistsFailureAudit_WhenProviderReturnsError()
        {
            var draft = CreateDraft();
            var draftBuilder = new StubInvoiceDraftBuilder(draft);
            var requestFactory = new StubERacuniInvoiceRequestFactory();
            var apiClient = new StubERacuniApiClient
            {
                ResultToReturn = new ERacuniApiResult
                {
                    StatusCode = HttpStatusCode.BadRequest,
                    Body = "{\"status\":\"error\",\"message\":\"Invalid bank account\"}"
                }
            };
            var service = CreateService("Submit", draftBuilder, requestFactory, apiClient);

            using var dbContext = CreateContext();

            var ex = Assert.Throws<InvalidOperationException>(() =>
                service.HandleCompletedReservation(dbContext, new ChargePaymentReservation(), new Transaction(), new Session()));

            Assert.Contains("HTTP 400", ex.Message);

            var audit = Assert.Single(dbContext.InvoiceSubmissionLogs);
            Assert.Equal("Failed", audit.Status);
            Assert.Equal(400, audit.HttpStatusCode);
            Assert.Contains("Invalid bank account", audit.ResponseBody);
            Assert.Contains("HTTP 400", audit.Error);
        }

        [Fact]
        public void HandleCompletedReservation_FailsBeforeProviderCall_WhenSubmitModeIsMissingProductCodes()
        {
            var draft = CreateDraft();
            var draftBuilder = new StubInvoiceDraftBuilder(draft);
            var requestFactory = new StubERacuniInvoiceRequestFactory();
            var apiClient = new StubERacuniApiClient();
            var service = CreateService(
                "Submit",
                draftBuilder,
                requestFactory,
                apiClient,
                eracuni =>
                {
                    eracuni.LineItems = new Dictionary<string, ERacuniLineItemOptions>();
                });

            using var dbContext = CreateContext();

            var ex = Assert.Throws<InvalidOperationException>(() =>
                service.HandleCompletedReservation(dbContext, new ChargePaymentReservation(), new Transaction(), new Session()));

            Assert.Contains("requires configured product codes", ex.Message);
            Assert.Equal(1, draftBuilder.BuildCount);
            Assert.Equal(0, requestFactory.BuildCount);
            Assert.Equal(0, apiClient.CreateCount);

            var audit = Assert.Single(dbContext.InvoiceSubmissionLogs);
            Assert.Equal("Failed", audit.Status);
            Assert.Contains("INVOICES_ERACUNI_LINEITEM_ENERGY_PRODUCT_CODE", audit.RequestPayloadJson);
            Assert.Contains("\"isConfigured\":false", audit.RequestPayloadJson);
            Assert.Contains("SessionFee", audit.RequestPayloadJson);
        }

        private static InvoiceIntegrationService CreateService(
            string mode,
            IInvoiceDraftBuilder draftBuilder,
            IERacuniInvoiceRequestFactory requestFactory,
            IERacuniApiClient apiClient,
            Action<ERacuniInvoiceOptions>? configureEracuni = null)
        {
            var eracuni = new ERacuniInvoiceOptions
            {
                LineItems = new Dictionary<string, ERacuniLineItemOptions>
                {
                    ["Energy"] = new ERacuniLineItemOptions { ProductCode = "EV-ENERGY" },
                    ["SessionFee"] = new ERacuniLineItemOptions { ProductCode = "EV-SESSION" },
                    ["UsageFee"] = new ERacuniLineItemOptions { ProductCode = "EV-OCCUPANCY" },
                    ["IdleFee"] = new ERacuniLineItemOptions { ProductCode = "EV-IDLE" }
                }
            };
            configureEracuni?.Invoke(eracuni);

            return new InvoiceIntegrationService(
                Options.Create(new InvoiceIntegrationOptions
                {
                    Enabled = true,
                    Provider = "ERacuni",
                    Mode = mode,
                    ERacuni = eracuni
                }),
                draftBuilder,
                requestFactory,
                apiClient,
                NullLogger<InvoiceIntegrationService>.Instance);
        }

        private static InvoiceDraft CreateDraft()
        {
            return new InvoiceDraft
            {
                ReservationId = Guid.NewGuid(),
                TransactionId = 101,
                InvoiceKind = "Retail",
                Currency = "EUR",
                StripeCheckoutSessionId = "cs_test_123",
                StripePaymentIntentId = "pi_123",
                Lines =
                {
                    new InvoiceDraftLine
                    {
                        Type = "Energy",
                        Description = "Charging energy",
                        Quantity = 1m,
                        UnitCode = "kWh",
                        UnitPrice = 0.30m,
                        LineAmount = 0.30m
                    }
                }
            };
        }

        private static OCPPCoreContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<OCPPCoreContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            return new OCPPCoreContext(options);
        }

        private sealed class StubInvoiceDraftBuilder : IInvoiceDraftBuilder
        {
            private readonly InvoiceDraft _draft;

            public StubInvoiceDraftBuilder(InvoiceDraft draft)
            {
                _draft = draft;
            }

            public int BuildCount { get; private set; }

            public InvoiceDraft Build(ChargePaymentReservation reservation, Transaction transaction, Session checkoutSession)
            {
                BuildCount++;
                return _draft;
            }
        }

        private sealed class StubERacuniInvoiceRequestFactory : IERacuniInvoiceRequestFactory
        {
            public int BuildCount { get; private set; }

            public ERacuniApiRequestEnvelope BuildCreateSalesInvoiceRequest(InvoiceDraft draft)
            {
                BuildCount++;
                return new ERacuniApiRequestEnvelope
                {
                    Username = "api-user",
                    SecretKey = "secret",
                    Token = "token",
                    Method = "SalesInvoiceCreate",
                    Parameters = new ERacuniSalesInvoiceCreateParameters
                    {
                        ApiTransactionId = draft.ReservationId.ToString("N"),
                        SalesInvoice = new ERacuniSalesInvoice()
                    }
                };
            }

            public object BuildSanitizedLogPayload(ERacuniApiRequestEnvelope request)
            {
                return request;
            }
        }

        private sealed class StubERacuniApiClient : IERacuniApiClient
        {
            private int _createCount;
            private int _lookupCount;

            public int CreateCount => Volatile.Read(ref _createCount);
            public int LookupCount => Volatile.Read(ref _lookupCount);
            public ERacuniApiResult? ResultToReturn { get; set; }
            public ERacuniInvoiceLookupResult? LookupResultToReturn { get; set; }
            public Exception? CreateException { get; set; }
            public bool BlockFirstCreate { get; set; }
            public ManualResetEventSlim FirstCreateEntered { get; } = new(false);
            public ManualResetEventSlim ReleaseFirstCreate { get; } = new(false);

            public ERacuniApiResult CreateSalesInvoice(ERacuniApiRequestEnvelope request)
            {
                var createNumber = Interlocked.Increment(ref _createCount);
                if (BlockFirstCreate && createNumber == 1)
                {
                    FirstCreateEntered.Set();
                    if (!ReleaseFirstCreate.Wait(TimeSpan.FromSeconds(10)))
                    {
                        throw new TimeoutException("Synthetic first provider create was not released.");
                    }
                }
                if (CreateException != null)
                {
                    throw CreateException;
                }

                return ResultToReturn ?? new ERacuniApiResult
                {
                    StatusCode = HttpStatusCode.OK,
                    Body = "{\"status\":\"ok\",\"result\":{\"documentId\":\"doc-42\",\"number\":\"INV-2026-0042\",\"publicURL\":\"https://example.test/public/42\",\"pdfURL\":\"https://example.test/pdf/42\"}}",
                    ParsedBody = Newtonsoft.Json.Linq.JToken.Parse("{\"status\":\"ok\",\"result\":{\"documentId\":\"doc-42\",\"number\":\"INV-2026-0042\",\"publicURL\":\"https://example.test/public/42\",\"pdfURL\":\"https://example.test/pdf/42\"}}")
                };
            }

            public ERacuniInvoiceLookupResult LookupSalesInvoiceByApiTransactionId(ERacuniApiRequestEnvelope request)
            {
                Interlocked.Increment(ref _lookupCount);
                return LookupResultToReturn ?? ERacuniInvoiceLookupResult.Unknown("Synthetic lookup was not configured.");
            }
        }
    }
}
