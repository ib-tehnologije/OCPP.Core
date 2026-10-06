using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using OCPP.Core.Server.Payments.Invoices;
using OCPP.Core.Server.Payments.Invoices.ERacuni;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    public class ERacuniApiClientTests
    {
        [Fact]
        public void CreateSalesInvoice_PostsJsonEnvelope_ToConfiguredEndpoint()
        {
            var handler = new RecordingHttpMessageHandler();
            var httpClientFactory = new StubHttpClientFactory(new HttpClient(handler)
            {
                BaseAddress = new Uri("https://ignored.example/")
            });
            var client = new ERacuniApiClient(
                httpClientFactory,
                Options.Create(new InvoiceIntegrationOptions
                {
                    ERacuni = new ERacuniInvoiceOptions
                    {
                        ApiBaseUrl = "https://eurofaktura.example",
                        ApiPath = "/WebServices/API",
                        MinimumRequestIntervalMilliseconds = 0
                    }
                }),
                NullLogger<ERacuniApiClient>.Instance);

            var result = client.CreateSalesInvoice(new ERacuniApiRequestEnvelope
            {
                Username = "api-user",
                SecretKey = "secret-1234",
                Token = "token-9876",
                Method = "SalesInvoiceCreate",
                Parameters = new ERacuniSalesInvoiceCreateParameters
                {
                    ApiTransactionId = "tx-1",
                    SalesInvoice = new ERacuniSalesInvoice
                    {
                        Type = "Retail",
                        Date = "2026-03-05"
                    }
                }
            });

            Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
            Assert.Equal("https://eurofaktura.example/WebServices/API", handler.LastRequest.RequestUri!.ToString());
            Assert.Equal("application/json", handler.LastRequest.Content!.Headers.ContentType!.MediaType);
            Assert.Contains("\"method\":\"SalesInvoiceCreate\"", handler.LastRequestBody!);
            Assert.Contains("\"apiTransactionId\":\"tx-1\"", handler.LastRequestBody!);
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
            Assert.NotNull(result.ParsedBody);
            Assert.Equal("INV-2026-0001", result.ParsedBody!["number"]?.ToString());
        }

        [Fact]
        public void CreateSalesInvoice_ReturnsErrorResponseBody_ForAuditPersistence()
        {
            var handler = new RecordingHttpMessageHandler
            {
                Response = new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("{\"status\":\"error\",\"message\":\"Invalid payload\"}", Encoding.UTF8, "application/json")
                }
            };
            var httpClientFactory = new StubHttpClientFactory(new HttpClient(handler));
            var client = new ERacuniApiClient(
                httpClientFactory,
                Options.Create(new InvoiceIntegrationOptions
                {
                    ERacuni = new ERacuniInvoiceOptions()
                }),
                NullLogger<ERacuniApiClient>.Instance);

            var result = client.CreateSalesInvoice(new ERacuniApiRequestEnvelope
            {
                Username = "api-user",
                SecretKey = "secret-1234",
                Token = "token-9876",
                Method = "SalesInvoiceCreate",
                Parameters = new { }
            });

            Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
            Assert.Equal("error", result.ParsedBody!["status"]?.ToString());
            Assert.Equal("Invalid payload", result.ParsedBody!["message"]?.ToString());
        }

        [Fact]
        public void LookupSalesInvoice_PostsOnlyDocumentedDateWindowFilters()
        {
            var handler = CreateHandler(Envelope());
            var client = CreateClient(handler);

            client.LookupSalesInvoice(CreateLookupRequest(), CreateCriteria());

            var body = JObject.Parse(handler.LastRequestBody!);
            Assert.Equal("SalesInvoiceList", body["method"]?.ToString());
            var parameters = Assert.IsType<JObject>(body["parameters"]);
            Assert.Equal(
                new[] { "dateFrom", "dateTo" },
                parameters.Properties().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
            Assert.Equal("2026-01-01", parameters["dateFrom"]?.ToString());
            Assert.Equal("2026-01-03", parameters["dateTo"]?.ToString());
            Assert.DoesNotContain("apiTransactionId", handler.LastRequestBody!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("EVSE-101", handler.LastRequestBody!, StringComparison.Ordinal);
        }

        [Fact]
        public void LookupSalesInvoice_ReturnsFoundForOneOrderReferenceAndAmountMatchInProviderEnvelope()
        {
            var handler = CreateHandler(Envelope(
                InvoiceRow("EVSE-1010", number: "2026-0041", totalAmount: 12.34m, reference: "00 8511-261"),
                InvoiceRow("EVSE-101", number: "2026-0042", reference: "00 8512-261"),
                InvoiceRow(null, number: "2026-0043", reference: string.Empty),
                InvoiceRow("EVSE-10", number: "2026-0044", totalAmount: 12.34m, reference: "00 8513-261")));
            var client = CreateClient(handler);

            var result = client.LookupSalesInvoice(CreateLookupRequest(), CreateCriteria());

            Assert.Equal(ERacuniInvoiceLookupOutcome.Found, result.Outcome);
            Assert.True(result.Diagnostics.RequestAttempted);
            Assert.Equal(ERacuniInvoiceLookupFailureCategory.None, result.Diagnostics.FailureCategory);
            Assert.Equal(200, result.Diagnostics.HttpStatusCode);
            Assert.Equal(ERacuniInvoiceLookupResponseShape.ResponseResultArray, result.Diagnostics.ResponseShape);
            Assert.Equal("2026-0042", result.ProviderResult!.ParsedBody!["number"]?.ToString());
            Assert.Equal("2026-0042", ERacuniApiResponseMetadataReader.Read(result.ProviderResult.ParsedBody).InvoiceNumber);
        }

        [Theory]
        [InlineData("{\"status\":\"ok\",\"result\":[{\"date\":\"2026-01-02\",\"number\":\"2026-0042\",\"orderReference\":\" evse-101 \",\"totalAmount\":\"12.340\",\"totalCurrency\":\"eur\"}]}", ERacuniInvoiceLookupResponseShape.ResultArray)]
        [InlineData("[{\"date\":\"2026-01-02T00:00:00\",\"documentID\":\"doc-42\",\"orderReference\":\"EVSE-101\",\"totalAmount\":12.34,\"totalCurrency\":\"EUR\"}]", ERacuniInvoiceLookupResponseShape.JsonArray)]
        public void LookupSalesInvoice_ReturnsFoundForEquivalentRowEncodings(
            string body,
            ERacuniInvoiceLookupResponseShape expectedShape)
        {
            var client = CreateClient(CreateHandler(body));

            var result = client.LookupSalesInvoice(CreateLookupRequest(), CreateCriteria());

            Assert.Equal(ERacuniInvoiceLookupOutcome.Found, result.Outcome);
            Assert.Equal(expectedShape, result.Diagnostics.ResponseShape);
        }

        [Fact]
        public void LookupSalesInvoice_ReturnsNotFoundForCompleteWindowWithoutOrderReferenceMatch()
        {
            var client = CreateClient(CreateHandler(Envelope(
                InvoiceRow("EVSE-1010"),
                InvoiceRow("EVSE-10"),
                InvoiceRow("XEVSE-101"),
                InvoiceRow(null),
                InvoiceRow(string.Empty))));

            var result = client.LookupSalesInvoice(CreateLookupRequest(), CreateCriteria());

            Assert.Equal(ERacuniInvoiceLookupOutcome.NotFound, result.Outcome);
            Assert.True(result.Diagnostics.RequestAttempted);
            Assert.Equal(ERacuniInvoiceLookupFailureCategory.None, result.Diagnostics.FailureCategory);
            Assert.Equal(ERacuniInvoiceLookupResponseShape.ResponseResultArray, result.Diagnostics.ResponseShape);
            Assert.Null(result.ProviderResult);
        }

        [Theory]
        [InlineData("[]")]
        [InlineData("{\"status\":\"ok\",\"result\":[]}")]
        [InlineData("{\"response\":{\"status\":\"ok\",\"result\":[]}}")]
        public void LookupSalesInvoice_ReturnsNotFoundForRecognizedEmptyResults(string body)
        {
            var client = CreateClient(CreateHandler(body));

            var result = client.LookupSalesInvoice(CreateLookupRequest(), CreateCriteria());

            Assert.Equal(ERacuniInvoiceLookupOutcome.NotFound, result.Outcome);
            Assert.True(result.Diagnostics.RequestAttempted);
            Assert.Equal(ERacuniInvoiceLookupFailureCategory.None, result.Diagnostics.FailureCategory);
            Assert.Equal(200, result.Diagnostics.HttpStatusCode);
        }

        [Theory]
        [InlineData(ERacuniApiClient.ProviderListRowLimit, ERacuniInvoiceLookupOutcome.Unknown)]
        [InlineData(ERacuniApiClient.ProviderListRowLimit + 1, ERacuniInvoiceLookupOutcome.Unknown)]
        [InlineData(ERacuniApiClient.ProviderListRowLimit - 1, ERacuniInvoiceLookupOutcome.NotFound)]
        public void LookupSalesInvoice_TreatsFullProviderPageAsTruncated(
            int rowCount,
            ERacuniInvoiceLookupOutcome expectedOutcome)
        {
            var rows = Enumerable.Range(1, rowCount)
                .Select(index => InvoiceRow($"OTHER-{index}"))
                .ToArray();
            var client = CreateClient(CreateHandler(Envelope(rows)));

            var result = client.LookupSalesInvoice(CreateLookupRequest(), CreateCriteria());

            Assert.Equal(expectedOutcome, result.Outcome);
            Assert.Equal(
                expectedOutcome == ERacuniInvoiceLookupOutcome.Unknown
                    ? ERacuniInvoiceLookupFailureCategory.TruncatedResponse
                    : ERacuniInvoiceLookupFailureCategory.None,
                result.Diagnostics.FailureCategory);
        }

        [Fact]
        public void LookupSalesInvoice_ReturnsUnknownForFullPageEvenWhenItContainsTheMatch()
        {
            var rows = Enumerable.Range(1, ERacuniApiClient.ProviderListRowLimit - 1)
                .Select(index => InvoiceRow($"OTHER-{index}"))
                .Append(InvoiceRow("EVSE-101"))
                .ToArray();
            var client = CreateClient(CreateHandler(Envelope(rows)));

            var result = client.LookupSalesInvoice(CreateLookupRequest(), CreateCriteria());

            Assert.Equal(ERacuniInvoiceLookupOutcome.Unknown, result.Outcome);
            Assert.Equal(ERacuniInvoiceLookupFailureCategory.TruncatedResponse, result.Diagnostics.FailureCategory);
        }

        [Fact]
        public void LookupSalesInvoice_ReturnsUnknownForDuplicateOrderReferenceMatches()
        {
            var client = CreateClient(CreateHandler(Envelope(
                InvoiceRow("EVSE-101", number: "2026-0042"),
                InvoiceRow("EVSE-101", number: "2026-0043", totalAmount: 99m))));

            var result = client.LookupSalesInvoice(CreateLookupRequest(), CreateCriteria());

            Assert.Equal(ERacuniInvoiceLookupOutcome.Unknown, result.Outcome);
            Assert.True(result.Diagnostics.RequestAttempted);
            Assert.Equal(ERacuniInvoiceLookupFailureCategory.DuplicateMatch, result.Diagnostics.FailureCategory);
            Assert.Equal(200, result.Diagnostics.HttpStatusCode);
        }

        [Theory]
        [InlineData("{\"date\":\"2026-01-02\",\"number\":\"2026-0042\",\"orderReference\":\"EVSE-101\",\"documentAmount\":12.35,\"documentCurrency\":\"EUR\"}")]
        [InlineData("{\"date\":\"2026-01-02\",\"number\":\"2026-0042\",\"orderReference\":\"EVSE-101\",\"documentCurrency\":\"EUR\"}")]
        [InlineData("{\"date\":\"2026-01-02\",\"number\":\"2026-0042\",\"orderReference\":\"EVSE-101\",\"documentAmount\":\"12,34\",\"documentCurrency\":\"EUR\"}")]
        [InlineData("{\"date\":\"2026-01-02\",\"number\":\"2026-0042\",\"orderReference\":\"EVSE-101\",\"documentAmount\":12.34,\"documentCurrency\":\"USD\"}")]
        [InlineData("{\"date\":\"2026-01-02\",\"number\":\"2026-0042\",\"orderReference\":\"EVSE-101\",\"documentAmount\":12.34}")]
        public void LookupSalesInvoice_ReturnsUnknownWhenOrderReferenceMatchHasDifferentTotal(string row)
        {
            var client = CreateClient(CreateHandler($"{{\"response\":{{\"status\":\"ok\",\"result\":[{row}]}}}}"));

            var result = client.LookupSalesInvoice(CreateLookupRequest(), CreateCriteria());

            Assert.Equal(ERacuniInvoiceLookupOutcome.Unknown, result.Outcome);
            Assert.Equal(ERacuniInvoiceLookupFailureCategory.AmountMismatch, result.Diagnostics.FailureCategory);
        }

        [Fact]
        public void LookupSalesInvoice_IgnoresProviderGeneratedPaymentReference()
        {
            var client = CreateClient(CreateHandler(Envelope(
                InvoiceRow("EVSE-101", number: "8512/EVC/261", reference: "00 8512-261"))));

            var result = client.LookupSalesInvoice(CreateLookupRequest(), CreateCriteria());

            Assert.Equal(ERacuniInvoiceLookupOutcome.Found, result.Outcome);
        }

        [Fact]
        public void LookupSalesInvoice_PrefersDocumentAmountOverDocumentedTotalAmount()
        {
            var client = CreateClient(CreateHandler(
                "{\"response\":{\"status\":\"ok\",\"result\":[{\"date\":\"2026-01-02\",\"number\":\"2026-0042\",\"orderReference\":\"EVSE-101\",\"documentAmount\":99.00,\"documentCurrency\":\"EUR\",\"totalAmount\":12.34,\"totalCurrency\":\"EUR\"}]}}"));

            var result = client.LookupSalesInvoice(CreateLookupRequest(), CreateCriteria());

            Assert.Equal(ERacuniInvoiceLookupOutcome.Unknown, result.Outcome);
            Assert.Equal(ERacuniInvoiceLookupFailureCategory.AmountMismatch, result.Diagnostics.FailureCategory);
        }

        [Theory]
        [InlineData("2025-12-31")]
        [InlineData("2026-01-04")]
        public void LookupSalesInvoice_ReturnsUnknownWhenProviderIgnoresDateWindow(string date)
        {
            var client = CreateClient(CreateHandler(Envelope(
                InvoiceRow("OTHER-1"),
                InvoiceRow("OTHER-2", date: date))));

            var result = client.LookupSalesInvoice(CreateLookupRequest(), CreateCriteria());

            Assert.Equal(ERacuniInvoiceLookupOutcome.Unknown, result.Outcome);
            Assert.Equal(ERacuniInvoiceLookupFailureCategory.RowOutsideDateWindow, result.Diagnostics.FailureCategory);
        }

        [Theory]
        [InlineData("{\"date\":\"2026-01-02\",\"number\":\"2026-0042\",\"totalAmount\":12.34,\"totalCurrency\":\"EUR\"}")]
        [InlineData("{\"date\":\"2026-01-02\",\"number\":\"2026-0042\",\"apiTransactionId\":\"exact-ref\",\"totalAmount\":12.34}")]
        [InlineData("{\"number\":\"2026-0042\",\"orderReference\":\"EVSE-101\",\"totalAmount\":12.34,\"totalCurrency\":\"EUR\"}")]
        [InlineData("{\"date\":\"02.01.2026\",\"number\":\"2026-0042\",\"orderReference\":\"OTHER\",\"totalAmount\":12.34}")]
        [InlineData("\"2026-0042\"")]
        public void LookupSalesInvoice_ReturnsUnknownWhenRowCannotProveAbsence(string row)
        {
            var client = CreateClient(CreateHandler($"{{\"response\":{{\"status\":\"ok\",\"result\":[{row}]}}}}"));

            var result = client.LookupSalesInvoice(CreateLookupRequest(), CreateCriteria());

            Assert.Equal(ERacuniInvoiceLookupOutcome.Unknown, result.Outcome);
            Assert.Equal(ERacuniInvoiceLookupFailureCategory.UnrecognizedRow, result.Diagnostics.FailureCategory);
        }

        [Fact]
        public void LookupSalesInvoice_ReturnsUnknownForMatchWithoutDurableIdentifier()
        {
            var client = CreateClient(CreateHandler(Envelope(InvoiceRow("EVSE-101", number: null))));

            var result = client.LookupSalesInvoice(CreateLookupRequest(), CreateCriteria());

            Assert.Equal(ERacuniInvoiceLookupOutcome.Unknown, result.Outcome);
            Assert.Equal(ERacuniInvoiceLookupFailureCategory.MissingDurableIdentifier, result.Diagnostics.FailureCategory);
        }

        [Fact]
        public void LookupSalesInvoice_ReturnsSanitizedUnknownForProviderErrorStatus()
        {
            var client = CreateClient(CreateHandler(
                "{\"response\":{\"status\":\"error\",\"result\":[],\"error\":{\"description\":\"synthetic-private-provider-detail\"}}}"));

            var result = client.LookupSalesInvoice(CreateLookupRequest(), CreateCriteria());

            Assert.Equal(ERacuniInvoiceLookupOutcome.Unknown, result.Outcome);
            Assert.Equal(ERacuniInvoiceLookupFailureCategory.ProviderErrorStatus, result.Diagnostics.FailureCategory);
            Assert.Equal(ERacuniInvoiceLookupResponseShape.ResponseResultArray, result.Diagnostics.ResponseShape);
            Assert.DoesNotContain("synthetic-private", result.Error, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("{\"status\":\"ok\",\"unexpected\":[]}")]
        [InlineData("{\"response\":{\"status\":\"ok\",\"result\":{\"number\":\"2026-0042\"}}}")]
        [InlineData("\"ok\"")]
        public void LookupSalesInvoice_ReturnsUnknownForUnrecognizedResponseShape(string body)
        {
            var client = CreateClient(CreateHandler(body));

            var result = client.LookupSalesInvoice(CreateLookupRequest(), CreateCriteria());

            Assert.Equal(ERacuniInvoiceLookupOutcome.Unknown, result.Outcome);
            Assert.Equal(ERacuniInvoiceLookupFailureCategory.UnrecognizedResponse, result.Diagnostics.FailureCategory);
        }

        [Theory]
        [InlineData(null, "2026-01-01", "2026-01-03", "12.34")]
        [InlineData(" ", "2026-01-01", "2026-01-03", "12.34")]
        [InlineData("EVSE-101", null, "2026-01-03", "12.34")]
        [InlineData("EVSE-101", "2026-01-01", "03.01.2026", "12.34")]
        [InlineData("EVSE-101", "2026-01-01", "2026-01-03", null)]
        public void LookupSalesInvoice_ReturnsMissingReferenceWithoutSending(
            string? orderReference,
            string? dateFrom,
            string? dateTo,
            string? totalAmount)
        {
            var handler = new RecordingHttpMessageHandler();
            var client = CreateClient(handler);

            var result = client.LookupSalesInvoice(
                CreateLookupRequest(dateFrom, dateTo),
                new ERacuniSalesInvoiceLookupCriteria
                {
                    OrderReference = orderReference,
                    TotalAmount = totalAmount == null ? null : decimal.Parse(totalAmount, CultureInfo.InvariantCulture),
                    Currency = "EUR"
                });

            Assert.Equal(ERacuniInvoiceLookupOutcome.Unknown, result.Outcome);
            Assert.False(result.Diagnostics.RequestAttempted);
            Assert.Equal(ERacuniInvoiceLookupFailureCategory.MissingReference, result.Diagnostics.FailureCategory);
            Assert.Null(handler.LastRequest);
        }

        [Theory]
        [InlineData("2026-01-03", "2026-01-01")]
        [InlineData("2026-01-01", "2026-02-01")]
        public void LookupSalesInvoice_RejectsInvertedOrUnboundedWindowWithoutSending(string dateFrom, string dateTo)
        {
            var handler = new RecordingHttpMessageHandler();
            var client = CreateClient(handler);

            var result = client.LookupSalesInvoice(CreateLookupRequest(dateFrom, dateTo), CreateCriteria());

            Assert.Equal(ERacuniInvoiceLookupOutcome.Unknown, result.Outcome);
            Assert.False(result.Diagnostics.RequestAttempted);
            Assert.Equal(ERacuniInvoiceLookupFailureCategory.Configuration, result.Diagnostics.FailureCategory);
            Assert.Null(handler.LastRequest);
        }

        [Fact]
        public void LookupSalesInvoice_ReturnsStructuredPreflightFailureWithoutSending()
        {
            var handler = new RecordingHttpMessageHandler();
            var client = CreateClient(handler);

            var result = client.LookupSalesInvoice(new ERacuniApiRequestEnvelope
            {
                Method = "SalesInvoiceList",
                Parameters = new ERacuniSalesInvoiceLookupParameters { DateFrom = "2026-01-01", DateTo = "2026-01-03" }
            }, CreateCriteria());

            Assert.Equal(ERacuniInvoiceLookupOutcome.Unknown, result.Outcome);
            Assert.False(result.Diagnostics.RequestAttempted);
            Assert.Equal(ERacuniInvoiceLookupFailureCategory.Configuration, result.Diagnostics.FailureCategory);
            Assert.Null(result.Diagnostics.HttpStatusCode);
            Assert.Equal(ERacuniInvoiceLookupResponseShape.NotAvailable, result.Diagnostics.ResponseShape);
            Assert.Null(handler.LastRequest);
            Assert.DoesNotContain("credential", result.Error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void LookupSalesInvoice_ReturnsSanitizedTransportFailureAfterAttempt()
        {
            var handler = new RecordingHttpMessageHandler
            {
                ExceptionToThrow = new HttpRequestException("synthetic-private-transport-detail")
            };
            var client = CreateClient(handler);

            var result = client.LookupSalesInvoice(CreateLookupRequest(), CreateCriteria());

            Assert.Equal(ERacuniInvoiceLookupOutcome.Unknown, result.Outcome);
            Assert.True(result.Diagnostics.RequestAttempted);
            Assert.Equal(ERacuniInvoiceLookupFailureCategory.Transport, result.Diagnostics.FailureCategory);
            Assert.Null(result.Diagnostics.HttpStatusCode);
            Assert.Equal(ERacuniInvoiceLookupResponseShape.NotAvailable, result.Diagnostics.ResponseShape);
            Assert.DoesNotContain("synthetic-private", result.Error, StringComparison.Ordinal);
        }

        [Fact]
        public void LookupSalesInvoice_ReturnsStructuredHttpFailureWithoutBody()
        {
            var handler = new RecordingHttpMessageHandler
            {
                Response = new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("{\"private\":\"provider-detail\"}", Encoding.UTF8, "application/json")
                }
            };
            var client = CreateClient(handler);

            var result = client.LookupSalesInvoice(CreateLookupRequest(), CreateCriteria());

            Assert.Equal(ERacuniInvoiceLookupOutcome.Unknown, result.Outcome);
            Assert.True(result.Diagnostics.RequestAttempted);
            Assert.Equal(ERacuniInvoiceLookupFailureCategory.HttpStatus, result.Diagnostics.FailureCategory);
            Assert.Equal(401, result.Diagnostics.HttpStatusCode);
            Assert.Equal(ERacuniInvoiceLookupResponseShape.JsonObject, result.Diagnostics.ResponseShape);
            Assert.DoesNotContain("provider-detail", result.Error, StringComparison.Ordinal);
        }

        [Fact]
        public void LookupSalesInvoice_ReturnsStructuredNonJsonFailureWithoutBody()
        {
            var handler = new RecordingHttpMessageHandler
            {
                Response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("private non-json provider payload", Encoding.UTF8, "text/plain")
                }
            };
            var client = CreateClient(handler);

            var result = client.LookupSalesInvoice(CreateLookupRequest(), CreateCriteria());

            Assert.Equal(ERacuniInvoiceLookupOutcome.Unknown, result.Outcome);
            Assert.True(result.Diagnostics.RequestAttempted);
            Assert.Equal(ERacuniInvoiceLookupFailureCategory.NonJsonResponse, result.Diagnostics.FailureCategory);
            Assert.Equal(200, result.Diagnostics.HttpStatusCode);
            Assert.Equal(ERacuniInvoiceLookupResponseShape.NonJson, result.Diagnostics.ResponseShape);
            Assert.DoesNotContain("private non-json", result.Error, StringComparison.Ordinal);
        }

        [Fact]
        public void LookupSalesInvoice_PreservesStatusWhenResponseBodyReadFails()
        {
            var handler = new RecordingHttpMessageHandler
            {
                Response = new HttpResponseMessage(HttpStatusCode.BadGateway)
                {
                    Content = new ThrowingHttpContent()
                }
            };
            var client = CreateClient(handler);

            var result = client.LookupSalesInvoice(CreateLookupRequest(), CreateCriteria());

            Assert.Equal(ERacuniInvoiceLookupOutcome.Unknown, result.Outcome);
            Assert.True(result.Diagnostics.RequestAttempted);
            Assert.Equal(ERacuniInvoiceLookupFailureCategory.Transport, result.Diagnostics.FailureCategory);
            Assert.Equal(502, result.Diagnostics.HttpStatusCode);
            Assert.Equal(ERacuniInvoiceLookupResponseShape.NotAvailable, result.Diagnostics.ResponseShape);
        }

        [Fact]
        public void LookupSalesInvoice_BoundsResponseBodyReadWithHttpClientTimeout()
        {
            using var content = new CancellationAwareSlowHttpContent();
            var handler = new RecordingHttpMessageHandler
            {
                Response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = content
                }
            };
            var client = CreateClient(handler, TimeSpan.FromMilliseconds(100));

            // The body never completes on its own; only the client's timeout token can end the read.
            // The safety net only exists so a regression fails the test instead of hanging the suite.
            using var safetyNet = new System.Threading.Timer(_ => content.ReleaseWithoutCancellation(), null, TimeSpan.FromSeconds(30), System.Threading.Timeout.InfiniteTimeSpan);

            var result = client.LookupSalesInvoice(CreateLookupRequest(), CreateCriteria());

            Assert.False(content.ReleasedWithoutCancellation, "Response body read was not bounded by the HttpClient timeout.");
            Assert.True(content.CancellationObserved);
            Assert.Equal(ERacuniInvoiceLookupOutcome.Unknown, result.Outcome);
            Assert.Equal(ERacuniInvoiceLookupFailureCategory.Transport, result.Diagnostics.FailureCategory);
            Assert.Equal(200, result.Diagnostics.HttpStatusCode);
        }

        private static ERacuniApiRequestEnvelope CreateLookupRequest(
            string? dateFrom = "2026-01-01",
            string? dateTo = "2026-01-03") => new()
        {
            Username = "api-user",
            SecretKey = "secret-1234",
            Token = "token-9876",
            Method = "SalesInvoiceList",
            Parameters = new ERacuniSalesInvoiceLookupParameters { DateFrom = dateFrom, DateTo = dateTo }
        };

        private static ERacuniSalesInvoiceLookupCriteria CreateCriteria() => new()
        {
            OrderReference = "EVSE-101",
            TotalAmount = 12.34m,
            Currency = "EUR"
        };

        private static JObject InvoiceRow(
            string? orderReference,
            string date = "2026-01-02",
            string? number = "2026-0001",
            decimal totalAmount = 12.34m,
            string? reference = null)
        {
            var row = new JObject
            {
                ["date"] = date,
                ["orderReference"] = orderReference == null ? JValue.CreateNull() : new JValue(orderReference),
                ["documentAmount"] = totalAmount,
                ["documentCurrency"] = "EUR",
                ["buyerName"] = "Synthetic Buyer"
            };
            if (number != null)
            {
                row["number"] = number;
            }

            if (reference != null)
            {
                row["reference"] = reference;
            }

            return row;
        }

        private static string Envelope(params JObject[] rows) =>
            new JObject
            {
                ["response"] = new JObject
                {
                    ["status"] = "ok",
                    ["result"] = new JArray(rows)
                }
            }.ToString(Newtonsoft.Json.Formatting.None);

        private static RecordingHttpMessageHandler CreateHandler(string body) => new()
        {
            Response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            }
        };

        private static ERacuniApiClient CreateClient(
            RecordingHttpMessageHandler handler,
            TimeSpan? timeout = null)
        {
            var httpClient = new HttpClient(handler);
            if (timeout.HasValue)
            {
                httpClient.Timeout = timeout.Value;
            }

            return new ERacuniApiClient(
                new StubHttpClientFactory(httpClient),
                Options.Create(new InvoiceIntegrationOptions
                {
                    ERacuni = new ERacuniInvoiceOptions { MinimumRequestIntervalMilliseconds = 0 }
                }),
                NullLogger<ERacuniApiClient>.Instance);
        }

        private sealed class RecordingHttpMessageHandler : HttpMessageHandler
        {
            public HttpRequestMessage? LastRequest { get; private set; }
            public string? LastRequestBody { get; private set; }
            public HttpResponseMessage? Response { get; set; }
            public Exception? ExceptionToThrow { get; set; }

            protected override System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
            {
                LastRequest = request;
                LastRequestBody = request.Content == null
                    ? string.Empty
                    : request.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                if (ExceptionToThrow != null)
                {
                    return System.Threading.Tasks.Task.FromException<HttpResponseMessage>(ExceptionToThrow);
                }

                var response = Response ?? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"number\":\"INV-2026-0001\"}", Encoding.UTF8, "application/json")
                };

                return System.Threading.Tasks.Task.FromResult(response);
            }
        }

        private sealed class StubHttpClientFactory : IHttpClientFactory
        {
            private readonly HttpClient _client;

            public StubHttpClientFactory(HttpClient client)
            {
                _client = client;
            }

            public HttpClient CreateClient(string name) => _client;
        }

        private sealed class ThrowingHttpContent : HttpContent
        {
            protected override System.Threading.Tasks.Task SerializeToStreamAsync(
                System.IO.Stream stream,
                System.Net.TransportContext? context) =>
                System.Threading.Tasks.Task.FromException(new HttpRequestException("synthetic body read failure"));

            protected override bool TryComputeLength(out long length)
            {
                length = 0;
                return false;
            }
        }

        private sealed class CancellationAwareSlowHttpContent : HttpContent
        {
            private readonly System.Threading.Tasks.TaskCompletionSource _release =
                new(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);

            public bool CancellationObserved { get; private set; }
            public bool ReleasedWithoutCancellation { get; private set; }

            public void ReleaseWithoutCancellation()
            {
                ReleasedWithoutCancellation = true;
                _release.TrySetResult();
            }

            protected override System.Threading.Tasks.Task SerializeToStreamAsync(
                System.IO.Stream stream,
                System.Net.TransportContext? context) =>
                SerializeToStreamAsync(stream, context, System.Threading.CancellationToken.None);

            protected override async System.Threading.Tasks.Task SerializeToStreamAsync(
                System.IO.Stream stream,
                System.Net.TransportContext? context,
                System.Threading.CancellationToken cancellationToken)
            {
                try
                {
                    await _release.Task.WaitAsync(cancellationToken);
                    await stream.WriteAsync(Encoding.UTF8.GetBytes("{}"), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    CancellationObserved = true;
                    throw;
                }
            }

            protected override bool TryComputeLength(out long length)
            {
                length = 2;
                return true;
            }
        }
    }
}
