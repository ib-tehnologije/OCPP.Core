using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
        public void LookupSalesInvoiceByApiTransactionId_ReturnsFoundOnlyForOneExactMatch()
        {
            var handler = new RecordingHttpMessageHandler
            {
                Response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"status\":\"ok\",\"result\":[{\"apiTransactionId\":\"exact-ref\",\"documentId\":\"doc-1\",\"number\":\"INV-1\"},{\"apiTransactionId\":\"another-ref\",\"documentId\":\"doc-2\"}]}",
                        Encoding.UTF8,
                        "application/json")
                }
            };
            var client = CreateClient(handler);

            var result = client.LookupSalesInvoiceByApiTransactionId(new ERacuniApiRequestEnvelope
            {
                Username = "api-user",
                SecretKey = "secret-1234",
                Token = "token-9876",
                Method = "SalesInvoiceList",
                Parameters = new ERacuniSalesInvoiceLookupParameters { ApiTransactionId = "exact-ref" }
            });

            Assert.Equal(ERacuniInvoiceLookupOutcome.Found, result.Outcome);
            Assert.True(result.Diagnostics.RequestAttempted);
            Assert.Equal(ERacuniInvoiceLookupFailureCategory.None, result.Diagnostics.FailureCategory);
            Assert.Equal(200, result.Diagnostics.HttpStatusCode);
            Assert.Equal(ERacuniInvoiceLookupResponseShape.ResultArray, result.Diagnostics.ResponseShape);
            Assert.Equal("doc-1", result.ProviderResult!.ParsedBody!["documentId"]?.ToString());
            Assert.Contains("\"method\":\"SalesInvoiceList\"", handler.LastRequestBody!);
            Assert.Contains("\"apiTransactionId\":\"exact-ref\"", handler.LastRequestBody!);
        }

        [Fact]
        public void LookupSalesInvoiceByApiTransactionId_ReturnsUnknown_ForDuplicateExactMatches()
        {
            var handler = new RecordingHttpMessageHandler
            {
                Response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "[{\"apiTransactionId\":\"same-ref\",\"documentId\":\"doc-1\"},{\"apiTransactionId\":\"same-ref\",\"documentId\":\"doc-2\"}]",
                        Encoding.UTF8,
                        "application/json")
                }
            };
            var client = CreateClient(handler);

            var result = client.LookupSalesInvoiceByApiTransactionId(new ERacuniApiRequestEnvelope
            {
                Username = "api-user",
                SecretKey = "secret-1234",
                Token = "token-9876",
                Method = "SalesInvoiceList",
                Parameters = new ERacuniSalesInvoiceLookupParameters { ApiTransactionId = "same-ref" }
            });

            Assert.Equal(ERacuniInvoiceLookupOutcome.Unknown, result.Outcome);
            Assert.True(result.Diagnostics.RequestAttempted);
            Assert.Equal(ERacuniInvoiceLookupFailureCategory.DuplicateMatch, result.Diagnostics.FailureCategory);
            Assert.Equal(200, result.Diagnostics.HttpStatusCode);
            Assert.Equal(ERacuniInvoiceLookupResponseShape.JsonArray, result.Diagnostics.ResponseShape);
        }

        [Theory]
        [InlineData("[{\"apiTransactionId\":\"another-ref\",\"documentId\":\"doc-1\"}]")]
        [InlineData("{\"status\":\"ok\",\"result\":[{\"apiTransactionId\":\"another-ref\",\"documentId\":\"doc-1\"}]}")]
        public void LookupSalesInvoiceByApiTransactionId_ReturnsUnknown_ForNonEmptyUnmatchedResults(string body)
        {
            var handler = new RecordingHttpMessageHandler
            {
                Response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                }
            };
            var client = CreateClient(handler);

            var result = client.LookupSalesInvoiceByApiTransactionId(new ERacuniApiRequestEnvelope
            {
                Username = "api-user",
                SecretKey = "secret-1234",
                Token = "token-9876",
                Method = "SalesInvoiceList",
                Parameters = new ERacuniSalesInvoiceLookupParameters { ApiTransactionId = "exact-ref" }
            });

            Assert.Equal(ERacuniInvoiceLookupOutcome.Unknown, result.Outcome);
            Assert.True(result.Diagnostics.RequestAttempted);
            Assert.Equal(ERacuniInvoiceLookupFailureCategory.UnrecognizedResponse, result.Diagnostics.FailureCategory);
            Assert.Equal(200, result.Diagnostics.HttpStatusCode);
        }

        [Theory]
        [InlineData("[]")]
        [InlineData("{\"status\":\"ok\",\"result\":[]}")]
        public void LookupSalesInvoiceByApiTransactionId_ReturnsNotFound_OnlyForRecognizedEmptyResults(string body)
        {
            var handler = new RecordingHttpMessageHandler
            {
                Response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                }
            };
            var client = CreateClient(handler);

            var result = client.LookupSalesInvoiceByApiTransactionId(new ERacuniApiRequestEnvelope
            {
                Username = "api-user",
                SecretKey = "secret-1234",
                Token = "token-9876",
                Method = "SalesInvoiceList",
                Parameters = new ERacuniSalesInvoiceLookupParameters { ApiTransactionId = "exact-ref" }
            });

            Assert.Equal(ERacuniInvoiceLookupOutcome.NotFound, result.Outcome);
            Assert.True(result.Diagnostics.RequestAttempted);
            Assert.Equal(ERacuniInvoiceLookupFailureCategory.None, result.Diagnostics.FailureCategory);
            Assert.Equal(200, result.Diagnostics.HttpStatusCode);
        }

        [Fact]
        public void LookupSalesInvoiceByApiTransactionId_ReturnsStructuredPreflightFailureWithoutSending()
        {
            var handler = new RecordingHttpMessageHandler();
            var client = CreateClient(handler);

            var result = client.LookupSalesInvoiceByApiTransactionId(new ERacuniApiRequestEnvelope
            {
                Method = "SalesInvoiceList",
                Parameters = new ERacuniSalesInvoiceLookupParameters { ApiTransactionId = "exact-ref" }
            });

            Assert.Equal(ERacuniInvoiceLookupOutcome.Unknown, result.Outcome);
            Assert.False(result.Diagnostics.RequestAttempted);
            Assert.Equal(ERacuniInvoiceLookupFailureCategory.Configuration, result.Diagnostics.FailureCategory);
            Assert.Null(result.Diagnostics.HttpStatusCode);
            Assert.Equal(ERacuniInvoiceLookupResponseShape.NotAvailable, result.Diagnostics.ResponseShape);
            Assert.Null(handler.LastRequest);
            Assert.DoesNotContain("credential", result.Error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void LookupSalesInvoiceByApiTransactionId_ReturnsSanitizedTransportFailureAfterAttempt()
        {
            var handler = new RecordingHttpMessageHandler
            {
                ExceptionToThrow = new HttpRequestException("synthetic-private-transport-detail")
            };
            var client = CreateClient(handler);

            var result = client.LookupSalesInvoiceByApiTransactionId(CreateLookupRequest());

            Assert.Equal(ERacuniInvoiceLookupOutcome.Unknown, result.Outcome);
            Assert.True(result.Diagnostics.RequestAttempted);
            Assert.Equal(ERacuniInvoiceLookupFailureCategory.Transport, result.Diagnostics.FailureCategory);
            Assert.Null(result.Diagnostics.HttpStatusCode);
            Assert.Equal(ERacuniInvoiceLookupResponseShape.NotAvailable, result.Diagnostics.ResponseShape);
            Assert.DoesNotContain("synthetic-private", result.Error, StringComparison.Ordinal);
        }

        [Fact]
        public void LookupSalesInvoiceByApiTransactionId_ReturnsStructuredHttpFailureWithoutBody()
        {
            var handler = new RecordingHttpMessageHandler
            {
                Response = new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("{\"private\":\"provider-detail\"}", Encoding.UTF8, "application/json")
                }
            };
            var client = CreateClient(handler);

            var result = client.LookupSalesInvoiceByApiTransactionId(CreateLookupRequest());

            Assert.Equal(ERacuniInvoiceLookupOutcome.Unknown, result.Outcome);
            Assert.True(result.Diagnostics.RequestAttempted);
            Assert.Equal(ERacuniInvoiceLookupFailureCategory.HttpStatus, result.Diagnostics.FailureCategory);
            Assert.Equal(401, result.Diagnostics.HttpStatusCode);
            Assert.Equal(ERacuniInvoiceLookupResponseShape.JsonObject, result.Diagnostics.ResponseShape);
            Assert.DoesNotContain("provider-detail", result.Error, StringComparison.Ordinal);
        }

        [Fact]
        public void LookupSalesInvoiceByApiTransactionId_ReturnsStructuredNonJsonFailureWithoutBody()
        {
            var handler = new RecordingHttpMessageHandler
            {
                Response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("private non-json provider payload", Encoding.UTF8, "text/plain")
                }
            };
            var client = CreateClient(handler);

            var result = client.LookupSalesInvoiceByApiTransactionId(CreateLookupRequest());

            Assert.Equal(ERacuniInvoiceLookupOutcome.Unknown, result.Outcome);
            Assert.True(result.Diagnostics.RequestAttempted);
            Assert.Equal(ERacuniInvoiceLookupFailureCategory.NonJsonResponse, result.Diagnostics.FailureCategory);
            Assert.Equal(200, result.Diagnostics.HttpStatusCode);
            Assert.Equal(ERacuniInvoiceLookupResponseShape.NonJson, result.Diagnostics.ResponseShape);
            Assert.DoesNotContain("private non-json", result.Error, StringComparison.Ordinal);
        }

        [Fact]
        public void LookupSalesInvoiceByApiTransactionId_PreservesStatusWhenResponseBodyReadFails()
        {
            var handler = new RecordingHttpMessageHandler
            {
                Response = new HttpResponseMessage(HttpStatusCode.BadGateway)
                {
                    Content = new ThrowingHttpContent()
                }
            };
            var client = CreateClient(handler);

            var result = client.LookupSalesInvoiceByApiTransactionId(CreateLookupRequest());

            Assert.Equal(ERacuniInvoiceLookupOutcome.Unknown, result.Outcome);
            Assert.True(result.Diagnostics.RequestAttempted);
            Assert.Equal(ERacuniInvoiceLookupFailureCategory.Transport, result.Diagnostics.FailureCategory);
            Assert.Equal(502, result.Diagnostics.HttpStatusCode);
            Assert.Equal(ERacuniInvoiceLookupResponseShape.NotAvailable, result.Diagnostics.ResponseShape);
        }

        [Fact]
        public void LookupSalesInvoiceByApiTransactionId_BoundsResponseBodyReadWithHttpClientTimeout()
        {
            var content = new CancellationAwareSlowHttpContent();
            var handler = new RecordingHttpMessageHandler
            {
                Response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = content
                }
            };
            var client = CreateClient(handler, TimeSpan.FromMilliseconds(100));
            var stopwatch = Stopwatch.StartNew();

            var result = client.LookupSalesInvoiceByApiTransactionId(CreateLookupRequest());

            stopwatch.Stop();
            Assert.Equal(ERacuniInvoiceLookupOutcome.Unknown, result.Outcome);
            Assert.Equal(ERacuniInvoiceLookupFailureCategory.Transport, result.Diagnostics.FailureCategory);
            Assert.Equal(200, result.Diagnostics.HttpStatusCode);
            Assert.True(content.CancellationObserved);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"Lookup took {stopwatch.Elapsed}.");
        }

        private static ERacuniApiRequestEnvelope CreateLookupRequest() => new()
        {
            Username = "api-user",
            SecretKey = "secret-1234",
            Token = "token-9876",
            Method = "SalesInvoiceList",
            Parameters = new ERacuniSalesInvoiceLookupParameters { ApiTransactionId = "exact-ref" }
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
            public bool CancellationObserved { get; private set; }

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
                    await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
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
