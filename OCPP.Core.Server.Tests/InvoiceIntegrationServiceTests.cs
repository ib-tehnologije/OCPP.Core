using System;
using System.Linq;
using System.Net;
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

        private static InvoiceIntegrationService CreateService(
            string mode,
            IInvoiceDraftBuilder draftBuilder,
            IERacuniInvoiceRequestFactory requestFactory,
            IERacuniApiClient apiClient)
        {
            return new InvoiceIntegrationService(
                Options.Create(new InvoiceIntegrationOptions
                {
                    Enabled = true,
                    Provider = "ERacuni",
                    Mode = mode
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
            public int CreateCount { get; private set; }
            public ERacuniApiResult? ResultToReturn { get; set; }

            public ERacuniApiResult CreateSalesInvoice(ERacuniApiRequestEnvelope request)
            {
                CreateCount++;
                return ResultToReturn ?? new ERacuniApiResult
                {
                    StatusCode = HttpStatusCode.OK,
                    Body = "{\"status\":\"ok\",\"result\":{\"documentId\":\"doc-42\",\"number\":\"INV-2026-0042\",\"publicURL\":\"https://example.test/public/42\",\"pdfURL\":\"https://example.test/pdf/42\"}}",
                    ParsedBody = Newtonsoft.Json.Linq.JToken.Parse("{\"status\":\"ok\",\"result\":{\"documentId\":\"doc-42\",\"number\":\"INV-2026-0042\",\"publicURL\":\"https://example.test/public/42\",\"pdfURL\":\"https://example.test/pdf/42\"}}")
                };
            }
        }
    }
}
