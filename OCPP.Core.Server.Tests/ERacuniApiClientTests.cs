using System;
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
        }

        private static ERacuniApiClient CreateClient(RecordingHttpMessageHandler handler)
        {
            return new ERacuniApiClient(
                new StubHttpClientFactory(new HttpClient(handler)),
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

            protected override System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
            {
                LastRequest = request;
                LastRequestBody = request.Content == null
                    ? string.Empty
                    : request.Content.ReadAsStringAsync().GetAwaiter().GetResult();

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
    }
}
